using Hc2000.Asm;
using Hc2000.Machine.Storage;
using Hc2000.Z80;
using Xunit;
using Cpu = Hc2000.Z80.Z80;

namespace Hc2000.Machine.Tests;

// Firmware-only tests, not the pending genuine receiver/BDOS/ACK witness.
public sealed class HddGuestBiosTests
{
    private static AssemblyResult Assemble(string name)
    {
        var path = Path.Combine(StorageAssetTests.Root(), "bios/src/hc2000/upgrade", name + ".asm");
        var result = Assembler.AssembleText(File.ReadAllText(path), file: path);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        return result;
    }

    private static Bus SuppliedSystem()
    {
        var disk = Dsk.Load(File.ReadAllBytes(Path.Combine(StorageAssetTests.Root(), "disks/cpm/CPM22-HC.DSK")));
        var bus = new Bus();
        (int Side, int Sector)[] sectors = [(0,3),(0,5),(0,7),(0,9),(0,2),(0,4),(0,6),(0,8),
            (1,1),(1,3),(1,5),(1,7),(1,9),(1,2),(1,4)];
        for (var i = 0; i < sectors.Length; i++)
        {
            var (side, sector) = sectors[i];
            disk.Tracks.Single(t => t.Cylinder == 0 && t.Side == side).Sectors.Single(s => s.R == sector)
                .Data.CopyTo(bus.Bytes, 0xBD00 + i * 512);
        }
        bus.Bytes[5] = 0xC3;
        bus.Word(6, 0xC506);
        return bus;
    }

    private static Cpu Run(Bus bus, ushort entry, bool interrupts = true, byte function = 0, ushort fcb = 0, bool allowPorts = false)
    {
        var cpu = new Cpu(bus);
        cpu.R.PC = entry;
        cpu.R.SP = 0x8000;
        cpu.R.C = function;
        cpu.R.DE = fcb;
        cpu.R.IFF1 = interrupts;
        cpu.R.IFF2 = interrupts;
        bus.Word(0x8000, 0x80);
        for (var n = 0; cpu.R.PC != 0x80 && n < 200000; n++) cpu.Step();
        Assert.Equal((ushort)0x80, cpu.R.PC);
        if (!allowPorts) Assert.Empty(bus.WritesToPorts);
        return cpu;
    }

