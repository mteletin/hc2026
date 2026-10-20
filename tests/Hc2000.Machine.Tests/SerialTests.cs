using Hc2000.Machine.Serial;
using Hc2000.Machine.Timing;
using Xunit;

namespace Hc2000.Machine.Tests;

public class SerialTests
{
    [Fact]
    public void SerialNetworkPortsExist() =>
        Assert.NotNull(typeof(DeviceScheduler).Assembly.GetType("Hc2000.Machine.Serial.SerialNetworkPorts"));

    [Fact]
    public void DisconnectedPortsHaveNoPeerOrHostDependency()
    {
        var scheduler = new DeviceScheduler();
        var ports = new SerialNetworkPorts(scheduler);
        Assert.Equal(0xff, ports.ReadF7(0));
        Assert.Equal(0xf7, ports.ReadEf(0));
        ports.WriteEf(0x20, 17);
        Assert.True(ports.WaitAsserted);
        ports.WriteEf(0xdf, 19);
        Assert.False(ports.WaitAsserted);
        Assert.Empty(scheduler.PendingEvents);
    }

    [Fact]
    public void F7AndEfBitMappingsAndSameTickInputPrecedence()
    {
        var scheduler = new DeviceScheduler();
        var serial = new InMemoryLinePeer(scheduler, 4);
        var network = new InMemoryLinePeer(scheduler, 5);
        var ports = new SerialNetworkPorts(scheduler, serial, network);
        serial.QueueInput(100, false, true);
        network.QueueInput(100, false);
        Assert.Equal(0xff, ports.ReadF7(99));
        Assert.Equal(0x7e, ports.ReadF7(100));
        Assert.Equal(0xff, ports.ReadEf(100));
        ports.WriteF7(0xff, 100);
        ports.WriteEf(0x30, 100);
        Assert.Equal(new PeerOutput(100, true, true), serial.Outputs[^1]);
        Assert.Equal(new PeerOutput(100, true, true), network.Outputs[^1]);
        Assert.True(ports.WaitAsserted);
        // EF bit0 is not TX. Unused latch bits must not alter logical lines.
        ports.WriteEf(0x31, 101);
        Assert.Equal(100, serial.Outputs[^1].MasterTick);
        ports.WriteF7(0xfe, 102);
        Assert.Equal(new PeerOutput(102, false, true), serial.Outputs[^1]);
        Assert.Equal(new PeerOutput(102, false, true), network.Outputs[^1]);
        ports.Reset(103);
        Assert.False(ports.WaitAsserted);
        Assert.Equal(103, scheduler.Now);
        Assert.Equal(0, ports.F7Latch);
        Assert.Equal(0, ports.EfLatch);
        Assert.Equal(new PeerOutput(103, false, false), serial.Outputs[^1]);
    }

