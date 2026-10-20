using System.Runtime.InteropServices;

namespace Hc2000.App.Media;

/// <summary>Real Linux statx/access facts. A failed or non-Linux probe is unknown, never a modeled regular file or invented inode.</summary>
internal static class LinuxFileFacts
{
    internal readonly record struct Fact(bool Known, bool Regular, bool Link, bool Writable, string? FileId);

    internal static Fact Inspect(string path)
    {
        if (!OperatingSystem.IsLinux() || Statx(-100, path, 0x100, 0x7ffu, out var stat) != 0) return Unknown;
        if ((stat.Mask & 0x101u) != 0x101u) return Unknown;
        var type = stat.Mode & 0xF000u;
        var link = type == 0xA000u;
        var regular = type == 0x8000u;
        if (!regular || link) return new(true, regular, link, false, null);
        var writable = Access(path, 2) == 0;
        return new(true, true, false, writable, $"{stat.DevMajor:x}:{stat.DevMinor:x}:{stat.Ino:x}");
    }

    private static Fact Unknown => new(false, false, false, false, null);

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxBuf
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Ino;
        [FieldOffset(136)] public uint DevMajor;
        [FieldOffset(140)] public uint DevMinor;
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out StatxBuf stat);

    [DllImport("libc", EntryPoint = "access", SetLastError = true)]
    private static extern int Access([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);
}
