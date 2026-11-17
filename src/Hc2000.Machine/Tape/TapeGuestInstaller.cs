namespace Hc2000.Machine.Tape;

/// <summary>
/// Bind the assembled TAPEINIT.COM template to the owner-selected mounted session.
/// Only metadata is emitted: no filesystem, second volume image or guest operation port.
/// A clean unmount/remount requires a fresh installer and guest disk reset.
/// </summary>
public static class TapeGuestInstaller
{
    /// <summary>The unbound TPV1 template shape: a JP entry, the magic at 3..7 and a zeroed
    /// 6-byte binding at 7..13. The media composer and the launch preparation both validate
    /// against this before any binding.</summary>
    public static void ValidateTemplate(ReadOnlySpan<byte> template)
    {
        if (template.Length is < 13 or > 0x5f00 || template[0] != 0xc3
            || !template.Slice(3, 4).SequenceEqual("TPV1"u8) || template.Slice(7, 6).IndexOfAnyExcept((byte)0) >= 0)
            throw new ArgumentException("Expected the unbound TPV1 assembled COM template.", nameof(template));
    }

    public static byte[] Bind(ReadOnlySpan<byte> template, TapeVolumeSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.Mounted || !session.MediaLive)
            throw new InvalidOperationException("A live mounted session is required.");
        ValidateTemplate(template);
        var result = template.ToArray();
        session.WriteBinding(result.AsSpan(7, 6));
        return result;
    }
}
