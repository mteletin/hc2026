using System.Security.Cryptography;
using Xunit;

namespace Hc2000.Tools.Tests;

public sealed class HddCommandTests : IDisposable
{
    private const int ImageBytes = 8_388_608;
    private const string BlankHash = "2daeb1f36095b44b318410b3f4e8b5d989dcc7bb023d1426c492dab0a3053e74";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "hc-hdd-" + Guid.NewGuid().ToString("N"));
    private readonly NativeImageAdmissionTests.Domain nativeDomain = new();
    private readonly IDisposable nativeScope;
    public HddCommandTests() { Directory.CreateDirectory(directory); nativeScope = nativeDomain.Enter(); }
    public void Dispose() { nativeScope.Dispose(); nativeDomain.Dispose(); Directory.Delete(directory, true); }
    private string PathIn(string name) => Path.Combine(directory, name);
    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter(); var error = new StringWriter();
        var code = StorageCommand.Run("hdd", args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void CreateIsDeterministicFlatZeroImageAndIdentifyIsReadOnly()
    {
        var path = PathIn("blank.img");
        var created = Run("create", path);
        Assert.Equal(0, created.Code); Assert.Empty(created.Error);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(ImageBytes, bytes.Length);
        Assert.True(bytes.All(b => b == 0));
        Assert.Equal(BlankHash, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        var identified = Run("identify", path);
        Assert.Equal(0, identified.Code); Assert.Empty(identified.Error);
        Assert.Equal($"raw-hdd bytes=8388608 sectors=16384 sector-size=512 offset=0 sha256={BlankHash}{Environment.NewLine}", identified.Output);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(directory, "*.hc-*"));
    }

    [Fact]
    public void IdentifyAcceptsNonblankContentAndReportsItsActualIdentity()
    {
        var path = PathIn("data.img"); var bytes = new byte[ImageBytes];
        bytes[0] = 0xe5; bytes[511] = 42; bytes[^1] = 99;
        File.WriteAllBytes(path, bytes);
        var result = Run("identify", path);
        Assert.Equal(0, result.Code);
        Assert.Contains("sha256=" + Convert.ToHexStringLower(SHA256.HashData(bytes)), result.Output);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(0)] [InlineData(512)] [InlineData(ImageBytes - 1)] [InlineData(ImageBytes + 1)]
    public void WrongLengthsAreRejectedWithoutMutation(int length)
    {
        var path = PathIn("wrong.img"); var bytes = new byte[length];
        if (length > 0) bytes[^1] = 27;
        File.WriteAllBytes(path, bytes);
        var result = Run("identify", path);
        Assert.Equal(1, result.Code); Assert.Empty(result.Output);
        Assert.Contains("hc hdd:", result.Error); Assert.Contains("8388608", result.Error);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(directory));
    }

    [Fact]
    public void CreateRefusesExistingPathAndCleansTemporaryFiles()
    {
        var path = PathIn("existing.img"); File.WriteAllBytes(path, [1, 2, 3]);
        var result = Run("create", path);
        Assert.Equal(1, result.Code); Assert.Contains("hc hdd:", result.Error);
        Assert.Empty(result.Output); Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(directory, "*.hc-*"));
        var directoryResult = Run("create", directory);
        Assert.Equal(1, directoryResult.Code);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(directory)!, Path.GetFileName(directory) + ".hc-*"));
    }

    [Theory]
    [InlineData("create", "--format", "raw")]
    [InlineData("identify", "--geometry", "1,1,1,512")]
    [InlineData("create", "--force")]
    [InlineData("create", "extra")]
    [InlineData("format")]
    public void InvalidArgumentsNeverCreateAnImage(string verb, params string[] extra)
    {
        var path = PathIn("bad.img");
        var result = Run(new[] { verb, path }.Concat(extra).ToArray());
        Assert.Equal(1, result.Code); Assert.Contains("hc hdd:", result.Error);
        Assert.Empty(result.Output); Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public void MissingInputAndMissingParentFailCleanly()
    {
        Assert.Equal(1, Run("identify", PathIn("absent.img")).Code);
        Assert.Equal(1, Run("create", PathIn("absent/blank.img")).Code);
        Assert.Equal(1, Run("create").Code);
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public void MainDispatchesHddCaseInsensitively()
    {
        var path = PathIn("dispatch.img");
        Assert.Equal(0, Program.Main(["HDD", "create", path]));
        Assert.Equal(ImageBytes, new FileInfo(path).Length);
    }
}