    [Fact]
    public void ComPhysicalAndLogicalMapsRespectAllReservations()
    {
        var init = Assemble("hdd.init");
        var swap = Assemble("hdd.swap");
        Assert.Equal(0x100, init.Segments.Min(s => s.Address));
        Assert.InRange(init.ToBinary().Length, 1, 0x8F00);
        Assert.Equal(0x9000, init.Symbols["HddResidentStart"]);
        Assert.Equal(0x9040, init.Symbols["HddCodeStart"]);
        Assert.True(init.Symbols["HddCodeEnd"] <= 0xA000);
        Assert.Equal(0xB500, init.Symbols["HddSwitch"]);
        Assert.True(init.Symbols["HddSwitchEnd"] <= 0xB800);
        Assert.Equal(0xB300, init.Symbols["HddStackTop"]);
        Assert.Equal(0x100, swap.Segments.Min(s => s.Address));
        Assert.InRange(swap.ToBinary().Length, 1, 128);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InstallerUsesRealSuppliedGuardsAndPreservesOldAssetsAndInterruptState(bool interrupts)
    {
        var init = Assemble("hdd.init");
        var bus = SuppliedSystem();
        init.ToBinary().CopyTo(bus.Bytes, 0x100);
        var high = bus.Bytes.AsSpan(0xBD00).ToArray();
        bus.SimulateNewProfile = true;
        var cpu = Run(bus, 0x100, interrupts, allowPorts: true);
        Assert.Equal(0, cpu.R.A);
        Assert.Equal(interrupts, cpu.R.IFF1);
        Assert.Equal(0x9006, bus.Word(6));
        Assert.Equal(0x9000, bus.Word(0xC3F5));
        Assert.Equal(init.Symbols["HddEof"], bus.Word(0xC407));
        Assert.Equal(new byte[] { 0xCD, 0, 1 }, bus.Bytes.AsSpan(0xC45C, 3).ToArray());
        Assert.Equal(high.AsSpan(0xD333 - 0xBD00, 0xD465 - 0xD333).ToArray(), bus.Bytes.AsSpan(0xD333, 0xD465 - 0xD333).ToArray());
        Assert.Equal(new byte[] { 0x40,0,5,0x1F,1,0xFF,7,0xFF,3,0xFF,0,0,1,0,0 }, bus.Bytes.AsSpan(0xA840,15).ToArray());
        Assert.All(bus.Bytes.AsSpan(0xAC00, 1280).ToArray(), b => Assert.Equal(0, b));
        Assert.All(bus.Bytes.AsSpan(0xB800, 1280).ToArray(), b => Assert.Equal(0xA5, b));
        // Reinstall must refuse without zeroing evidence.
        bus.Bytes[0xAC00] = 1;
        bus.WritesToPorts.Clear();
        Assert.Equal(1, Run(bus, 0x100).R.A);
        Assert.Equal(1, bus.Bytes[0xAC00]);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(0xC3F4)]
    [InlineData(0xC3F5)]
    [InlineData(0xC406)]
    [InlineData(0xC407)]
    [InlineData(0xD465)]
    [InlineData(0xD466)]
    [InlineData(0xD467)]
    [InlineData(0xD303)]
    [InlineData(0xD304)]
    [InlineData(0xD328)]
    public void MismatchedOriginalByteRefusesBeforeAnyPatchOrReservationWrite(int address)
    {
        var bus = SuppliedSystem();
        Assemble("hdd.init").ToBinary().CopyTo(bus.Bytes, 0x100);
        bus.Bytes[address] ^= 1;
        var before = bus.Bytes.ToArray();
        Assert.Equal(1, Run(bus, 0x100).R.A);
        Assert.Equal(before.AsSpan(0x9000).ToArray(), bus.Bytes.AsSpan(0x9000).ToArray());
        Assert.Equal(before.AsSpan(0, 8).ToArray(), bus.Bytes.AsSpan(0, 8).ToArray());
    }

    [Fact]
    public void OldProfileCannotInstallOrExposeGuestVectors()
    {
        var bus = SuppliedSystem();
        Assemble("hdd.init").ToBinary().CopyTo(bus.Bytes, 0x100);
        var high = bus.Bytes.AsSpan(0xBD00).ToArray();
        Assert.Equal(1, Run(bus, 0x100, allowPorts: true).R.A);
        Assert.Equal(0xC506, bus.Word(6));
        Assert.Equal(high, bus.Bytes.AsSpan(0xBD00).ToArray());
        Assert.Contains(bus.WritesToPorts, p => p.Port == 0x89);
    }

    [Fact]
    public void SwapCommandWithoutInstalledGateRefusesWithoutPorts()
    {
        var bus = SuppliedSystem();
        Assemble("hdd.swap").ToBinary().CopyTo(bus.Bytes, 0x100);
        Assert.Equal(1, Run(bus, 0x100).R.A);
    }

    // Deliberately a synthetic BDOS return seam for census unit tests only;
    // this is NOT the required original-BDOS/native receiver positive proof.
    private static Bus CensusFixture()
    {
        var bus = SuppliedSystem();
        Assemble("hdd.init").ToBinary().CopyTo(bus.Bytes, 0x100);
        bus.SimulateNewProfile = true;
        Assert.Equal(0, Run(bus, 0x100, allowPorts: true).R.A);
        bus.WritesToPorts.Clear();
        var stub = Assembler.AssembleText("""
            ORG 0xC506
            LD A,C
            CP 25
            JR NZ,User
            LD A,2
            RET
            User:
            CP 32
            JR NZ,Operation
            XOR A
            RET
            Operation:
            LD HL,0xABF0
            INC (HL)
            LD A,(0xABF1)
            LD HL,0x1234
            RET
            """);
        Assert.True(stub.Success);
        stub.ToBinary().CopyTo(bus.Bytes, 0xC506);
        bus.Bytes[0x2000] = 3;
        "FILE    DAT"u8.CopyTo(bus.Bytes.AsSpan(0x2001));
        return bus;
    }

    [Fact]
    public void CensusRecordsOnlySuccessfulOpensAndClearsOnlySuccessfulMatchingCloses()
    {
        var bus = CensusFixture();
        bus.Bytes[0xABF1] = 0xFF;
        Assert.Equal(0xFF, Run(bus, 0x9006, function: 15, fcb: 0x2000).R.A);
        Assert.Equal(0, bus.Bytes[0xAC00]);
        bus.Bytes[0xABF1] = 0;
        var opened = Run(bus, 0x9006, function: 15, fcb: 0x2000);
        Assert.Equal(0, opened.R.A);
        Assert.Equal(0x1234, opened.R.HL);
        Assert.Equal(0x2000, opened.R.DE);
        Assert.Equal(1, bus.Bytes[0xAC00]);
        Assert.Equal(new byte[] { 1, 2, 0, 0, 0, 0x20 }, bus.Bytes.AsSpan(0xAC00, 6).ToArray());
        bus.Bytes[0xABF1] = 0xFF;
        Run(bus, 0x9006, function: 16, fcb: 0x2000);
        Assert.Equal(1, bus.Bytes[0xAC00]);
        bus.Bytes[0x2001] = (byte)'X';
        var count = bus.Bytes[0xABF0];
        Assert.Equal(0xFF, Run(bus, 0x9006, function: 16, fcb: 0x2000).R.A);
        Assert.Equal(count, bus.Bytes[0xABF0]);
        Assert.Equal(1, bus.Bytes[0xAC00]);
        bus.Bytes[0x2001] = (byte)'F';
        bus.Bytes[0xABF1] = 0;
        Assert.Equal(0, Run(bus, 0x9006, function: 16, fcb: 0x2000).R.A);
        Assert.Equal(0, bus.Bytes[0xAC00]);
        Assert.Equal(0, bus.Bytes[0xAB06]);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(33)]
    [InlineData(34)]
    [InlineData(40)]
    public void UntrackedAffectedDataCallsRefuseBeforeOriginalBdos(byte function)
    {
        var bus = CensusFixture();
        Assert.Equal(0xFF, Run(bus, 0x9006, function: function, fcb: 0x2000).R.A);
        Assert.Equal(0, bus.Bytes[0xABF0]);
    }

    [Fact]
    public void PerDriveCensusCapacityAndAliasRefusalNeverEraseExistingEvidence()
    {
        var bus = CensusFixture();
        for (var i = 0; i < 16; i++)
        {
            var address = (ushort)(0x2000 + 64 * i);
            bus.Bytes[address] = 3;
            System.Text.Encoding.ASCII.GetBytes($"F{i:D7}DAT").CopyTo(bus.Bytes, address + 1);
            Assert.Equal(0, Run(bus, 0x9006, function: 22, fcb: address).R.A);
        }
        var evidence = bus.Bytes.AsSpan(0xAC00, 320).ToArray();
        bus.Bytes[0x2400] = 3;
        "OVERFLOWDAT"u8.CopyTo(bus.Bytes.AsSpan(0x2401));
        var calls = bus.Bytes[0xABF0];
        Assert.Equal(0xFF, Run(bus, 0x9006, function: 15, fcb: 0x2400).R.A);
        Assert.Equal(calls, bus.Bytes[0xABF0]);
        Assert.Equal(evidence, bus.Bytes.AsSpan(0xAC00, 320).ToArray());
    }

    [Fact]
    public void WriteThroughPreservesNeighborsAndFailedDirtyOldGenerationUntilRealRetry()
    {
        var bus = CensusFixture();
        var symbols = Assemble("hdd.init").Symbols;
        bus.WritesToPorts.Clear();
        bus.Commands.Clear();
        bus.Bytes[0xAB00] = 2;
        bus.Word(0xD875, 0x3000);
        bus.Word(0xAB03, 1); // second128-byte record in physical LBA0
        Array.Fill(bus.Bytes, (byte)0x5A, 0x3000, 128);
        var before = bus.Sector.ToArray();
        bus.WriteStatus = 8;
        Assert.Equal(8, Run(bus, (ushort)symbols["HddWrite"], allowPorts: true).R.A);
        Assert.Equal(before, bus.Sector);
        Assert.Equal(1, bus.Bytes[0xAB08]);
        Assert.Equal(before.AsSpan(0,128).ToArray(), bus.Bytes.AsSpan(0xA900,128).ToArray());
        Assert.All(bus.Bytes.AsSpan(0xA980,128).ToArray(), b => Assert.Equal(0x5A,b));
        Assert.Equal(before.AsSpan(256).ToArray(), bus.Bytes.AsSpan(0xAA00,256).ToArray());
        // An attempted eviction after a generation change must still flush
        // the OLD tag, refuse, and never IDENTIFY/retry onto the replacement.
        bus.Word(0xA8E0, 99);
        bus.Word(0xAB03, 4);
        bus.WriteStatus = 0x0D;
        Assert.Equal(0x0D, Run(bus, (ushort)symbols["HddRead"], allowPorts: true).R.A);
        var failed = bus.Commands.Last();
        Assert.Equal(2, failed[1]);
        Assert.Equal(3u, BitConverter.ToUInt32(failed,12));
        Assert.Equal(0u, BitConverter.ToUInt32(failed,4));
        Assert.DoesNotContain(bus.Commands, c => c[1] == 3);
        Assert.Equal(1, bus.Bytes[0xAB08]);
        bus.WriteStatus = 0;
        bus.Word(0xA8E0, 3);
        Assert.Equal(0, Run(bus, (ushort)symbols["HddRead"], allowPorts: true).R.A);
        Assert.Equal(0, bus.Bytes[0xAB08]);
        Assert.All(bus.Sector.AsSpan(128,128).ToArray(), b => Assert.Equal(0x5A,b));
        Assert.Equal(before.AsSpan(0,128).ToArray(), bus.Sector.AsSpan(0,128).ToArray());
        Assert.Equal(before.AsSpan(256).ToArray(), bus.Sector.AsSpan(256).ToArray());
    }

    [Theory]
    [InlineData(0x7F)]
    [InlineData(0x8F81)]
    [InlineData(0x9000)]
    [InlineData(0xAC00)]
    [InlineData(0xB100)]
    [InlineData(0xFFFF)]
    public void LogicalDmaAliasRefusesWithoutPortOrCacheMutation(int address)
    {
        var bus = CensusFixture();
        bus.Bytes[0xAB00] = 2;
        bus.Word(0xD875,address);
        bus.WritesToPorts.Clear();
        var cache = bus.Bytes.AsSpan(0xA900,512).ToArray();
        Assert.Equal(1,Run(bus,(ushort)Assemble("hdd.init").Symbols["HddRead"]).R.A);
        Assert.Equal(cache,bus.Bytes.AsSpan(0xA900,512).ToArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IdleQueryNeverProducesAckAndRestoresPrivateStackAndCallerInterruptState(bool iff)
    {
        var bus = CensusFixture();
        var cpu = Run(bus,0x9009,interrupts:iff,allowPorts:true);
        Assert.Equal(1,cpu.R.A);
        Assert.Equal(iff,cpu.R.IFF1);
        Assert.Equal((ushort)0x8002,cpu.R.SP);
        Assert.Equal(0xA5,bus.Bytes[0xB100]);
        Assert.DoesNotContain(bus.Commands,c => c[1] == 5);
    }

    [Fact]
    public void ChangedChallengeAfterResetRefusesBeforeAck()
    {
        var bus = CensusFixture();
        var count = 0;
        bus.QueryReply = () =>
        {
            var bytes = new byte[512];
            bytes[0] = 1; bytes[1] = 1; bytes[2] = 2; bytes[4] = 4;
            bytes[8] = (byte)++count; bytes[16] = 0x11; bytes[32] = 3;
            return bytes;
        };
        bus.Commands.Clear();
        Assert.Equal(1,Run(bus,0x9009,allowPorts:true).R.A);
        Assert.Equal(2,count);
        Assert.Equal(1,bus.Bytes[0xABF0]); // synthetic original BDOS37 return seam
        Assert.DoesNotContain(bus.Commands,c => c[1] == 5);
        Assert.Equal(0,bus.Bytes[0xAB06]);
    }

    private sealed class Bus : IBus
    {
        public byte[] Bytes { get; } = new byte[65536];
        public List<(ushort Port, byte Value)> WritesToPorts { get; } = [];
        public int Word(int address) => Bytes[address] | Bytes[address + 1] << 8;
        public void Word(int address, int value) { Bytes[address] = (byte)value; Bytes[address + 1] = (byte)(value >> 8); }
        public byte Read(ushort address) => Bytes[address];
        public void Write(ushort address, byte value) => Bytes[address] = value;
        // Transcript seam ONLY, not the production receiver or ACK witness.
        public bool SimulateNewProfile { get; set; }
        public Func<byte[]>? QueryReply { get; set; }
        public byte WriteStatus { get; set; }
        public byte[] Sector { get; } = Enumerable.Range(0,512).Select(i => (byte)i).ToArray();
        public List<byte[]> Commands { get; } = [];
        private int pointer;
        private byte status = 1;
        public byte In(ushort port) => status;
        public void Out(ushort port, byte value)
        {
            WritesToPorts.Add((port, value));
            if (port == 0x81) pointer = (pointer & 0xFF00) | value;
            if (port == 0x83) pointer = (pointer & 0xFF) | value << 8;
            if (port != 0x89) return;
            status = 1;
            Commands.Add(Bytes.AsSpan(pointer,16).ToArray());
            if (!SimulateNewProfile) return;
            var dma = Word(pointer + 8);
            var unit = Bytes[pointer + 2];
            if (Bytes[pointer + 1] == 4)
            {
                Array.Clear(Bytes, dma, 512);
                Bytes[dma] = 1;
                QueryReply?.Invoke().CopyTo(Bytes,dma);
                status = 0;
            }
            if (Bytes[pointer + 1] == 1)
            {
                Sector.CopyTo(Bytes,dma);
                status = 0;
            }
            if (Bytes[pointer + 1] == 2)
            {
                status = WriteStatus;
                if (status == 0) Bytes.AsSpan(dma,512).CopyTo(Sector);
            }
            if (Bytes[pointer + 1] == 3)
            {
                Array.Clear(Bytes, dma, 16);
                Word(dma, 0x4000);
                Word(dma + 4, unit + 1);
                Bytes[dma + 8] = 3;
                status = 0;
            }
        }
        public byte IntVector() => 0xFF;
        public void Contend(ushort address, int tstates) { }
    }
}
