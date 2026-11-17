using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Hc2000.Asm;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Tape;

// WorkstationSystemDiskBuilder: produces the shipped combined-workstation A: base disk
// (CPM22-HC-ram-paged-v2-workstation.DSK): the real m-paged-v2 producer output with the
// current guest tools placed from source (HDINST, MFMT, XJOB), the stock DRVSEL/RAMSTAT from
// the shipped utilities volume, and the UNBOUND TAPEINIT template. The runtime preparation
// (LaunchStartup --workstation) binds that template against the machine's mounted live tape
// session and stages the session-specific image; nothing this tool emits is a bound disk.
//
// Usage: WorkstationSystemDiskBuilder <repository-root> <utilities-dsk> <output-directory>

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: WorkstationSystemDiskBuilder <repository-root> <utilities-dsk> <output-directory>");
    return 2;
}
var root = Path.GetFullPath(args[0]);
var utilitiesPath = Path.GetFullPath(args[1]);
var output = Path.GetFullPath(args[2]);
Directory.CreateDirectory(output);
var commit = Run("git", "-C", root, "rev-parse", "HEAD").Trim();
// Provenance honesty: the artifact's sources are the WORKTREE, not the commit, when the
// tree is dirty; say so in the manifest instead of naming a commit the tree is not.
var dirty = Run("git", "-C", root, "status", "--porcelain").Trim().Length > 0;
var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
var baseDir = Path.Combine(Path.GetTempPath(), "hc2000-workstation-base-" + Guid.NewGuid().ToString("N"));
try
{
    // The real m-paged-v2 producer (the same tool call the builder lane uses); it requires
    // a new private output directory.
    Run("dotnet", Path.Combine(root, "tools/UpgradeBiosBuilder/bin", configuration, "net10.0/UpgradeBiosBuilder.dll"),
        "m-paged-v2", baseDir);

    // The guest tools are assembled from this branch's source, never taken from a prebuilt
    // image: the image copies are build-time artifacts and go stale. They build as hc build builds
    // them for the extended machine, with the shared libraries on the search path
    // (hc2000-asmlib-build-system); a source without library includes assembles as before.
    byte[] Assemble(string relativePath)
    {
        var path = Path.Combine(root, relativePath);
        var assembly = Assembler.AssembleText(File.ReadAllText(path), LibraryTargets.Options(path, LibraryTargets.Find("hc2000-v2")!), path);
        if (!assembly.Success)
            throw new InvalidOperationException(string.Join("; ", assembly.Diagnostics.Select(d => d.Message)));
        return assembly.ToBinary();
    }
    var hddInst = Assemble("src-library/extended/bios-upgrade/hdd/hdd.init.asm");
    var mfmt = Assemble("src-library/extended/tools/mfmt/mfmt.asm");
    var xjob = Assemble("src-library/extended/tools/xjob/xjob.asm");
    var template = Assemble("src-library/extended/bios-upgrade/tape/tape.init.asm");
    TapeGuestInstaller.ValidateTemplate(template);

    var utilityBytes = File.ReadAllBytes(utilitiesPath);
    var utilities = CpmFs.Open(Dsk.Load(utilityBytes));

    var baseBytes = File.ReadAllBytes(Path.Combine(baseDir, "CPM22-HC-ram-paged-v2-upgrade.DSK"));
    var media = Dsk.Load(baseBytes);
    var files = CpmFs.Open(media);
    // A: carries only what the session needs: the BDOS installer, the UNBOUND TAPEINIT and the
    // job/diagnostic tools. The stock CP/M disk's read-only STAT stays on A: untouched; the
    // full factory set (including STAT) is on the C: tools volume and runs from there.
    files.Put("HDINST.COM", hddInst);
    files.Put("MFMT.COM", mfmt);
    files.Put("XJOB.COM", xjob);
    files.Put("DRVSEL.COM", utilities.Get("DRVSEL.COM"));
    files.Put("RAMSTAT.COM", utilities.Get("RAMSTAT.COM"));
    files.Put("TAPEINIT.COM", template);
    var disk = media.Save();

    var diskName = WorkstationMedia.BaseDiskName;
    var diskPath = Path.Combine(output, diskName);
    File.WriteAllBytes(diskPath, disk);
    static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    var receipt = new Dictionary<string, string>
    {
        ["artifact"] = diskName,
        ["state"] = "unbound TAPEINIT template; session-bound at launch by --workstation",
        ["git"] = commit,
        ["gitDirty"] = dirty ? "true" : "false",
        ["base"] = "CPM22-HC-ram-paged-v2-upgrade.DSK (UpgradeBiosBuilder m-paged-v2)",
        ["baseSha256"] = Hash(baseBytes),
        ["utilities"] = Path.GetFileName(utilitiesPath),
        ["utilitiesSha256"] = Hash(utilityBytes),
        ["hddInstCom"] = Hash(hddInst),
        ["mfmtCom"] = Hash(mfmt),
        ["xjobCom"] = Hash(xjob),
        ["tapeInitTemplateSha256"] = Hash(template),
        ["diskSha256"] = Hash(disk),
    };
    var receiptPath = Path.Combine(output, "workstation-manifest.json");
    File.WriteAllText(receiptPath, JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
    foreach (var (key, value) in receipt) Console.WriteLine($"{key} {value}");
    Console.WriteLine($"DISK {disk.Length} {Hash(disk)} {diskPath}");
    return 0;
}
finally
{
    if (Directory.Exists(baseDir)) Directory.Delete(baseDir, true);
}

static string Run(string executable, params string[] arguments)
{
    using var process = new Process
    {
        StartInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true
        }
    };
    foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
    process.Start();
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    if (!process.WaitForExit(240000))
    {
        process.Kill(true);
        process.WaitForExit();
        throw new TimeoutException($"{executable} timed out.");
    }
    var text = stdout.GetAwaiter().GetResult();
    var error = stderr.GetAwaiter().GetResult();
    if (process.ExitCode != 0) throw new InvalidOperationException($"{executable}: exit {process.ExitCode}\n{text}\n{error}");
    return text;
}
