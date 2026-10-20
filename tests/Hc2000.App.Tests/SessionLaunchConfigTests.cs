using System.Text;
using Hc2000.App;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class SessionLaunchConfigTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "hc2000-session-" + Guid.NewGuid().ToString("N"));
    public SessionLaunchConfigTests() => Directory.CreateDirectory(directory);
    private SessionLoad Parse(string json) => SessionLaunchConfigLoader.Parse(Encoding.UTF8.GetBytes(json),
        Path.Combine(directory, "session.json"));

    [Fact]
    public void MissingImplicitCreatesNothingAndDefaultsOff()
    {
        var loader = new SessionLaunchConfigLoader(configRoots: () => new[] { directory });
        var loaded = loader.Load();
        Assert.Equal(SessionLoadKind.Absent, loaded.Kind);
        Assert.Empty(Directory.GetFiles(directory));
        Assert.Equal(new EffectiveLaunch(null, null, false), SessionLaunchConfig.Resolve(loaded).Launch);
    }

    [Fact]
    public void ExplicitMissingAndCorruptFilesRefuseWithoutChangingBytes()
    {
        var path = Path.Combine(directory, "session.json");
        var loader = new SessionLaunchConfigLoader();
        Assert.Equal(SessionLoadKind.Refused, loader.Load(path).Kind);
        var original = Encoding.UTF8.GetBytes("{\"version\":1,\"disks\":{},\"writeback\":true,\"future\":9}");
        File.WriteAllBytes(path, original);
        var refused = loader.Load(path);
        Assert.Equal(SessionLoadKind.Refused, refused.Kind);
        Assert.Null(refused.File);
        Assert.Null(SessionLaunchConfig.Resolve(refused, cliDiskA: "A:cli.dsk", cliWriteback: true).Launch);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(directory));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"version\":1,\"disks\":{}}")]
    [InlineData("{\"version\":1,\"disks\":{},\"writeback\":null}")]
    [InlineData("{\"version\":1,\"disks\":{},\"writeback\":1}")]
    [InlineData("{\"version\":1,\"disks\":{},\"writeback\":\"false\"}")]
    [InlineData("{\"version\":1,\"disks\":{},\"writeback\":false,\"writeback\":true}")]
    [InlineData("{\"version\":1,\"disks\":{},\"writeback\":false,\"unknown\":0}")]
    [InlineData("{\"version\":1,\"disks\":null,\"writeback\":false}")]
    [InlineData("{\"version\":1,\"disks\":[],\"writeback\":false}")]
    [InlineData("{\"version\":1,\"disks\":{\"A\":\"a\",\"A\":\"b\"},\"writeback\":false}")]
    [InlineData("{\"version\":1,\"disks\":{\"C\":\"c\"},\"writeback\":false}")]
    [InlineData("{\"version\":1,\"disks\":{\"a\":\"a\"},\"writeback\":false}")]
    [InlineData("{\"version\":1,\"disks\":{\"A\":null},\"writeback\":false}")]
    [InlineData("{\"version\":1,\"disks\":{\"A\":1},\"writeback\":false}")]
    [InlineData("{\"version\":1,\"disks\":{\"A\":\"\"},\"writeback\":false}")]
    [InlineData("{\"version\":1,\"disks\":{\"A\":\"   \"},\"writeback\":false}")]
    [InlineData("{\"version\":1,\"disks\":{\"A\":\"a\\u0000b\"},\"writeback\":false}")]
    [InlineData("{\"version\":1,\"disks\":{\"A\":\"a\\u001fb\"},\"writeback\":false}")]
    [InlineData("{\"version\":1,\"disks\":{},\"writeback\":false,}")]
    public void InvalidSchemaHasNoPartialSession(string json)
    {
        var loaded = Parse(json);
        Assert.Equal(SessionLoadKind.Refused, loaded.Kind);
        Assert.Null(loaded.File);
        Assert.NotNull(loaded.Refusal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("1.0")]
    [InlineData("1e0")]
    [InlineData("\"1\"")]
    [InlineData("null")]
    public void VersionIsIntegerOneOnly(string version)
    {
        var json = "{\"version\":" + version + ",\"disks\":{},\"writeback\":false}";
        Assert.Equal(SessionLoadKind.Refused, Parse(json).Kind);
    }

    [Fact]
    public void ValidNestedConfigResolvesPathsWithoutStatingDiskFiles()
    {
        var loaded = Parse("{\"version\":1,\"disks\":{\"A\":\"a.dsk\",\"B\":\"b.dsk\"},\"writeback\":true}");
        Assert.Equal(SessionLoadKind.Loaded, loaded.Kind);
        var result = SessionLaunchConfig.Resolve(loaded).Launch!;
        Assert.Equal("A:" + Path.Combine(directory, "a.dsk"), result.DiskA);
        Assert.Equal("B:" + Path.Combine(directory, "b.dsk"), result.DiskB);
        Assert.True(result.Writeback);
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void CliOverridesOneDriveAndWritebackIsTriState(bool? flag, bool expected)
    {
        var loaded = Parse("{\"version\":1,\"disks\":{\"A\":\"a.dsk\",\"B\":\"b.dsk\"},\"writeback\":true}");
        var result = SessionLaunchConfig.Resolve(loaded, "A:raw-cli.dsk", cliWriteback: flag).Launch!;
        Assert.Equal("A:raw-cli.dsk", result.DiskA);
        Assert.Equal("B:" + Path.Combine(directory, "b.dsk"), result.DiskB);
        Assert.Equal(expected, result.Writeback);
    }

    [Fact]
    public void HelpDoesNotDiscoverRootsReadOrApplyEvenRefusedConfig()
    {
        var reads = 0;
        var roots = 0;
        var loader = new SessionLaunchConfigLoader(_ => { reads++; throw new IOException("Must not read."); },
            () => { roots++; return new[] { directory }; });
        Assert.Equal(SessionLoadKind.Absent, loader.Load("missing.json", help: true).Kind);
        Assert.Equal(0, reads); Assert.Equal(0, roots);
        var refused = new SessionLoad(SessionLoadKind.Refused, Refusal: new("bad config"));
        Assert.Equal(new EffectiveLaunch(null, null, false), SessionLaunchConfig.Resolve(refused, help: true).Launch);
    }

    [Fact]
    public void PathLengthLimitAndEmptyDriveObjectAreIndependentOfMounting()
    {
        string Json(int length) => "{\"version\":1,\"disks\":{\"A\":\"" + new string('a', length)
            + "\"},\"writeback\":false}";
        Assert.Equal(SessionLoadKind.Loaded, Parse(Json(4096)).Kind);
        Assert.Equal(SessionLoadKind.Refused, Parse(Json(4097)).Kind);
        Assert.Equal(new SessionFileV1(1, null, null, false),
            Parse("{\"version\":1,\"disks\":{},\"writeback\":false}").File);
    }

    public void Dispose() => Directory.Delete(directory, true);
}
