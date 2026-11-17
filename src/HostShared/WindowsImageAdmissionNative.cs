using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Hc2000.HostFiles;

[SupportedOSPlatform("windows")]
internal static class WindowsImageAdmissionNative
{
    private const uint BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;
    private const uint Read = 0x80000000, Write = 0x40000000;
    private const uint ReadControl = 0x20000, ReadAttributes = 0x80;
    private static AdmissionException Refuse(string operation, string detail, int? error = null,
        ImageAdmissionRefusal refusal = ImageAdmissionRefusal.Unsupported, bool missingDirectory = false) =>
        new(refusal, missingDirectory, $"Windows {operation}: {detail}" +
            (error is int code ? $"; Win32={code} ({new System.ComponentModel.Win32Exception(code).Message})" : ""));
    [StructLayout(LayoutKind.Sequential)]
    private struct Information
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    private static SafeFileHandle Open(string path, uint access, uint share, uint disposition, uint flags)
    {
        var handle = CreateFile(@"\\?\" + path, access, share, IntPtr.Zero, disposition, flags | OpenReparsePoint, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastPInvokeError(); handle.Dispose();
        throw Refuse("CreateFileW", $"path={path}; access=0x{access:X}; share=0x{share:X}; flags=0x{flags | OpenReparsePoint:X}",
            error, error is 32 or 33 ? ImageAdmissionRefusal.Busy : ImageAdmissionRefusal.InvalidDomain, error is 2 or 3);
    }
    internal static LinuxImageAdmissionNative.Facts Probe(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info))
            throw Refuse("GetFileInformationByHandle", "could not read file identity/attributes", Marshal.GetLastPInvokeError());
        var type = GetFileType(handle);
        if (type != 1) throw Refuse("GetFileType", $"expected disk handle, actual={type}");
        if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
            throw Refuse("file attributes", $"reparse point refused; attributes=0x{info.Attributes:X}");
        // Only NTFS is admitted: ReFS requires its 128-bit file ID and a wider on-disk admission record.
        return new() { Major = info.Volume, Minor = 0, Inode = ((ulong)info.IndexHigh << 32) | info.IndexLow,
            Length = ((ulong)info.SizeHigh << 32) | info.SizeLow, Links = info.Links,
            Mode = (ushort)((info.Attributes & (uint)FileAttributes.Directory) != 0 ? 0x4000 : 0x8000) };
    }
    internal static void Supported(SafeFileHandle handle)
    {
        var fs = new StringBuilder(32);
        if (!GetVolumeInformationByHandle(handle, null, 0, out _, out _, out var flags, fs, fs.Capacity))
            throw Refuse("GetVolumeInformationByHandleW", "could not query filesystem", Marshal.GetLastPInvokeError());
        if (!string.Equals(fs.ToString(), "NTFS", StringComparison.OrdinalIgnoreCase) || (flags & 8) == 0)
            throw Refuse("filesystem policy", $"filesystem={fs}; flags=0x{flags:X}; local NTFS with persistent ACLs required");
        var path = HandlePath(handle);
        var driveType = GetDriveType(Path.GetPathRoot(path)!);
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || driveType != 3)
            throw Refuse("drive policy", $"path={path}; driveType={driveType}; fixed local drive required");
    }
    internal static string HandlePath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, buffer, buffer.Capacity, 0);
        if (length == 0) throw Refuse("GetFinalPathNameByHandleW", "could not resolve opened path",
            Marshal.GetLastPInvokeError(), ImageAdmissionRefusal.InvalidDomain);
        if (length >= buffer.Capacity) throw Refuse("GetFinalPathNameByHandleW", $"path requires {length} characters",
            refusal: ImageAdmissionRefusal.InvalidDomain);
        var path = buffer.ToString();
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }
    internal static string Canonical(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Length > 4096 || path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new AdmissionException(ImageAdmissionRefusal.InvalidDomain);
        var full = Path.GetFullPath(path);
        if (full.Length < 3 || full[1] != ':' || full[2] != '\\' || full[2..].Contains(':') ||
            full.Split('\\').Skip(1).Any(part => part.EndsWith(' ') || part.EndsWith('.')))
            throw new AdmissionException(ImageAdmissionRefusal.InvalidDomain);
        return Path.TrimEndingDirectorySeparator(full);
    }
    private static DirectorySecurity PrivateSecurity()
    {
        var sid = WindowsIdentity.GetCurrent().User ?? throw new AdmissionException(ImageAdmissionRefusal.InvalidDomain);
        var security = new DirectorySecurity();
        security.SetOwner(sid); security.SetAccessRuleProtection(true, false);
        foreach (var allowed in new[] { sid, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(allowed, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }
    internal static NativeDirectory Directory(string path, bool create)
    {
        var full = Canonical(path);
        var root = Path.GetPathRoot(full)!;
        var handles = new List<SafeFileHandle>();
        try
        {
            var current = root;
            // Exclude directory writers as well as rename/delete, so an opened ancestor cannot
            // be converted into a reparse point while the full-path child open is in progress.
            handles.Add(Open(current, ReadControl | ReadAttributes, 1, 3, BackupSemantics));
            foreach (var part in full[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                SafeFileHandle next;
                try { next = Open(current, ReadControl | ReadAttributes, 1, 3, BackupSemantics); }
                catch (AdmissionException ex) when (create && ex.MissingDirectory)
                {
                    Supported(handles[^1]);
                    PrivateSecurity().CreateDirectory(current);
                    next = Open(current, ReadControl | ReadAttributes, 1, 3, BackupSemantics);
                }
                handles.Add(next);
                if ((Probe(next).Mode & 0xf000) != 0x4000) throw new AdmissionException(ImageAdmissionRefusal.InvalidDomain);
            }
            Supported(handles[^1]);
            return new(handles);
        }
        catch { foreach (var handle in handles) handle.Dispose(); throw; }
    }
    internal static void RequirePrivateDirectory(SafeFileHandle handle)
    {
        var security = new DirectoryInfo(HandlePath(handle)).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        var sid = WindowsIdentity.GetCurrent().User!;
        if (!sid.Equals(security.GetOwner(typeof(SecurityIdentifier))) || !security.AreAccessRulesProtected)
            throw Refuse("state ACL policy", $"path={HandlePath(handle)}; owner={security.GetOwner(typeof(SecurityIdentifier))}; currentUser={sid}; protectedAcl={security.AreAccessRulesProtected}",
                refusal: ImageAdmissionRefusal.InvalidDomain);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            var identity = (SecurityIdentifier)rule.IdentityReference;
            if (rule.AccessControlType == AccessControlType.Allow && !identity.Equals(sid) &&
                !identity.IsWellKnown(WellKnownSidType.LocalSystemSid) && !identity.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid))
                throw Refuse("state ACL policy", $"path={HandlePath(handle)}; untrusted principal={identity}; rights={rule.FileSystemRights}",
                    refusal: ImageAdmissionRefusal.InvalidDomain);
        }
    }
    private static void RequireSafeFile(string path)
    {
        var security = new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        var user = WindowsIdentity.GetCurrent().User!;
        static bool Privileged(SecurityIdentifier sid) => sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
            sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);
        var owner = (SecurityIdentifier)security.GetOwner(typeof(SecurityIdentifier))!;
        if (!owner.Equals(user) && !Privileged(owner))
            throw Refuse("source owner policy", $"path={path}; owner={owner}; currentUser={user}");
        const FileSystemRights mutation = FileSystemRights.Write | FileSystemRights.Delete |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            var identity = (SecurityIdentifier)rule.IdentityReference;
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & mutation) != 0 &&
                !identity.Equals(user) && !Privileged(identity))
                throw Refuse("source ACL policy", $"path={path}; principal={identity}; rights={rule.FileSystemRights}; other-principal mutation grant refused");
        }
    }
    internal static (SafeFileHandle Handle, bool Created) Gate(SafeFileHandle directory)
    {
        RequirePrivateDirectory(directory);
        var path = Path.Combine(HandlePath(directory), "gate");
        var handle = CreateFile(@"\\?\" + path, Read | Write, 0, IntPtr.Zero, 1, OpenReparsePoint | 0x80000000, IntPtr.Zero);
        var created = !handle.IsInvalid;
        if (!created)
        {
            var error = Marshal.GetLastPInvokeError(); handle.Dispose();
            if (error is not (80 or 183))
                throw Refuse("CreateFileW(gate)", $"path={path}", error,
                    error is 32 or 33 ? ImageAdmissionRefusal.Busy : ImageAdmissionRefusal.InvalidDomain);
            handle = Open(path, Read | Write, 0, 3, 0x80000000);
        }
        try
        {
            var facts = Probe(handle); Supported(handle);
            RequireSafeFile(path);
            if ((facts.Mode & 0xf000) != 0x8000 || facts.Links != 1)
                throw new AdmissionException(ImageAdmissionRefusal.InvalidDomain);
            return (handle, created);
        }
        catch { handle.Dispose(); throw; }
    }
    internal static SafeFileHandle OpenSource(string path)
    {
        var full = Canonical(path);
        using var parent = Directory(Path.GetDirectoryName(full)!, false);
        // Permit the admitted publisher's atomic replacement; exclude every open write handle.
        var handle = Open(full, Read, 1 | 4, 3, 0);
        try
        {
            var facts = Probe(handle); Supported(handle);
            // User-selected images may inherit ordinary shared-drive ACLs (for example,
            // Authenticated Users: Modify). Kernel share modes exclude open/new data writers
            // for every account while this read handle lives; identity checks detect replacement.
            // Private owner/DACL requirements belong to the admission gate, not image sources.
            if ((facts.Mode & 0xf000) != 0x8000 || facts.Links == 0 || facts.Length > long.MaxValue)
                throw Refuse("source facts policy", $"path={full}; mode=0x{facts.Mode:X}; links={facts.Links}; length={facts.Length}");
            return handle;
        }
        catch (AdmissionException ex)
        {
            handle.Dispose();
            throw new AdmissionException(ex.Refusal, ex.MissingDirectory, $"source={full}; {ex.Diagnostic ?? ex.Message}");
        }
        catch { handle.Dispose(); throw; }
    }
    // Non-authoritative metadata, also usable while our own stage writer has exclusive data access.
    // Callers needing read exclusion must still acquire admission and use OpenSource.
    internal static LinuxImageAdmissionNative.Facts InspectFile(string path)
    {
        var full = Canonical(path);
        using var parent = Directory(Path.GetDirectoryName(full)!, false);
        using var handle = Open(full, ReadAttributes, 7, 3, 0);
        var facts = Probe(handle); Supported(handle);
        return facts;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Information info);
    [DllImport("kernel32.dll")] private static extern uint GetFileType(SafeFileHandle handle);
    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetVolumeInformationByHandle(SafeFileHandle handle,
        StringBuilder? volumeName, int volumeNameSize, out uint serial, out uint maxComponent, out uint flags, StringBuilder fs, int fsSize);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, int length, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "GetDriveTypeW", CharSet = CharSet.Unicode)] private static extern uint GetDriveType(string root);
}
