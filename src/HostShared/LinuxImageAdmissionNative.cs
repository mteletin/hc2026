using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Hc2000.HostFiles;

internal static class LinuxImageAdmissionNative
{
    private const int DirectoryFlag = 0x10000, NoFollow = 0x20000, CloseExec = 0x80000;
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    internal struct Facts
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(16)] internal uint Links;
        [FieldOffset(20)] internal uint Uid;
        [FieldOffset(28)] internal ushort Mode;
        [FieldOffset(32)] internal ulong Inode;
        [FieldOffset(40)] internal ulong Length;
        [FieldOffset(136)] internal uint Major;
        [FieldOffset(140)] internal uint Minor;
        internal NativeImageFileId Id => new(Major, Minor, Inode);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Passwd
    {
        internal IntPtr Name, Password;
        internal uint Uid, Gid;
        internal IntPtr Gecos, Home, Shell;
    }
    internal static uint Uid => GetEuid();
    internal static string AccountHome()
    {
        var buffer = Marshal.AllocHGlobal(16384);
        try
        {
            if (GetPwUid(Uid, out var pw, buffer, 16384, out var result) != 0 || result == IntPtr.Zero)
                throw new AdmissionException(ImageAdmissionRefusal.InvalidDomain);
            return Marshal.PtrToStringUTF8(pw.Home) ?? throw new AdmissionException(ImageAdmissionRefusal.InvalidDomain);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    internal static Facts Probe(SafeFileHandle handle)
    {
        if (Statx(handle, "", 0x1000 | 0x100, 0x7ff, out var facts) != 0 || (facts.Mask & 0x7ff) != 0x7ff)
            throw new AdmissionException(ImageAdmissionRefusal.Unsupported);
        return facts;
    }
    internal static void Supported(SafeFileHandle handle)
    {
        var bytes = new byte[256];
        if (FStatFs(handle, bytes) != 0) throw new AdmissionException(ImageAdmissionRefusal.Unsupported);
        // Only locally implemented, explicitly supported filesystem families. No NFS/FUSE fallback.
        var magic = BitConverter.ToInt64(bytes);
        if (magic is not (0xef53 or 0x58465342 or 0x9123683e or 0x01021994 or 0x2fc12fc1))
            throw new AdmissionException(ImageAdmissionRefusal.Unsupported);
    }
    internal static SafeFileHandle Directory(string path, bool create, bool privateTest = false)
    {
        if (path.Length > 4096 || !Path.IsPathFullyQualified(path))
            throw new AdmissionException(ImageAdmissionRefusal.InvalidDomain);
        var current = OpenHandle(-100, "/", DirectoryFlag);
        try
        {
            var root = Probe(current);
            if ((root.Mode & 0xf000) != 0x4000 || (root.Uid != 0 && root.Uid != Uid) || (root.Mode & 0x12) != 0)
                throw new AdmissionException(ImageAdmissionRefusal.InvalidDomain);
            foreach (var part in Path.GetFullPath(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var fd = OpenAt(current.DangerousGetHandle().ToInt32(), part, DirectoryFlag | NoFollow | CloseExec, 0);
                if (fd < 0 && create && Marshal.GetLastPInvokeError() == 2)
                {
                    Supported(current); // Never create even domain directories on an unsupported filesystem.
                    if (MkdirAt(current, part, 0x1c0) == 0) FlushDirectory(current);
                    else if (Marshal.GetLastPInvokeError() != 17)
                        throw new AdmissionException(ImageAdmissionRefusal.InvalidDomain);
                    fd = OpenAt(current.DangerousGetHandle().ToInt32(), part, DirectoryFlag | NoFollow | CloseExec, 0);
                }
                if (fd < 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (!create && error == 2)
                    {
                        Supported(current);
                        throw new AdmissionException(ImageAdmissionRefusal.InvalidDomain, missingDirectory: true);
                    }
                    throw new AdmissionException(ImageAdmissionRefusal.InvalidDomain);
                }
                var next = new SafeFileHandle(fd, true);
                try
                {
                    var f = Probe(next);
                    // A root-owned sticky ancestor (the standard /tmp, /var/tmp) is safe in every domain:
                    // a non-root user cannot rename or delete entries it does not own, and each component
                    // above is opened with O_NOFOLLOW, so the walk cannot be diverted by a local attacker.
                    var stickyAncestor = (f.Mode & 0x200) != 0 && f.Uid == 0;
                    if ((f.Mode & 0xf000) != 0x4000 || (f.Uid != 0 && f.Uid != Uid) ||
                        ((f.Mode & 0x12) != 0 && !stickyAncestor))
                        throw new AdmissionException(ImageAdmissionRefusal.InvalidDomain);
                }
                catch { next.Dispose(); throw; }
                current.Dispose(); current = next;
            }
            Supported(current);
            return current;
        }
        catch { current.Dispose(); throw; }
    }
    internal static (SafeFileHandle Handle, bool Created) Gate(SafeFileHandle directory)
    {
        var fd = OpenAt(directory.DangerousGetHandle().ToInt32(), "gate", 2 | 0x40 | 0x80 | NoFollow | CloseExec, 0x180);
        var created = fd >= 0;
        if (!created && Marshal.GetLastPInvokeError() == 17)
            fd = OpenAt(directory.DangerousGetHandle().ToInt32(), "gate", 2 | NoFollow | CloseExec, 0);
        if (fd < 0) throw new AdmissionException(ImageAdmissionRefusal.InvalidDomain);
        var handle = new SafeFileHandle(fd, true);
        try
        {
            var f = Probe(handle);
            if ((f.Mode & 0xf000) != 0x8000 || (f.Mode & 0x1ff) != 0x180 || f.Uid != Uid || f.Links != 1)
                throw new AdmissionException(ImageAdmissionRefusal.InvalidDomain);
            Supported(handle);
            if (Flock(handle, 2 | 4) != 0)
                throw new AdmissionException(Marshal.GetLastPInvokeError() is 11 or 35 ? ImageAdmissionRefusal.Busy : ImageAdmissionRefusal.IoFailure);
            return (handle, created);
        }
        catch { handle.Dispose(); throw; }
    }
    internal static void FlushDirectory(SafeFileHandle directory)
    {
        if (FSync(directory) != 0) throw new AdmissionException(ImageAdmissionRefusal.IoFailure);
    }
    internal static Facts FileAt(string path, bool privateTest)
    {
        using var file = OpenSource(path, privateTest);
        return Probe(file);
    }
    internal static SafeFileHandle OpenSource(string path, bool privateTest)
    {
        using var parent = Directory(Path.GetDirectoryName(path)!, false, privateTest);
        var file = OpenHandle(parent.DangerousGetHandle().ToInt32(), Path.GetFileName(path), 0);
        try
        {
            Supported(file);
            var f = Probe(file);
            if ((f.Mode & 0xf000) != 0x8000 || f.Uid != Uid || (f.Mode & 0x12) != 0 || f.Links == 0 || f.Length > long.MaxValue)
                throw new AdmissionException(ImageAdmissionRefusal.Unsupported);
            return file;
        }
        catch { file.Dispose(); throw; }
    }
    private static SafeFileHandle OpenHandle(int parent, string name, int flags)
    {
        var fd = OpenAt(parent, name, flags | NoFollow | CloseExec | 0x800, 0);
        if (fd < 0) throw new AdmissionException(ImageAdmissionRefusal.InvalidDomain);
        return new SafeFileHandle(fd, true);
    }
    [DllImport("libc", EntryPoint = "openat", SetLastError = true)] private static extern int OpenAt(int fd, string path, int flags, uint mode);
    [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)] private static extern int MkdirAt(SafeFileHandle fd, string path, uint mode);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)] private static extern int Statx(SafeFileHandle fd, string path, int flags, uint mask, out Facts facts);
    [DllImport("libc", EntryPoint = "fstatfs", SetLastError = true)] private static extern int FStatFs(SafeFileHandle fd, byte[] facts);
    [DllImport("libc", EntryPoint = "flock", SetLastError = true)] private static extern int Flock(SafeFileHandle fd, int operation);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)] private static extern int FSync(SafeFileHandle fd);
    [DllImport("libc", EntryPoint = "geteuid")] private static extern uint GetEuid();
    [DllImport("libc", EntryPoint = "getpwuid_r")] private static extern int GetPwUid(uint uid, out Passwd pwd, IntPtr buffer, nuint size, out IntPtr result);
}

internal sealed class AdmissionException(ImageAdmissionRefusal refusal, bool missingDirectory = false) : IOException($"Native image admission: {refusal}.")
{
    internal ImageAdmissionRefusal Refusal { get; } = refusal;
    internal bool MissingDirectory { get; } = missingDirectory;
}
