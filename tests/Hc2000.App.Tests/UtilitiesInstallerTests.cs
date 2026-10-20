using System.Diagnostics;
using System.Text;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class UtilitiesInstallerTests
{
    private static readonly string[] Required = ["CPM22-HC.DSK", "HC2000-UTIL.DSK", "HC2000-UTIL-8M.DSK", "CPM22-HC-ram-paged-upgrade.DSK", "utilities-manifest.json", "m-v1-source-fit.txt"];

    [Fact]
    public void LinuxInstallerRequiresGeneratedPayloadAndPreservesEveryUserDiskAcrossUpgradeAndUninstall()
    {
        if (!OperatingSystem.IsLinux()) return;
        var repo = Repository();
        var root = Path.Combine(Path.GetTempPath(), "hc-utilities-install-" + Guid.NewGuid().ToString("N"));
        var payload = Path.Combine(root, "payload");
        var home = Path.Combine(root, "home");
        var prefix = Path.Combine(home, "program");
        try
        {
            var disks = Path.Combine(payload, "disks");
            Directory.CreateDirectory(disks);
            foreach (var name in Required)
                File.Copy(name == "CPM22-HC.DSK" ? Path.Combine(repo, "disks/cpm", name) : Path.Combine(AppContext.BaseDirectory, "disks", name), Path.Combine(disks, name));
            Directory.CreateDirectory(Path.Combine(payload, "roms"));
            File.WriteAllText(Path.Combine(payload, "roms/test.rom"), "private fixture");
            File.WriteAllText(Path.Combine(payload, "VERSION"), "test-utilities");
            File.WriteAllText(Path.Combine(payload, "icon.png"), "fixture icon");
            File.Copy(Path.Combine(repo, "deploy/emu/install-ubuntu.sh"), Path.Combine(payload, "install-ubuntu.sh"));
            var native = Path.Combine(payload, "app/runtimes/linux-x64/native");
            Directory.CreateDirectory(native);
            foreach (var name in new[] { "libSDL2-2.0.so.0", "SDL2-MANIFEST.sha256", "SDL2-LICENSE.txt", "SDL2-config.h", "SDL2-configure.log", "SDL2-provenance.txt" })
                File.Copy(Path.Combine(repo, "deploy/emu/sdl2/linux-x64", name), Path.Combine(native, name));
            File.WriteAllText(Path.Combine(payload, "app/Hc2000.App"), "fixture (not launched)");
            foreach (var name in Required)
            {
                var path = Path.Combine(disks, name);
                File.Move(path, path + ".missing");
                var refused = Run(payload, home, "--prefix", prefix);
                Assert.NotEqual(0, refused.Exit);
                Assert.Contains("payload lacks disks/" + name, refused.Text);
                Assert.False(Directory.Exists(prefix));
                Assert.False(Directory.Exists(Path.Combine(home, ".config")));
                File.Move(path + ".missing", path);
            }
            var initial = Run(payload, home, "--prefix", prefix);
            Assert.True(initial.Exit == 0, initial.Text);
            var installed = Path.Combine(home, ".config/hc2000/disks");
            foreach (var name in Required)
                Assert.Equal(File.ReadAllBytes(Path.Combine(disks, name)), File.ReadAllBytes(Path.Combine(installed, name)));
            var expected = new Dictionary<string, byte[]>();
            foreach (var name in Required)
            {
                expected[name] = Encoding.ASCII.GetBytes("owner-edited " + name);
                File.WriteAllBytes(Path.Combine(installed, name), expected[name]);
            }
            var upgrade = Run(payload, home);
            Assert.True(upgrade.Exit == 0, upgrade.Text);
            foreach (var name in Required) Assert.Equal(expected[name], File.ReadAllBytes(Path.Combine(installed, name)));
            var uninstall = Run(payload, home, "--uninstall");
            Assert.True(uninstall.Exit == 0, uninstall.Text);
            Assert.False(Directory.Exists(prefix));
            foreach (var name in Required) Assert.Equal(expected[name], File.ReadAllBytes(Path.Combine(installed, name)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void WindowsInstallerRequiresExplicitArtifactsInsideTheCopyIfAbsentBlock()
    {
        var script = File.ReadAllText(Path.Combine(Repository(), "deploy/emu/setup.nsi"));
        var first = script.IndexOf("File \"${PAYLOAD_DIR}\\disks\\CPM22-HC.DSK\"", StringComparison.Ordinal);
        var start = script.LastIndexOf("SetOverwrite off", first, StringComparison.Ordinal);
        var end = script.IndexOf("SetOverwrite on", first, StringComparison.Ordinal);
        var block = script[start..end];
        foreach (var name in Required) Assert.Contains("File \"${PAYLOAD_DIR}\\disks\\" + name + "\"", block);
        Assert.DoesNotContain("/nonfatal", block);
    }

    private static (int Exit, string Text) Run(string payload, string home, params string[] args)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("bash")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        } };
        process.StartInfo.Environment["HOME"] = home;
        process.StartInfo.ArgumentList.Add(Path.Combine(payload, "install-ubuntu.sh"));
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000)) { process.Kill(true); throw new TimeoutException("Installer fixture timed out."); }
        return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }
    private static string Repository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Hc2000.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository not found.");
    }
}
