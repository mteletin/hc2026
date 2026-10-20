using Hc2000.App.Debugging;
using Hc2000.App.Runtime;
using Hc2000.Machine.Memory;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class EmulationWorkerDebuggerTests
{
    [Fact]
    public async Task CaptureDemandStaysPendingUntilTheOwnerPublishes()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var memory = new HcMemory(new MemoryProfile(), new byte[16384], new byte[16384], new byte[16384]);
        using var worker = new EmulationWorker(() => Z80VisualDemo.CreateMachine(out _, out _),
            context => context.Pause(), debugSnapshot: _ =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Capture was not released.");
                return new DebuggerSession("gated boundary fixture").Boundary(new(0x1234, 0xffff), memory, null, 42);
            });
        try
        {
            worker.RequestDebugSnapshot();
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(10))));
            Assert.True(worker.DebugDemandPending, "An owner capture is still an outstanding demand until publication.");
        }
        finally
        {
            release.Set();
            worker.Dispose();
            await worker.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
