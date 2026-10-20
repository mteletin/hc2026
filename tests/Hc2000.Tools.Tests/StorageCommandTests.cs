using Hc2000.Machine.Storage;
using Xunit;

namespace Hc2000.Tools.Tests;

public sealed class StorageCommandTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "hc-storage-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable nativeScope;
    public StorageCommandTests()
    {
        Directory.CreateDirectory(directory);
        var domain = Path.Combine(directory, "admission");
        Directory.CreateDirectory(domain);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(domain, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        nativeScope = Hc2000.HostFiles.NativeImageAdmission.EnterPrivateTestDomain(domain, OperatingSystem.IsLinux() ? Hc2000.HostFiles.ImageAdmissionPlatformMode.GuardedLinux : Hc2000.HostFiles.ImageAdmissionPlatformMode.LegacyOnly);
    }
    public void Dispose() { nativeScope.Dispose(); Directory.Delete(directory, true); }
    private string PathIn(string name) => Path.Combine(directory,name);
    internal static string Root()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName,"Hc2000.slnx"))) root=root.Parent;
        return root?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
    private static (int Code,string Output,string Error) Run(string command,params string[] args)
    {
        var output=new StringWriter(); var error=new StringWriter(); var code=StorageCommand.Run(command,args,output,error);
        return (code,output.ToString(),error.ToString());
    }
    [StorageAssetFact("disks/cpm/CPM1.DSK")]
    public void DiskInfoIsImplemented()
        => Assert.Equal(0,Program.Dispatch("dsk",["info",Path.Combine(Root(),"disks/cpm/CPM1.DSK")]));
    [StorageDisksFact]
    public void EveryDiskCanBeInspectedAndListedThroughCli()
    {
        var paths=Directory.GetFiles(Path.Combine(Root(),"disks"),"*",SearchOption.AllDirectories)
            .Where(p=>Path.GetExtension(p).Equals(".dsk",StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.Equal(77,paths.Length);
        foreach(var path in paths)
        {
            var info=Run("dsk","info",path); Assert.Equal(0,info.Code); Assert.Contains("Extended:",info.Output);
            var listing=Run("fs","ls",path); Assert.True(listing.Code==0,path+": "+listing.Error);
            Assert.Contains("format ",listing.Output); Assert.DoesNotContain("hc fs:",listing.Error);
        }
    }
    [Theory]
    [InlineData("cpm-hc40")] [InlineData("cpm-hc80")] [InlineData("hcfs40")] [InlineData("hcfs80")]
    public void FsNewPutGetOverwriteAndRemove(string format)
    {
        var disk=PathIn("test.DSK"); var source=PathIn("source.bin"); var target=PathIn("out.bin");
        var bytes=Enumerable.Range(0,32769).Select(i=>(byte)i).ToArray(); File.WriteAllBytes(source,bytes);
        Assert.Equal(0,Run("fs","new",disk,"--format",format).Code);
        Assert.Equal(0,Run("fs","put",disk,source,"--name","TEST.BIN","--user","3").Code);
        Assert.Equal(0,Run("fs","get",disk,"TEST.BIN","--user","3","-o",target).Code);
        var actual=File.ReadAllBytes(target); Assert.Equal(bytes,actual[..bytes.Length]);
        Assert.Equal(format.StartsWith("hcfs")?bytes.Length:32896,actual.Length);
        if(format.StartsWith("cpm")) Assert.All(actual[bytes.Length..],b=>Assert.Equal(0x1a,b));
        Assert.Contains("TEST.BIN",Run("fs","ls",disk,"--user","3").Output);
        Assert.DoesNotContain("TEST.BIN",Run("fs","ls",disk,"--user","0").Output);
        File.WriteAllBytes(source,[1,2]); Assert.Equal(0,Run("fs","put",disk,source,"--name","TEST.BIN","--user","3").Code);
        Assert.Equal(0,Run("fs","rm",disk,"TEST.BIN","--user","3").Code);
        Assert.Equal(1,Run("fs","get",disk,"TEST.BIN","--user","3","-o",target).Code);
    }
    [Fact]
    public void RawConversionRequiresGeometryAndKeepsLogicalSectors()
    {
        var disk=PathIn("disk.DSK"); var raw=PathIn("disk.img"); var back=PathIn("back.DSK");
        File.WriteAllBytes(disk,Dsk.Create(new(2,2,3,256)).Save());
        Assert.Equal(0,Run("dsk","convert",disk,"-o",raw).Code);
        Assert.Equal(1,Run("dsk","convert",raw,"-o",back).Code); Assert.False(File.Exists(back));
        Assert.Equal(0,Run("dsk","convert",raw,"-o",back,"--geometry","2,2,3,256","--container","standard").Code);
        Assert.Equal(DiskFormat.Standard,Dsk.Load(File.ReadAllBytes(back)).Format);
        Assert.Equal(File.ReadAllBytes(raw),Dsk.Load(File.ReadAllBytes(back)).ToRaw());
    }
    [Fact]
    public void TapeDiskTransferKeepsProgramDataVariablesAndAutostart()
    {
        var tape=PathIn("in.tap"); var disk=PathIn("disk.DSK"); var tapeOut=PathIn("out.tap"); var host=PathIn("program.bin");
        var bytes=new byte[]{1,2,0,0x1a,5}; File.WriteAllBytes(host,bytes);
        Assert.Equal(0,Run("tap","put",tape,host,"--name","program","--type","0","--start","10","--variables","3").Code);
        Assert.Contains("program",Run("tap","ls",tape).Output);
        Assert.Equal(0,Run("fs","new",disk,"--format","hcfs40").Code);
        Assert.Equal(0,Run("fs","put",disk,tape,"--tap-name","program").Code);
        Assert.Equal(0,Run("fs","get",disk,"program","-o",tapeOut,"--tap").Code);
        var file=Tap.Load(File.ReadAllBytes(tapeOut)).Get("program"); Assert.Equal(bytes,file.Data); Assert.Equal(10,file.Start); Assert.Equal(3,file.Variables);
        var second=PathIn("second.tap"); Assert.Equal(0,Run("tap","put",second,disk,"--disk-name","program").Code);
        Assert.Equal(0,Run("tap","get",second,"program","-o",host).Code); Assert.Equal(bytes,File.ReadAllBytes(host));
    }
    [StorageAssetFact("roms/hc2000/eprom-dump-hc2k-2/M27C512.bin")]
    public void RomCommandsUseManifestAndDocumentedSlicesWithoutOverwrites()
    {
        var input=Path.Combine(Root(),"roms/hc2000/eprom-dump-hc2k-2/M27C512.bin"); var output=PathIn("slices");
        Assert.Equal(0,Run("rom","id",input).Code);
        Assert.Equal(0,Run("rom","split",input,"-o",output).Code); Assert.Equal(5,Directory.GetFiles(output).Length);
        Assert.Equal(1,Run("rom","split",input,"-o",output).Code);
        var bytes=File.ReadAllBytes(Path.Combine(output,"basic-v2.bin")); Assert.Equal(File.ReadAllBytes(Path.Combine(Root(),"roms/hc2000/v2/basic.rom")),bytes);
    }
    [Theory]
    [InlineData("--unknown","x")] [InlineData("--format")] [InlineData("--geometry","bad")]
    [InlineData("--user","32")] [InlineData("--user","-1")] [InlineData("--user","x")]
    [InlineData("--type","256")] [InlineData("--name","TOO-LONG-FILENAME")]
    public void BadPutOptionsNeverChangeImage(params string[] options)
    {
        var disk=PathIn("disk.DSK"); var input=PathIn("file.bin"); File.WriteAllBytes(input,[1,2]);
        Assert.Equal(0,Run("fs","new",disk,"--format","hcfs40").Code); var before=File.ReadAllBytes(disk);
        var result=Run("fs",new[]{"put",disk,input}.Concat(options).ToArray()); Assert.Equal(1,result.Code); Assert.Contains("hc fs:",result.Error);
        Assert.Equal(before,File.ReadAllBytes(disk)); Assert.Empty(Directory.GetFiles(directory,"*.hc-*"));
    }
    [Fact]
    public void FailedGetsAndNewDoNotOverwriteAndCorruptHeaderIsDiagnosed()
    {
        var disk=PathIn("disk.DSK"); var output=PathIn("out.bin");
        Assert.Equal(0,Run("fs","new",disk,"--format","hcfs40").Code); var before=File.ReadAllBytes(disk);
        Assert.Equal(1,Run("fs","new",disk,"--format","cpm-hc40").Code); Assert.Equal(before,File.ReadAllBytes(disk));
        File.WriteAllBytes(output,[9]); Assert.Equal(1,Run("fs","get",disk,"missing","-o",output).Code); Assert.Equal(new byte[]{9},File.ReadAllBytes(output));
        var image=Dsk.Load(before); var fs=CpmFs.Open(image); fs.Put("bad",new HcFileHeader(3,65535,0,0,0).Encode()); File.WriteAllBytes(disk,image.Save());
        var listing=Run("fs","ls",disk); Assert.Equal(0,listing.Code); Assert.Contains("invalid-header",listing.Output); Assert.Contains("payload length",listing.Error);
        Assert.Equal(1,Run("fs","get",disk,"bad","-o",output).Code); Assert.Equal(new byte[]{9},File.ReadAllBytes(output));
    }
    [Fact]
    public void ExportCollisionsAreDiagnosedWithoutDroppingRawNamesOrWritingOutput()
    {
        var disk=PathIn("disk.DSK"); var output=PathIn("out.bin"); var image=Dsk.Create(new(40,2,16,256));
        var fs=CpmFs.Open(image); var hc=new HcFs(fs); hc.Put("a/b",[1]); hc.Put("a_b",[2]); fs.Put("empty",[]);
        File.WriteAllBytes(disk,image.Save()); var before=File.ReadAllBytes(disk);
        var listing=Run("fs","ls",disk); Assert.Equal(0,listing.Code); Assert.Contains("Export name collision",listing.Error);
        Assert.Contains("a/b",listing.Error); Assert.Contains("a_b",listing.Error); Assert.Contains("literal=\"a/b        \"",listing.Output);
        Assert.Equal(2,listing.Output.Split('\n').Count(l=>l.StartsWith("0\ta_b\t")));
        Assert.Contains("untyped/headerless",listing.Output); Assert.DoesNotContain("invalid-header",listing.Output);
        var collision=Run("fs","get",disk,"a/b"); Assert.Equal(1,collision.Code); Assert.Contains("collision",collision.Error);
        Assert.Equal(0,Run("fs","get",disk,"a/b","-o",output).Code); Assert.Equal(new byte[]{1},File.ReadAllBytes(output));
        Assert.Equal(1,Run("fs","get",disk,"empty","-o",output).Code); Assert.Equal(new byte[]{1},File.ReadAllBytes(output));
        Assert.Equal(before,File.ReadAllBytes(disk));
    }
}

public sealed class StorageAssetFactAttribute : FactAttribute
{
    public StorageAssetFactAttribute(string relative)
    {
        if (!File.Exists(Path.Combine(StorageCommandTests.Root(), relative))) Skip = "Copyrighted asset is absent: " + relative;
    }
}
public sealed class StorageDisksFactAttribute : FactAttribute
{
    public StorageDisksFactAttribute()
    {
        if (Directory.GetFiles(Path.Combine(StorageCommandTests.Root(), "disks"), "*", SearchOption.AllDirectories)
            .Count(p => Path.GetExtension(p).Equals(".dsk", StringComparison.OrdinalIgnoreCase)) != 77)
            Skip = "Complete copyrighted disk fixture set is absent.";
    }
}
