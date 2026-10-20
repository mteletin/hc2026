using Hc2000.Asm;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Video;
using System.Buffers.Binary;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class ConsoleCodeAcceptanceTests
{
    [Fact]
    public void GuestComSendsDocumentedControlAndEscapeBytesThroughCpmConout()
    {
        var root = StorageAssetTests.Root();
        var assemblyPath = Path.Combine(root, "tests/assets/cpm/console-codes.asm");
        var assembled = Assembler.Assemble(assemblyPath);
        Assert.True(assembled.Success, string.Join("; ", assembled.Diagnostics.Select(d => d.Message)));
        var com = assembled.ToBinary();

        // Put the test COM on a disposable copy of the canonical boot disk; CP/M loads it
        // through its real directory/FDC path when the guest types CONSOLE at A>.
        var sourceDisk = Dsk.Load(File.ReadAllBytes(Path.Combine(root, "disks/cpm/CPM22-HC.DSK")));
        CpmFs.Open(sourceDisk).Put("CONSOLE.COM", com);
        var evidenceDirectory = Path.Combine(root, "temp");
        Directory.CreateDirectory(evidenceDirectory);
        File.WriteAllBytes(Path.Combine(evidenceDirectory, "wi-118-step5-console.com"), com);
        File.WriteAllBytes(Path.Combine(evidenceDirectory, "wi-118-step5-console-cpm.dsk"), sourceDisk.Save());
        var diskPath = Path.Combine(Path.GetTempPath(), $"hc2000-step5-{Guid.NewGuid():N}.dsk");
        File.WriteAllBytes(diskPath, sourceDisk.Save());

        try
        {
            var boot = new MachineBootAcceptanceTests.BootHarness(
                MachineBootAcceptanceTests.BootHarness.Config.ForCpmWarmBoot("hc2000-v2", diskPath));
            Assert.True(MachineBootAcceptanceTests.BootHarness.ReachesEditor(boot, maxFrames: 1000),
                "BASIC did not reach the editor before the physical warm-boot command.");
            for (var frame = 0; frame < 30; frame++) boot.RunFrame();

            // Execute PRINT USR 14446 through the existing physical-key path, exactly as
            // MachineBootAcceptanceTests.CpmWarmBootToPromptWithinTenEmulatedSeconds does.
            boot.TypeKey(Hc2000.Machine.Input.HcKey.P);
            boot.TypeKey(Hc2000.Machine.Input.HcKey.Space);
            boot.TypeExtendedChord();
            boot.TypeKey(Hc2000.Machine.Input.HcKey.L);
            boot.TypeKey(Hc2000.Machine.Input.HcKey.Space);
            boot.TypeKey(Hc2000.Machine.Input.HcKey.D1);
            boot.TypeKey(Hc2000.Machine.Input.HcKey.D4);
            boot.TypeKey(Hc2000.Machine.Input.HcKey.D4);
            boot.TypeKey(Hc2000.Machine.Input.HcKey.D4);
            boot.TypeKey(Hc2000.Machine.Input.HcKey.D6);
            boot.TypeKey(Hc2000.Machine.Input.HcKey.Enter);
            boot.ArmConsoleHook();

            var prompt = false;
            for (var frame = 0; frame < 2000; frame++)
            {
                boot.RunFrame();
                if (boot.PhysicalBitmapShowsAPrompt()) { prompt = true; break; }
            }
            Assert.True(prompt, "CP/M did not reach A> on the modified canonical disk.");

            boot.TypeString("CONSOLE");
            var outputStart = boot.ConsoleOutput.Count;
            boot.TypeKey(Hc2000.Machine.Input.HcKey.Enter);
            VideoFrame settledFrame = boot.RunFrame();
            for (var frame = 1; frame < 1000; frame++) settledFrame = boot.RunFrame();

            var expected = new byte[]
            {
                0x18, (byte)'H', (byte)'C', (byte)'2', (byte)'0', (byte)'0', (byte)'0', (byte)' ',
                (byte)'C', (byte)'O', (byte)'N', (byte)'S', (byte)'O', (byte)'L', (byte)'E', (byte)' ',
                (byte)'C', (byte)'O', (byte)'D', (byte)'E', (byte)' ', (byte)'T', (byte)'E', (byte)'S',
                (byte)'T', 0x0D, 0x0A,
                0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x18, 0x1A, 0x1B, 0x1E,
                0x1B, 0x13, 0x1B, 0x1A, 0x1B, (byte)'A', 0x22, 0x24, 0x1B, (byte)'B',
                0x1B, (byte)'Y', 0x1B, (byte)'I', 0x03, 0x1B, (byte)'P', 0x05,
                0x1B, (byte)'Q', 0x06, 0x1B, (byte)'R', 0x17,
                0x1B, (byte)'I', 0x0B, 0x1B, (byte)'P', 0x0D,
                0x1B, (byte)'Q', 0x0E, 0x1B, (byte)'R', 0x3F,
                0x1B, (byte)'j', 0x1B, (byte)'k', 0x1B, (byte)'p', 0x1B, (byte)'q',
                0x1B, (byte)'r', 0x1B, (byte)'s', 0x1B, (byte)'t', 0x1B, (byte)'u',
                0x1B, (byte)'Z', 0xC1, 0x7F, 0x00, 0x01,
            };
            var output = boot.ConsoleOutput.Skip(outputStart).ToArray();
            Assert.True(ContainsSubsequence(output, expected),
                $"CP/M CONOUT did not receive the assembled code stream. Captured {output.Length} bytes: " +
                Convert.ToHexString(output));
            Console.WriteLine($"Step5 console output (hex): {Convert.ToHexString(output)}");
            File.WriteAllBytes(Path.Combine(evidenceDirectory, "wi-118-step5-console-output.bin"), output);
            var evidencePath = Path.Combine(evidenceDirectory, "wi-118-step5-console.bmp");
            WriteBmp(evidencePath, settledFrame);
            Console.WriteLine($"Step5 frame: {settledFrame.Width}x{settledFrame.Height}; " +
                              $"sha256={boot.FrameHash(settledFrame)}; capture={evidencePath}");
        }
        finally
        {
            File.Delete(diskPath);
        }
    }

    private static bool ContainsSubsequence(byte[] source, byte[] expected)
    {
        for (var start = 0; start <= source.Length - expected.Length; start++)
            if (source.AsSpan(start, expected.Length).SequenceEqual(expected)) return true;
        return false;
    }

    private static void WriteBmp(string path, VideoFrame frame)
    {
        var pixelBytes = checked(frame.Width * frame.Height * 4);
        var bmp = new byte[54 + pixelBytes];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), frame.Width);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), -frame.Height);
        BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(28), 32);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(34), pixelBytes);
        var pixels = frame.Pixels.Span;
        for (var i = 0; i < pixels.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(54 + i * 4), pixels[i]);
        File.WriteAllBytes(path, bmp);
    }
}
