namespace Hc2000.Machine.Expansion;

/// <summary>Bounded Paula-class sample descriptor. Admission of each physical page still belongs
/// to X0 DMA; arithmetic here never grants access to a page.</summary>
internal readonly record struct ExpansionX3Sample(ushort Page, ushort Offset, uint Length,
    uint LoopStart, uint LoopLength, ushort Period, byte Volume)
{
    internal const uint MaximumLength = 131070;
    internal const int MaximumSegment = 8192;
    internal const int MinimumPeriod = 124;
    private const long ArenaBytes = 65536L * ExpansionDma.PageBytes;

    internal bool IsValid => Offset < ExpansionDma.PageBytes && Length is > 0 and <= MaximumLength &&
        Period >= MinimumPeriod && Volume <= 64 &&
        (long)Page * ExpansionDma.PageBytes + Offset + Length <= ArenaBytes &&
        (LoopLength == 0 ? LoopStart == 0 : LoopStart < Length && (ulong)LoopStart + LoopLength <= Length);

    /// <summary>Computes one refill without crossing a physical page, the loop/sample boundary,
    /// FIFO capacity or the half-FIFO transfer limit. Position is a sample-relative byte offset.
    /// For a loop, after NextPosition reaches the loop end it is reset to LoopStart.</summary>
    internal bool TrySegment(uint position, int freeBytes, out X3SampleSegment segment)
    {
        segment = default;
        if (!IsValid || freeBytes <= 0) return false;
        var end = LoopLength == 0 ? Length : LoopStart + LoopLength;
        if (position >= end) return false;
        var physical = (long)Page * ExpansionDma.PageBytes + Offset + position;
        var page = (ushort)(physical / ExpansionDma.PageBytes);
        var offset = (ushort)(physical % ExpansionDma.PageBytes);
        var count = (int)Math.Min(Math.Min((long)freeBytes, MaximumSegment),
            Math.Min(ExpansionDma.PageBytes - offset, (long)end - position));
        var next = position + (uint)count;
        var ended = LoopLength == 0 && next == end;
        if (LoopLength != 0 && next == end) next = LoopStart;
        segment = new(page, offset, count, next, ended);
        return true;
    }
}

internal readonly record struct X3SampleSegment(ushort Page, ushort Offset, int Count, uint NextPosition, bool Ended);
