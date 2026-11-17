using System.Collections.Immutable;
using System.Globalization;

namespace Hc2000.App.Network;

/// <summary>One drive a terminal profile shares with the remote host (hc2000-remote-drive-mount, decision
/// hc2000-drive-sharing-sftp-v1): the drive letter, read-write or read-only, and the user areas it shows.</summary>
public sealed record X6DriveShare(char Drive, bool ReadWrite, ImmutableArray<int> Users)
{
    public override string ToString() =>
        $"{Drive}:{(ReadWrite ? "RW" : "RO")}" + (Users.SequenceEqual([0]) ? "" : ":" + string.Join('.', Users));
}

/// <summary>The drives a profile shares, written <c>B:RW,A:RO</c>, with user areas after a second colon
/// (<c>B:RW:0.1</c>; user area 0 alone when none is named). Nothing is shared unless named: the epic's opt-in. On a
/// machine with the X6 card the floppies A: and B: can be shared; M: is RAM inside the machine with no media to take
/// off, and an X5 machine has no HDD, so both are refused with their reason (architecture 645431).</summary>
public static class X6DriveShares
{
    public static ImmutableArray<X6DriveShare> Parse(string? text, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(text)) return [];
        var shares = ImmutableArray.CreateBuilder<X6DriveShare>();
        foreach (var raw in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.ToUpperInvariant().Split(':');
            if (parts.Length is < 2 or > 3 || parts[0].Length != 1) { error = $"{raw}: write drive:RW or drive:RO"; return []; }
            var drive = parts[0][0];
            if (drive == 'M') { error = "M: is RAM inside the machine; it cannot be taken off to share"; return []; }
            if (drive is not ('A' or 'B')) { error = $"{drive}: only the floppies A: and B: can be shared on this machine"; return []; }
            if (parts[1] is not ("RW" or "RO")) { error = $"{raw}: the mode is RW or RO"; return []; }
            var users = ImmutableArray.Create(0);
            if (parts.Length == 3)
            {
                var areas = parts[2].Split('.', StringSplitOptions.RemoveEmptyEntries);
                if (areas.Length == 0 || areas.Any(a => !int.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n > 15))
                { error = $"{raw}: user areas are 0-15, separated by dots"; return []; }
                users = [.. areas.Select(a => int.Parse(a, CultureInfo.InvariantCulture)).Distinct().Order()];
            }
            if (shares.Any(s => s.Drive == drive)) { error = $"{drive}: named twice"; return []; }
            shares.Add(new(drive, parts[1] == "RW", users));
        }
        return shares.ToImmutable();
    }
}
