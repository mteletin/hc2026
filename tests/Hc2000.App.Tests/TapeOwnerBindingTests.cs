using Hc2000.App.Input;
using Hc2000.App.Media;
using Hc2000.App.Runtime;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Tape;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class TapeOwnerBindingTests
{
    // Uses only baseline APIs plus reflection so it can run, not merely fail compilation,
    // against the pre-binding source. Both paths compose the REAL scheduled transport.
    [Fact]
    public async Task OwnerBoundaryReportsComposedScheduledTransportCapability()
    {
        var queue = new InputQueue(); var bridge = new MediaBridge(queue);
        InputController? controller = null;
        using var worker = new EmulationWorker(() =>
        {
            var fixture = new RuntimeTests.Fixture();
            var transport = fixture.Machine.EnableTape(3);
            var constructor = typeof(MediaSession).GetConstructor([typeof(FloppyDrive), typeof(FloppyDrive), typeof(TapeTransport)]);
            var session = constructor is null ? new MediaSession(new(new()), new(new())) :
                (MediaSession)constructor.Invoke([new FloppyDrive(new()), new FloppyDrive(new()), transport]);
            controller = new(queue, media: (command, _) => bridge.Publish(session.Apply(command)));
            return fixture.Machine;
        }, context =>
        {
            context.Pause();
            controller!.ApplyPending(context.Machine.MasterTick, context.Machine.Keyboard);
        }, cleanup: _ => bridge.Disconnect());
        try
        {
            var reply = await bridge.RequestAsync(new(MediaAction.Inspect)).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(reply.Error);
            Assert.NotNull(typeof(MediaReply).GetProperty("Tape")?.GetValue(reply));
        }
        finally
        {
            worker.Dispose(); await worker.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
