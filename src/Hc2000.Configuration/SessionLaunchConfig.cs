namespace Hc2000.App;

public sealed record SessionFileV1(int Version, string? DiskA, string? DiskB, bool Writeback);
public sealed record SessionRefusal(string Reason);
public sealed record EffectiveLaunch(string? DiskA, string? DiskB, bool Writeback);
public enum SessionLoadKind { Absent, Loaded, Refused }
public sealed record SessionLoad(SessionLoadKind Kind, SessionFileV1? File = null,
    string? AbsoluteConfigPath = null, SessionRefusal? Refusal = null);
public sealed record EffectiveLaunchResult(EffectiveLaunch? Launch, SessionRefusal? Refusal = null);

/// <summary>Pure session/CLI precedence. Disk existence and mounting remain LaunchStartup's job.
/// CLI disks retain their already-parsed drive prefix and existing lookup semantics.</summary>
public static class SessionLaunchConfig
{
    public static EffectiveLaunchResult Resolve(SessionLoad session, string? cliDiskA = null,
        string? cliDiskB = null, bool? cliWriteback = null, bool help = false)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (help) return new(new(null, null, false));
        if (session.Kind == SessionLoadKind.Refused)
            return new(null, session.Refusal ?? new("Session application refused."));
        if (session.Kind == SessionLoadKind.Absent)
            return new(new(cliDiskA, cliDiskB, cliWriteback ?? false));
        if (session.Kind != SessionLoadKind.Loaded || session.File is not { Version: 1 } file
            || session.AbsoluteConfigPath is null)
            return new(null, new("Invalid session load result."));
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(session.AbsoluteConfigPath))!;
            string? Disk(string? path, char drive) => path is null ? null
                : drive + ":" + Path.GetFullPath(path, directory);
            return new(new(cliDiskA ?? Disk(file.DiskA, 'A'), cliDiskB ?? Disk(file.DiskB, 'B'),
                cliWriteback ?? file.Writeback));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return new(null, new("Session path refused: " + ex.Message));
        }
    }
}
