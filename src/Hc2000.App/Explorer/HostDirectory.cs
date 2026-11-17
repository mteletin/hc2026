namespace Hc2000.App.Explorer;

internal sealed record HostEntry(string Name, string Path, bool IsDirectory, long? Size)
{
    public override string ToString() => IsDirectory ? $"{Name}/" : $"{Name}  ·  {Size:N0} bytes";
}

internal sealed class HostDirectory
{
    private readonly Stack<string> history = new();
    internal string? CurrentPath { get; private set; }
    internal IReadOnlyList<HostEntry> Entries { get; private set; } = [];
    internal string Notice { get; private set; } = "Choose a host folder.";
    internal bool CanBack => history.Count != 0;
    internal bool CanUp => CurrentPath is not null && Directory.GetParent(CurrentPath) is not null;

    internal void Open(string path)
    {
        var previous = CurrentPath;
        var loaded = Load(path);
        if (previous is not null && (!loaded || !PathEquals(previous, CurrentPath!))) history.Push(previous);
    }

    internal void Back()
    {
        if (history.Count == 0) return;
        var target = history.Pop();
        Load(target);
    }

    internal void Up()
    {
        if (CurrentPath is not null && Directory.GetParent(CurrentPath) is { } parent) Open(parent.FullName);
    }

    internal void Refresh()
    {
        if (CurrentPath is not null) Load(CurrentPath);
    }

    private bool Load(string path)
    {
        Entries = [];
        CurrentPath = null;
        try
        {
            var fullPath = Path.GetFullPath(path);
            var directory = new DirectoryInfo(fullPath);
            var rows = directory.EnumerateFileSystemInfos().Select(info => info switch
            {
                DirectoryInfo child => new HostEntry(child.Name, child.FullName, true, null),
                FileInfo file => new HostEntry(file.Name, file.FullName, false, file.Length),
                _ => throw new IOException("Unsupported directory entry.")
            }).OrderByDescending(entry => entry.IsDirectory)
              .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            CurrentPath = fullPath;
            Entries = rows;
            Notice = rows.Length == 0 ? "This folder is empty." : $"{rows.Length} item(s).";
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Notice = "Cannot open folder: " + exception.Message;
            return false;
        }
    }

    private static bool PathEquals(string first, string second) => string.Equals(first, second,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
