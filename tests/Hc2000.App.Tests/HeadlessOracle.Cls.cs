using Hc2000.App.Media;
using Hc2000.App.Runtime;
using Hc2000.Machine.Input;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Video;
using Xunit;

namespace Hc2000.App.Tests;

public sealed partial class HeadlessOracle
{
    // Reuse the existing identical whole-chip/I04 asset-presence guard.
    [WholeChipMbasicFact]
    public Task OriginalClsOnI04WholeChipClearsAndReturns() => CpmWarmBoot(prove: async (ctx, diskPath) =>
    {
        // Independent oracle: I04 user0/CPM92.TXT, Console Output, control 1A:
        // "Sterge ecran si aduce cursor in coltul stinga sus" (clear/home).
        // CLS entry: LD E,1A; LD C,2; CALL 0005; RET. No arguments.
        // Held I04: logical sectors1..4 -> H0 R3/5/7/9 -> BD00/BF00/C100/C300.
        // Raw135C maps C45C CALL0100; RET resumes C45F, then JP C082 (rawB82).
        // C082 CALL BD98 (raw698) emits CR/LF before A>: final prompt is row1.
        // No title-specific physical-HC transcript or installed-release parity claim.
        var original = await File.ReadAllBytesAsync(diskPath);
        var sourceHash = Hash(original);
        Assert.Equal("ff5a1c541c8aa80778a0f44d3ece398013084aafd9be1d3f32d2f68b774bb008", sourceHash);
        var root = RomSelectionFixture.RepositoryRoot();
        var chipPath = Path.Combine(root, ProfileRomSelection.WholeChipSourcePath);
        var chip = await File.ReadAllBytesAsync(chipPath);
        Assert.Equal(65536, chip.Length);
        Assert.Equal("83e3213b049c5b849ee32e04da901eba06992d5aa1f44f76564f312c79748d2b", Hash(chip));
        var before = await ctx.Bridge.RequestAsync(new MediaCommand(MediaAction.ExportDisk)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(before.Error);
        Assert.Equal(0, before.A.DirtySectors);
        var disk = Dsk.Load(before.Export.AsSpan());
        var fs = CpmFs.Open(disk, "cpm-hc");
        var binary = fs.Get("CLS.COM", 0);
        Assert.Equal(256, binary.Length);
        Assert.Equal("97497a4c5bb02ca743eb42b5e5c3244f862c91eb8eac0b26d87fa3ada19ae949", Hash(binary));
        Assert.Equal(new byte[] { 0x1e, 0x1a, 0x0e, 0x02, 0xcd, 0x05, 0x00, 0xc9 }, binary[..8]);
        var reference = fs.Get("CPM92.TXT", 0);
        Assert.Equal(11520, reference.Length);
        Assert.Equal("2b3bd728eadc8d8715837734644f5737ebdeaeefe16a0522232916f4f6625052", Hash(reference));
        var logical = ReadLogical(disk, fs.Parameters);
        Assert.Equal(ReadLogical(Dsk.Load(original), fs.Parameters), logical);

        VideoFrame? prior = null;
        await WaitFramesKeeping(ctx.Worker, 1, frame => prior = frame);
        Assert.NotNull(prior);
        Assert.Equal(352, prior.Width);
        Assert.Equal(296, prior.Height);
        // Console is 64 four-pixel columns by 24 eight-pixel rows at (48,40).
        // Witness exactly the final blank region: row0, row1 suffix, rows2..23.
        // Exclude row1 cells0..3 only (final prompt and cursor/shared cell).
        var priorPixels = prior.Pixels.ToArray();
        var colors = new Dictionary<uint, int>();
        for (var y = 40; y < 232; y++)
            for (var x = y is >= 48 and < 56 ? 64 : 48; x < 304; x++)
            {
                var color = priorPixels[y * 352 + x];
                colors[color] = colors.GetValueOrDefault(color) + 1;
            }
        var background = colors.MaxBy(pair => pair.Value).Key;
        var nonblank = colors.Where(pair => pair.Key != background).Sum(pair => pair.Value);
        Assert.True(nonblank >= 8, $"CLS precondition unavailable: final blank region has only {nonblank} nonbackground pixels.");

        var start = ctx.Trace.Console.Count;
        var startTick = prior.MasterTick;
        await Tap(ctx, HcKey.C);
        await Tap(ctx, HcKey.L);
        await Tap(ctx, HcKey.S);
        await Tap(ctx, HcKey.Enter);
        VideoFrame? final = null;
        // Four taps request64 frames; this adds120, not a polling-until-pass loop.
        await WaitFramesKeeping(ctx.Worker, 120, frame => final = frame);
        Assert.NotNull(final);
        const long maxCommandTicks = 500L * 355680;
        var elapsedTicks = final.MasterTick - startTick;
        Assert.InRange(elapsedTicks, 1L, maxCommandTicks);
        var raw = ctx.Trace.Console.ToArray()[start..];
        var clear = Array.IndexOf(raw, (byte)0x1a);
        Assert.True(clear >= 0, $"CLS did not emit documented control1A: {Convert.ToHexString(raw)}");
        Assert.Contains("cls", new string(raw[..clear].Select(value => (char)value).ToArray()));
        Assert.Contains("A>", new string(raw[(clear + 1)..].Select(value => (char)value).ToArray()));
        output.WriteLine($"CLS I04 image={sourceHash}; COM={Hash(binary)}; CPM92={Hash(reference)}; M27C512={Hash(chip)}; DB=none; raw={Convert.ToHexString(raw)}; commandTicks={elapsedTicks}/{maxCommandTicks}; priorInk={nonblank}; frame={final.Width}x{final.Height}@{final.MasterTick}; row0 blank=[48,304)x[40,48), row1 suffix=[64,304)x[48,56), rows2..23 blank=[48,304)x[56,232); logicalBefore={Hash(logical)}.");
        Assert.Equal(352, final.Width);
        Assert.Equal(296, final.Height);
        Assert.True(GlyphMatches(final.Pixels.Span, final.Width, 0, 1,
            new byte[] { 0, 2, 5, 5, 7, 5, 5, 0 }), "Fresh A is not at console cell(0,1).");
        Assert.True(GlyphMatches(final.Pixels.Span, final.Width, 1, 1,
            new byte[] { 0, 0, 4, 2, 1, 2, 4, 0 }), "Fresh > is not at console cell(1,1).");
        var pixels = final.Pixels.ToArray();
        // Exclude only row1 prompt0..1 and cursor2/shared attribute partner3.
        // All of row0 and rows2..23, plus row1 columns4..63, must be blank.
        for (var y = 40; y < 232; y++)
            for (var x = y is >= 48 and < 56 ? 64 : 48; x < 304; x++)
                Assert.True(pixels[y * 352 + x] == background,
                    $"CLS retained ink at published pixel({x},{y}); prior witnessed ink={nonblank}.");

        var after = await ctx.Bridge.RequestAsync(new MediaCommand(MediaAction.ExportDisk)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(after.Error);
        Assert.Equal(0, after.A.DirtySectors);
        var post = Dsk.Load(after.Export.AsSpan());
        Assert.Equal(logical, ReadLogical(post, fs.Parameters));
        Assert.Equal(binary, CpmFs.Open(post, "cpm-hc").Get("CLS.COM", 0));
        Assert.Equal(sourceHash, Hash(await File.ReadAllBytesAsync(diskPath)));
        Assert.Equal(chip, await File.ReadAllBytesAsync(chipPath));
        output.WriteLine($"CLS logicalAfter={Hash(ReadLogical(post, fs.Parameters))}; dirtySectors={after.A.DirtySectors}; original/chip/COM preserved.");
        output.WriteLine("Documented control1A clear/home followed by held CCP CR/LF and row1 prompt; real-HC CLS transcript and installed-release parity remain unverified.");
    });
}
