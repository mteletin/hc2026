using System.Diagnostics;
using Hc2000.HostFiles;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: UtilitiesDiskBuilder <repository-root> <output-directory>");
    return 2;
}
var root = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
var commit = Run("git", "rev-parse", "HEAD").Trim();
// UpgradeBiosBuilder requires a new private output directory. Keep its temporary
// companions out of the installer; only its qualified boot disk is bundled.
var boot = Path.Combine(Path.GetTempPath(), "hc2000-utilities-boot-" + Guid.NewGuid().ToString("N"));
try
{
    var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
    Run("dotnet", Path.Combine(root, "tools/UpgradeBiosBuilder/bin", configuration, "net10.0/UpgradeBiosBuilder.dll"), "m-paged", boot);
    CpmUtilitiesImages.GenerateInto(root, output, boot, commit);
    Console.WriteLine($"UTILITIES source={commit} manifest={CpmUtilitiesImages.ManifestName} boot-profile=hc2000-v2-paged-m-v1");
    return 0;
}
finally
{
    if (Directory.Exists(boot)) Directory.Delete(boot, true);
}

string Run(string executable, params string[] arguments)
{
    using var process = new Process
    {
        StartInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = root, UseShellExecute = false,
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
        throw new TimeoutException(executable + " utilities build timed out.");
    }
    var text = stdout.GetAwaiter().GetResult();
    var error = stderr.GetAwaiter().GetResult();
    if (process.ExitCode != 0) throw new InvalidOperationException($"{executable}: exit {process.ExitCode}\n{text}\n{error}");
    return text;
}