    [Fact]
    public void RawPeerResetCancelsFutureInputsWithoutResettingEpoch()
    {
        var scheduler = new DeviceScheduler();
        var peer = new InMemoryLinePeer(scheduler, 4);
        peer.QueueInput(100, false, true);
        scheduler.AdvanceTo(50);
        peer.Reset();
        Assert.Empty(scheduler.PendingEvents);
        scheduler.AdvanceTo(100);
        Assert.True(peer.Rx);
        Assert.False(peer.Dtr);
        Assert.Throws<ArgumentOutOfRangeException>(() => peer.QueueInput(100, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => peer.QueueInput(99, false));
    }

    [Fact]
    public void GuestBitBangedTxDecodesAllByteValuesAtCentres()
    {
        var scheduler = new DeviceScheduler();
        var peer = new InMemorySerialPeer(scheduler, 4, 10_000, 10);
        var ports = new SerialNetworkPorts(scheduler, peer);
        ports.WriteF7(1, 0);
        for (int value = 0; value < 256; value++)
        {
            long start = 10 + value * 10_000L;
            GuestFrame(ports, (byte)value, start);
            scheduler.AdvanceTo(start + 9_500);
            Assert.Equal(new SerialByte(start + 9_500, (byte)value), peer.Received[value]);
        }
        Assert.Equal(256, peer.Received.Count);
        Assert.Equal(0, peer.FramingErrors);
    }

    [Fact]
    public void FalseStartAndBadStopAreNotBytesAndResetCancelsSamples()
    {
        var scheduler = new DeviceScheduler();
        var peer = new InMemorySerialPeer(scheduler, 4, 10_000, 10);
        var ports = new SerialNetworkPorts(scheduler, peer);
        ports.WriteF7(1, 0);
        ports.WriteF7(0, 10);
        ports.WriteF7(1, 20); // false start
        scheduler.AdvanceTo(510);
        Assert.Empty(peer.Received);
        GuestFrame(ports, 0x55, 1000, stop: false);
        scheduler.AdvanceTo(10_500);
        Assert.Empty(peer.Received);
        Assert.Equal(1, peer.FramingErrors);
        ports.WriteF7(1, 11_000);
        ports.WriteF7(0, 12_000);
        peer.Reset();
        Assert.Empty(scheduler.PendingEvents);
        scheduler.AdvanceTo(30_000);
        Assert.Empty(peer.Received);
        Assert.Equal(0, peer.FramingErrors);
        Assert.True(peer.Rx);
    }

    [Fact]
    public void CtsGatesNewFramesButDoesNotTruncateActiveByte()
    {
        var scheduler = new DeviceScheduler();
        var peer = new InMemorySerialPeer(scheduler, 4, 10_000, 10);
        var ports = new SerialNetworkPorts(scheduler, peer);
        ports.WriteF7(1, 0);
        peer.QueueReceive(0x55);
        peer.QueueReceive(0xaa);
        scheduler.AdvanceTo(100);
        Assert.True(peer.Rx);
        Assert.Empty(scheduler.PendingEvents);
        ports.WriteEf(0x10, 100);
        Assert.Equal(101, scheduler.PendingEvents.Single().DueTick);
        Assert.Equal(0x7f, ports.ReadF7(101)); // start bit
        ports.WriteEf(0, 102);
        for (int bit = 0; bit < 8; bit++)
            Assert.Equal((0x55 & (1 << bit)) != 0, (ports.ReadF7(1601 + bit * 1000) & 0x80) != 0);
        Assert.True((ports.ReadF7(9601) & 0x80) != 0); // stop bit
        scheduler.AdvanceTo(10_101);
        Assert.Equal(1, peer.QueuedBytes);
        Assert.Empty(scheduler.PendingEvents);
        ports.WriteEf(0x10, 20_000);
        Assert.Equal(0, peer.QueuedBytes);
        Assert.False((ports.ReadF7(20_001) & 0x80) != 0);
        Assert.Equal(0xff, ports.ReadEf(20_001)); // attached peer DTR
    }

    [Fact]
    public void LongRunIncomingFramesRetainRationalPhaseWithoutCpuClock()
    {
        const long hz = 17_734_475, baud = 9600;
        var scheduler = new DeviceScheduler();
        var peer = new InMemorySerialPeer(scheduler, 4, hz, baud);
        var ports = new SerialNetworkPorts(scheduler, peer);
        ports.WriteF7(1, 0);
        for (int n = 0; n < 10_000; n++) peer.QueueReceive((byte)n);
        ports.WriteEf(0x10, 0);
        for (long n = 0; n < 10_000; n++)
        {
            for (int bit = 0; bit < 10; bit++)
            {
                long halfPeriods = 2 * (n * 10 + bit) + 1;
                long centre = 1 + (halfPeriods * hz + 2 * baud - 1) / (2 * baud);
                bool high = (ports.ReadF7(centre) & 0x80) != 0;
                Assert.Equal(bit == 9 || (bit > 0 && (((byte)n & (1 << (bit - 1))) != 0)), high);
            }
        }
        long end = 1 + (100_000L * hz + baud - 1) / baud;
        scheduler.AdvanceTo(end);
        Assert.Empty(scheduler.PendingEvents);
        Assert.Equal(0, peer.QueuedBytes);
        Assert.True(peer.Rx);
        Assert.InRange((end - 1) * baud - 100_000L * hz, 0, baud - 1);
    }

    [Fact]
    public void PeerTraceIsInvariantToAdvanceChunkingAndCpuDivider()
    {
        static (byte, byte, PeerOutput[]) Run(long p, long q, bool chunked)
        {
            var scheduler = new DeviceScheduler();
            var peer = new InMemoryLinePeer(scheduler, 4);
            var ports = new SerialNetworkPorts(scheduler, peer, peer);
            var cpuTime = new CpuDeviceTimeAdapter(scheduler, new CpuTimeMapper(p, q));
            peer.QueueInput(300, false, true);
            if (chunked)
                for (long n = 0; n < 100 * q; n++) cpuTime.SyncCpuTime(n);
            cpuTime.SyncCpuTime(100 * q); // both dividers reach 500 master ticks
            ports.WriteF7(1, scheduler.Now);
            ports.WriteEf(0x30, scheduler.Now);
            return (ports.ReadF7(500), ports.ReadEf(500), peer.Outputs.ToArray());
        }
        var a = Run(5, 1, false);
        var b = Run(5, 2, true);
        Assert.Equal(a.Item1, b.Item1);
        Assert.Equal(a.Item2, b.Item2);
        Assert.Equal(a.Item3, b.Item3);
    }

    [Fact]
    public void InvalidRateAndUnsynchronizedWritesFailExplicitly()
    {
        var scheduler = new DeviceScheduler();
        Assert.Throws<ArgumentOutOfRangeException>(() => new InMemorySerialPeer(scheduler, 1, 100, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new InMemorySerialPeer(scheduler, 1, 100, 51));
        var peer = new InMemorySerialPeer(scheduler, 1, 100, 10);
        Assert.Throws<ArgumentException>(() => peer.Write(false, false, 10));
        scheduler.AdvanceTo(long.MaxValue);
        peer.Write(true, true, long.MaxValue);
        Assert.Throws<OverflowException>(() => peer.QueueReceive(1));
    }

    private static void GuestFrame(SerialNetworkPorts ports, byte value, long start, bool stop = true)
    {
        ports.WriteF7(0, start);
        for (int bit = 0; bit < 8; bit++)
            ports.WriteF7((byte)((value >> bit) & 1), start + (bit + 1) * 1000);
        ports.WriteF7(stop ? (byte)1 : (byte)0, start + 9000);
    }
}
