extern alias tools;
extern alias app;

using System.Text.Json;
using AppAdmission = app::Hc2000.HostFiles.NativeImageAdmission;
using ToolAdmission = tools::Hc2000.HostFiles.NativeImageAdmission;
using Known = tools::Hc2000.HostFiles.KnownImagePublication;
using Storage = tools::Hc2000.Tools.StorageCommand;

// Test executable only. No default domain: every invocation MUST supply an absolute
// private fixture root. stdin/stdout barriers order actual native operations, not sleeps.
if (args.Length < 2 || !Path.IsPathFullyQualified(args[1])) return 64;
using var toolScope = ToolAdmission.EnterPrivateTestDomain(args[1]);
using var appScope = AppAdmission.EnterPrivateTestDomain(args[1]);
static void Emit(object value) { Console.WriteLine(JsonSerializer.Serialize(value)); Console.Out.Flush(); }
static void EmitHeld(string domain, string host)
{
    var fd = NativeObservation.Open(Path.Combine(domain, "gate"), 0x80000 | 0x20000);
    if (fd < 0) throw new IOException("Cannot observe private gate inode.");
    using var gate = new Microsoft.Win32.SafeHandles.SafeFileHandle((IntPtr)fd, ownsHandle: true);
    var id = tools::Hc2000.HostFiles.LinuxImageAdmissionNative.Probe(gate).Id;
    Emit(new { status = "held", host, id.DeviceMajor, id.DeviceMinor, id.Inode });
}
static void DrainCommand()
{
    var line = Console.In.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(25)).GetAwaiter().GetResult();
    if (line != "drain") throw new InvalidDataException("Expected bounded owner drain command.");
}
try
{
    switch (args[0])
    {
        case "read":
        case "pending-read":
        {
            var result = AppAdmission.TryAcquireColdRead(default);
            using var admission = result.Admission;
            if (admission is null) { Emit(new { status = "refused", reason = result.Refusal.ToString() }); return 2; }
            var candidate = Guid.NewGuid();
            if (args[0] == "pending-read") admission.MarkColdOwnerPending(candidate);
            EmitHeld(args[1], "App");
            DrainCommand();
            if (args[0] == "pending-read") admission.CompleteColdOwnerAfterDrain(candidate);
            Emit(new { status = "drained" });
            return 0;
        }
        case "publication":
        case "unknown":
        {
            if (args.Length != 3) return 64;
            var result = ToolAdmission.TryBeginPublication(args[2], Guid.NewGuid(), default);
            using var admission = result.Admission;
            if (admission is null) { Emit(new { status = "refused", reason = result.Refusal.ToString() }); return 2; }
            if (args[0] == "unknown") admission.RetainUnknownPublication();
            EmitHeld(args[1], "Tools");
            DrainCommand();
            if (args[0] == "publication") admission.CompleteKnownPublicationAfterDrain(Known.Unpublished);
            Emit(new { status = "drained" });
            return 0;
        }
        case "offline":
        {
            if (args.Length != 5) return 64;
            var request = JsonSerializer.Deserialize<Storage.OfflineReplaceRequest>(File.ReadAllText(args[2]))
                ?? throw new InvalidDataException("Missing private request.");
            var payload = File.ReadAllBytes(args[3]);
            var point = Enum.Parse<Storage.OfflineIoPoint>(args[4]);
            using var attempt = Storage.ReplaceOffline(request, payload, default, io =>
            {
                if (io.Point != point) return;
                Emit(new { status = "held", point = point.ToString() });
                DrainCommand();
            });
            Emit(new { status = "drained", outcome = attempt.Receipt.Outcome.ToString(), error = attempt.Receipt.Error.ToString(), attempt.Receipt.WriterDrained });
            return 0;
        }
        default: return 64;
    }
}
catch (Exception ex) { Emit(new { status = "error", error = ex.GetType().Name, ex.Message }); return 1; }

internal static class NativeObservation
{
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "open", SetLastError = true)]
    internal static extern int Open(string path, int flags);
}
