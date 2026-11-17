using Hc2000.Examples;

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: ExpansionMailboxBuilder <repository> <output-directory> <configuration>");
    return 2;
}
try
{
    ExpansionMailboxAssets.Generate(args[0], args[1], args[2]);
    X2ServiceAssets.Generate(args[0], Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(args[1]))!, "x2-service"), args[2]);
    X2BulkAssets.Generate(args[0], Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(args[1]))!, "x2-bulk"), args[2]);
    X2BenchmarkAssets.Generate(args[0], Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(args[1]))!, "x2-benchmark"), args[2]);
    X3ServiceAssets.Generate(args[0], Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(args[1]))!, "x3-service"), args[2]);
    X4ServiceAssets.Generate(args[0], Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(args[1]))!, "x4-service"), args[2]);
    return 0;
}
catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
{
    Console.Error.WriteLine("Expansion mailbox asset generation failed: " + ex.Message);
    return 1;
}
