using Hc2000.Machine.Storage;
using Xunit;

namespace Hc2000.Tools.Tests;

/// <summary>hc tap create, KB hc2000-media-home-scan-admission-v1 sec 4: a zero-byte blank tape via the
/// shared writer, never a spec-13 guest format.</summary>
public sealed class TapCreateCommandTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "hc-tap-create-" + Guid.NewGuid().ToString("N"));
    private readonly NativeImageAdmissionTests.Domain nativeDomain = new();
    private readonly IDisposable nativeScope;
    public TapCreateCommandTests() { Directory.CreateDirectory(directory); nativeScope = nativeDomain.Enter(); }
    public void Dispose() { nativeScope.Dispose(); nativeDomain.Dispose(); Directory.Delete(directory, true); }
    private string PathIn(string name) => Path.Combine(directory, name);
    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter(); var error = new StringWriter();
        var code = StorageCommand.Run("tap", args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void CreateWritesZeroBytesAndRefusesAnExistingPath()
    {
        var path = PathIn("blank.tap");
        var created = Run("create", path);
        Assert.Equal(0, created.Code); Assert.Empty(created.Error);
        var bytes = File.ReadAllBytes(path);
        Assert.Empty(bytes);
        Assert.Empty(Tap.Load(bytes).List());
        var refused = Run("create", path);
        Assert.Equal(1, refused.Code);
        Assert.Contains("Refusing to replace an existing path with tap create", refused.Error);
        Assert.Empty(File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(directory, "*.hc-*"));
    }

    [Fact]
    public void CreateUsageStaysWithTheOtherTapeVerbs()
    {
        var path = PathIn("x.tap");
        Run("create", path);
        var result = Run("unknown", path);
        Assert.Equal(1, result.Code);
        Assert.Contains("tap supports create/ls/get/put", result.Error);
    }
}
