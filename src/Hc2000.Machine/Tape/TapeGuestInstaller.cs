namespace Hc2000.Machine.Tape;

/// <summary>
/// Bind the assembled TAPEINIT.COM template to the owner-selected mounted session.
/// Only metadata is emitted: no filesystem, second volume image or guest operation port.
/// A clean unmount/remount requires a fresh installer and guest disk reset.
/// </summary>
public static class TapeGuestInstaller
{
    public static byte[] Bind(ReadOnlySpan<byte> template, TapeVolumeSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.Mounted || !session.MediaLive)
            throw new InvalidOperationException("A live mounted session is required.");
        if (template.Length is < 13 or > 0x5f00 || template[0] != 0xc3
            || !template.Slice(3, 4).SequenceEqual("TPV1"u8) || template.Slice(7, 6).IndexOfAnyExcept((byte)0) >= 0)
            throw new ArgumentException("Expected the unbound TPV1 assembled COM template.", nameof(template));
        var result = template.ToArray();
        session.BindingMetadata.CopyTo(result.AsSpan(7, 6));
        return result;
    }
}
