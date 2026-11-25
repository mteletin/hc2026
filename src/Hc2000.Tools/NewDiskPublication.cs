using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Hc2000.Tools;

/// <summary>Publication of a brand-new image: a sibling temporary file, then the name appears only if absent.</summary>
internal static class NewDiskPublication
{
    internal static void Publish(string label, string temporaryPrefix, string destination, byte[] bytes, TextWriter error)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $"{temporaryPrefix}{Guid.NewGuid():N}.tmp");
        var owned = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                owned = true;
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (OperatingSystem.IsWindows())
            {
                File.Move(temporary, destination, overwrite: false);
                owned = false;
            }
            else if (Link(temporary, destination) != 0)
            {
                // POSIX link installs the closed file only when the name is absent.
                // Do not fall back to an existence check followed by rename or copy.
                var errno = Marshal.GetLastPInvokeError();
                throw new IOException(errno == 17 // EEXIST
                    ? $"Destination already exists: {Quote(destination)}."
                    : $"Cannot publish the new disk without overwriting: {new Win32Exception(errno).Message}");
            }
        }
        finally
        {
            if (owned)
            {
                try { File.Delete(temporary); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    error.WriteLine($"{label}: could not remove temporary {Quote(temporary)}: {exception.Message}");
                }
            }
        }
    }

    internal static string Quote(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link([MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destination);
}
