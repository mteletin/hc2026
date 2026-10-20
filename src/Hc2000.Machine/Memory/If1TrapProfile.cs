namespace Hc2000.Machine.Memory;

/// <summary>
/// Sinclair IF1 fallback addresses, not yet confirmed for HC2000 hardware.
/// Configure these separately from ROM bytes; spec 04 keeps them as profile constants.
/// </summary>
public sealed record If1TrapProfile(
    ushort PageInFirst = 0x0008,
    ushort PageInSecond = 0x1708,
    ushort PageOut = 0x0700)
{
    internal void Validate()
    {
        if (PageOut == PageInFirst || PageOut == PageInSecond)
            throw new ArgumentException("IF1 page-in and page-out addresses must be distinct.");
    }
}
