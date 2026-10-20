using Xunit;

namespace Hc2000.App.Tests;

public sealed class WindowsConsoleTests
{
    [Fact]
    public void LinuxNeverCallsWindowsApi()
    {
        WindowsConsole.AttachParent(false, _ => throw new Exception("get"),
            () => throw new Exception("attach"), (_, _) => throw new Exception("set"));
    }

    [Fact]
    public void DesktopLaunchOrAlreadyAttachedConsoleLeavesHandlesAlone()
    {
        var calls = new List<string>();
        WindowsConsole.AttachParent(true, kind => { calls.Add($"get:{kind}"); return 42; },
            () => { calls.Add("attach-parent"); return false; },
            (_, _) => throw new Exception("must not change handles"));
        Assert.Equal(["get:-10", "get:-11", "get:-12", "attach-parent"], calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void TerminalLaunchKeepsNewConsoleHandlesWhenNoneWereInherited(int absent)
    {
        var attached = false;
        WindowsConsole.AttachParent(true, _ => absent,
            () => { attached = true; return true; },
            (_, _) => throw new Exception("must keep attached handles"));
        Assert.True(attached);
    }

    [Fact]
    public void TerminalLaunchPreservesInheritedPipesAndFilesAfterAttaching()
    {
        var calls = new List<string>();
        WindowsConsole.AttachParent(true,
            kind => { calls.Add($"get:{kind}"); return -kind + 100; },
            () => { calls.Add("attach-parent"); return true; },
            (kind, handle) => calls.Add($"set:{kind}:{handle}"));
        Assert.Equal(["get:-10", "get:-11", "get:-12", "attach-parent",
            "set:-10:110", "set:-11:111", "set:-12:112"], calls);
    }

    [Fact]
    public void OnlyRedirectedStreamIsRestored()
    {
        var restored = new List<int>();
        WindowsConsole.AttachParent(true, kind => kind == -12 ? 123 : 0,
            () => true, (kind, handle) => { Assert.Equal((nint)123, handle); restored.Add(kind); });
        Assert.Equal([-12], restored);
    }
}
