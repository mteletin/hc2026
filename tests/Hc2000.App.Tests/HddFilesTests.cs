using System.Collections.Immutable;
using Hc2000.App.Media;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class HddFilesTests : IDisposable
{
    private readonly HddColdAdmissionTests.PrivateDomain nativeDomain = new();
    private readonly IDisposable nativeScope;
    public HddFilesTests() => nativeScope = nativeDomain.Enter();
    public void Dispose() { nativeScope.Dispose(); nativeDomain.Dispose(); }
    [Theory]
    [InlineData(0)]
    [InlineData(8_388_607)]
    [InlineData(8_388_609)]
    public async Task WrongSizeRejectedWithoutPadding(int size)
    {
        var path = Path.GetTempFileName();
        try
        {
            using (var file = File.OpenWrite(path)) file.SetLength(size);
            await Assert.ThrowsAsync<InvalidDataException>(() => HddFiles.ReadAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ExactAtomicCopyAndCancelledSavePreservePriorTarget()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hc-hdd-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "copy.raw");
        try
        {
            var bytes = new byte[HddMediaAdapter.ImageLength]; bytes[0] = 0x5a; bytes[^1] = 0xa5;
            await HddFiles.SaveAsync(path, ImmutableArray.CreateRange(bytes));
            Assert.Equal(bytes, (await HddFiles.ReadAsync(path)).ToArray());
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HddFiles.SaveAsync(path,
                ImmutableArray.CreateRange(new byte[bytes.Length]), cancellation.Token));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
