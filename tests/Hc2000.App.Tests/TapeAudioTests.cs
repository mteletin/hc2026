using Hc2000.App.Audio;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class TapeAudioTests
{
    [Fact]
    public void RealMicTransitionsNeverCreateDirectSpeakerAudio()
    {
        var a = new BeeperResampler(FrameTiming.MasterHz, 48000, channel: BeeperChannel.Bel);
        var b = new BeeperResampler(FrameTiming.MasterHz, 48000, channel: BeeperChannel.Bel);
        var withMic = a.Render(355680, [new(0, false, true), new(10000, false, false), new(20000, true, true)]);
        var withoutMic = b.Render(355680, [new(0, false, false), new(10000, false, false), new(20000, true, false)]);
        Assert.Equal(withoutMic, withMic);
    }

    [Fact]
    public void EarIsSeparatelyTimedAndMuteOnlyAffectsHostSamples()
    {
        var audible = new BeeperResampler(FrameTiming.MasterHz, 48000, channel: BeeperChannel.EarMonitor);
        var muted = new BeeperResampler(FrameTiming.MasterHz, 48000, channel: BeeperChannel.EarMonitor);
        BeeperEdge[] edges = [new(5, false, true), new(10000, true, true), new(20000, false, false)];
        var samples = audible.Render(355680, edges);
        var silence = muted.Render(355680, edges, gain: 0);
        Assert.Contains(samples, sample => sample != 0);
        Assert.All(silence, sample => Assert.Equal(0, sample));
        Assert.Equal(audible.EndMasterTick, muted.EndMasterTick);
        Assert.Equal(audible.Phase, muted.Phase); Assert.Equal(audible.Pins, muted.Pins);
        Assert.Equal(audible.Render(711360, [new(360000, true, true)]), muted.Render(711360, [new(360000, true, true)]));
    }

    [Fact]
    public void EarNotificationObservesOnlyChangedPinsAtExactSchedulerTime()
    {
        var scheduler = new DeviceScheduler();
        var memory = new HcMemory(new(), new byte[16384], new byte[16384], new byte[16384]);
        var ula = new Ula(memory, new(), scheduler, new FrameClock(scheduler, 1), 2);
        var observed = new List<(long, bool)>(); ula.TapeInputChanged += (tick, level) => observed.Add((tick, level));
        scheduler.AdvanceTo(123); var pending = scheduler.PendingEvents.ToArray();
        ula.TapeIn = true; ula.TapeIn = true; ula.TapeIn = false;
        Assert.Equal(new[] { (123L, true), (123L, false) }, observed);
        Assert.Equal(123, scheduler.Now); Assert.Equal(pending, scheduler.PendingEvents);
        Assert.False(ula.Beeper.Microphone); Assert.False(ula.Beeper.Speaker);
    }
}
