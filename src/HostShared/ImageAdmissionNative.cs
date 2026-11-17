using Microsoft.Win32.SafeHandles;

namespace Hc2000.HostFiles;

// Common facts retain the Linux layout for record compatibility. Windows uses volume serial +
// NTFS file index for Id; Unix ownership/permission fields are never used as Windows authority.
internal static class ImageAdmissionNative
{
    internal static string CanonicalPath(string path) => OperatingSystem.IsWindows()
        ? WindowsImageAdmissionNative.Canonical(path) : Path.GetFullPath(path);
    internal static uint Uid => LinuxImageAdmissionNative.Uid;
    internal static string AccountHome() => LinuxImageAdmissionNative.AccountHome();
    internal static string StateRoot => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HC2000", "native-image-admission")
        : Path.Combine(AccountHome(), ".local/state/hc2000/native-image-admission");
    internal static LinuxImageAdmissionNative.Facts Probe(SafeFileHandle handle) => OperatingSystem.IsWindows()
        ? WindowsImageAdmissionNative.Probe(handle) : LinuxImageAdmissionNative.Probe(handle);
    internal static void Supported(SafeFileHandle handle)
    { if (OperatingSystem.IsWindows()) WindowsImageAdmissionNative.Supported(handle); else LinuxImageAdmissionNative.Supported(handle); }
    internal static NativeDirectory Directory(string path, bool create, bool privateTest = false) => OperatingSystem.IsWindows()
        ? WindowsImageAdmissionNative.Directory(path, create) : new([LinuxImageAdmissionNative.Directory(path, create, privateTest)]);
    internal static (SafeFileHandle Handle, bool Created) Gate(SafeFileHandle directory) => OperatingSystem.IsWindows()
        ? WindowsImageAdmissionNative.Gate(directory) : LinuxImageAdmissionNative.Gate(directory);
    internal static void FlushDirectory(SafeFileHandle directory)
    {
        // Windows gate creation uses FILE_FLAG_WRITE_THROUGH and every state write FlushFileBuffers.
        // Directory fsync is unavailable on an ordinary Windows directory handle.
        if (!OperatingSystem.IsWindows()) LinuxImageAdmissionNative.FlushDirectory(directory);
    }
    internal static void TightenDirectory(SafeFileHandle handle) => LinuxImageAdmissionNative.TightenDirectory(handle);
    internal static string? TightenStateChain(string home, string root) => LinuxImageAdmissionNative.TightenStateChain(home, root);
    internal static SafeFileHandle OpenSource(string path, bool privateTest) => OperatingSystem.IsWindows()
        ? WindowsImageAdmissionNative.OpenSource(path) : LinuxImageAdmissionNative.OpenSource(path, privateTest);
    internal static LinuxImageAdmissionNative.Facts FileAt(string path, bool privateTest)
    { using var handle = OpenSource(path, privateTest); return Probe(handle); }
    internal static void RequirePrivateDirectory(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows()) { WindowsImageAdmissionNative.RequirePrivateDirectory(handle); return; }
        var facts = Probe(handle);
        if (facts.Uid != Uid || (facts.Mode & 0x1ff) != 0x1c0)
            throw new AdmissionException(ImageAdmissionRefusal.InvalidDomain);
    }
}

internal sealed class NativeDirectory(List<SafeFileHandle> handles) : IDisposable
{
    // Pin every ancestor against rename/delete until the operation using this directory finishes.
    public static implicit operator SafeFileHandle(NativeDirectory directory) => directory.handles[^1];
    private readonly List<SafeFileHandle> handles = handles;
    public void Dispose() { for (var i = handles.Count - 1; i >= 0; i--) handles[i].Dispose(); }
}
