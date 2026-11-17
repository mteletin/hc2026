using Hc2000.Machine.Video;

namespace Hc2000.App.Runtime;

/// <summary>UI-thread, per-runtime cache of the latest immutable frame from each output. The main
/// screen always takes the ULA frame; the X2 VGA second screen takes its own, independently, so
/// showing or hiding it never changes what the main screen receives (hc2000-x2-second-screen).</summary>
internal sealed class VideoFrameRouter
{
    private VideoFrame? ula, x2;
    private bool ulaDirty, x2Dirty;

    /// <summary>This runtime has published an X2 frame. Survives a generation's frame clear (an
    /// owner reset), so the second screen does not blink away; a new runtime starts without it.</summary>
    public bool HasX2 { get; private set; }

    public void Update(VideoFrame? ulaFrame, VideoFrame? x2Frame, EmulationWorker? worker = null)
    {
        if (ulaFrame is not null)
        {
            if (ulaDirty && ula is not null && !ReferenceEquals(ula, ulaFrame))
                worker?.Recycle(ula);
            ula = ulaFrame;
            ulaDirty = true;
        }
        if (x2Frame is not null)
        {
            if (x2Dirty && x2 is not null && !ReferenceEquals(x2, x2Frame))
                worker?.Recycle(x2);
            x2 = x2Frame;
            x2Dirty = true;
            HasX2 = true;
        }
    }

    public void Invalidate() => ulaDirty = x2Dirty = true;
    public void InvalidateX2() => x2Dirty = true;

    public VideoFrame? TakeUla()
    {
        if (!ulaDirty) return null;
        ulaDirty = false;
        return ula;
    }

    public VideoFrame? TakeX2()
    {
        if (!x2Dirty) return null;
        x2Dirty = false;
        return x2;
    }

    public void Reset(EmulationWorker? worker = null)
    {
        ClearFrames(worker);
        HasX2 = false;
    }

    public void ClearFrames(EmulationWorker? worker = null)
    {
        if (ulaDirty && ula is not null) worker?.Recycle(ula);
        if (x2Dirty && x2 is not null) worker?.Recycle(x2);
        ula = x2 = null;
        ulaDirty = x2Dirty = false;
    }
}
