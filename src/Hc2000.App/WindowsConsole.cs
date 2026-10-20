using System.Runtime.InteropServices;

namespace Hc2000.App;

/// <summary>Reuse a terminal's console, never allocate one for a desktop launch.</summary>
internal static class WindowsConsole
{
    public static void AttachParent() => AttachParent(
        OperatingSystem.IsWindows(), GetStdHandle,
        () => AttachConsole(uint.MaxValue), (kind, handle) => { SetStdHandle(kind, handle); });

    // Native-call seam lets Linux tests cover desktop, terminal and redirected launches.
    internal static void AttachParent(bool isWindows, Func<int, nint> getHandle,
        Func<bool> attach, Action<int, nint> setHandle)
    {
        if (!isWindows) return;
        var stdin = getHandle(-10);
        var stdout = getHandle(-11);
        var stderr = getHandle(-12);
        // No parent console (Explorer), or already attached: leave handles untouched.
        if (!attach()) return;
        // AttachConsole installs console handles; retain any inherited redirection.
        Restore(-10, stdin);
        Restore(-11, stdout);
        Restore(-12, stderr);

        void Restore(int kind, nint handle)
        {
            if (handle != 0 && handle != -1) setHandle(kind, handle);
        }
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll")]
    private static extern nint GetStdHandle(int kind);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStdHandle(int kind, nint handle);
}
