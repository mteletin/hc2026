using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Hc2000.HostFiles;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hc2000.Machine;
using Hc2000.Machine.Io;
using Hc2000.Machine.Input;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;

namespace Hc2000.Tools;

/// <summary>Bounded offline observations over the delivered native paging devices.</summary>
public static class PagingCommand
{
    private const string Usage = """
        usage: hc paging inspect --profile hc2000-v2|hc2000-v2-paged-v1
          --diagnostic-roms BASIC16K CPM16K IF116K --operations OPS.json --max-ram-mib 1..1025 --json
        hc paging --help
        inspect currently requires Linux handle-safe ordinary-file input; help is portable.
        ROMs are caller-owned offline diagnostic stores, not a boot/runtime ROM composition.
        Exactly 16KiB each in BASIC/CPM/IF1 order; inputs are read-only; no guest execution or snapshots.
        Paged RAM allocates one true 0x40010000-byte arena and requires --max-ram-mib 1025;
        faithful RAM is 65536 bytes. The ceiling never selects a smaller allocation.
        OPS.json: {"v":1,"operations":[...]}, unsigned decimal integer fields only:
          out(port,value), in(port), read(address,count), write(address,value), m1(address),
          resolve(address), ram-read(offset,count), ram-write(offset,hex), reset(), frame(), pixel(x,y).
        Logical addresses/ports: 0..65535; bytes: 0..255; counts: 1..256; pixels: x 0..351, y 0..295.
        Physical ranges must fit selected RAM; hex is 1..256 bytes; pixel requires a preceding frame.
        Limits: 64KiB operations input, 128 operations, 4096 initialized/returned bytes each,
        four frames / 1422720 master ticks, 256KiB output; non-arena diagnostic payload <16MiB
        (not an RSS guarantee). One externally bounded process at a time with host memory headroom.
        """;

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args is ["--help"])
        {
            output.WriteLine(Usage);
            return 0;
        }
        try
        {
            var options = ParseOptions(args);
            if (!OperatingSystem.IsLinux())
                throw new PlatformNotSupportedException("UnsupportedPlatform: inspect requires Linux handle-safe input");
            var profile = options.Profile == "hc2000-v2-paged-v1"
                ? MachineProfile.Hc2000V2PagedV1.Memory : new MemoryProfile();
            int ramLength = profile.RamBytes;
            Require(ramLength <= (long)options.MaxRamMiB * 1024 * 1024, "RAM exceeds --max-ram-mib ceiling");
            var operationsBytes = ReadBounded(options.Operations, 65536);
            var operations = ParseOperations(operationsBytes, ramLength);
            var roms = options.Roms.Select(path => ReadBounded(path, 16384)).ToArray();
            Require(roms.All(bytes => bytes.Length == 16384), "each diagnostic ROM must be exactly 16384 bytes");
            var observations = Execute(profile, roms, operations);
            var json = JsonSerializer.Serialize(new
            {
                v = 1, diagnosticOnly = true,
                provenance = "offline diagnostic stores, not a boot/runtime ROM composition",
                profile = options.Profile, ramBytes = ramLength,
                inputHashes = new { basic = Hash(roms[0]), cpm = Hash(roms[1]), if1 = Hash(roms[2]) },
                operationsHash = Hash(operationsBytes), observations
            });
            Require(Encoding.UTF8.GetByteCount(json) <= 256 * 1024, "output budget exceeded");
            output.WriteLine(json);
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
            or JsonException or OutOfMemoryException or NotSupportedException
            or DllNotFoundException or EntryPointNotFoundException)
        {
            error.WriteLine($"hc paging: {ex.Message}");
            return 1;
        }
    }

    private sealed record Options(string Profile, string[] Roms, string Operations, int MaxRamMiB);
    private sealed record Operation(string Op, int A = 0, int B = 0, byte[]? Bytes = null);

    private static Options ParseOptions(string[] args)
    {
        Require(args.Length > 0 && args[0] == "inspect", "expected inspect (or --help)");
        string? profile = null, operations = null;
        string[]? roms = null;
        int max = 0;
        bool json = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 1; i < args.Length; i++)
        {
            string option = args[i];
            Require(seen.Add(option), $"duplicate option {option}");
            int count = option switch
            {
                "--profile" or "--operations" or "--max-ram-mib" => 1,
                "--diagnostic-roms" => 3, "--json" => 0,
                _ => throw new ArgumentException($"unknown option {option}")
            };
            Require(i + count < args.Length, $"missing argument for {option}");
            for (int j = 1; j <= count; j++)
                Require(!args[i + j].StartsWith("--", StringComparison.Ordinal), $"missing argument for {option}");
            switch (option)
            {
                case "--profile": profile = args[++i]; break;
                case "--operations": operations = args[++i]; break;
                case "--diagnostic-roms": roms = args[(i + 1)..(i + 4)]; i += 3; break;
                case "--max-ram-mib":
                    Require(int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out max)
                        && max is >= 1 and <= 1025, "--max-ram-mib must be an integer 1..1025");
                    break;
                case "--json": json = true; break;
            }
        }
        Require(profile is "hc2000-v2" or "hc2000-v2-paged-v1", "explicit supported --profile required");
        Require(operations != null && roms != null && max != 0 && json,
            "--diagnostic-roms, --operations, --max-ram-mib and --json are required");
        return new Options(profile!, roms!, operations!, max);
    }

    private static byte[] ReadBounded(string path, int limit)
    {
        Require(!path.Contains('\0'), "input path contains NUL");
        // Classify the owned descriptor before any read, without a blocking FIFO open or path reopen.
        const int atCurrentDirectory = -100, nonblock = 0x800, closeExec = 0x80000, noFollow = 0x20000;
        int descriptor = OpenAt(atCurrentDirectory, path, nonblock | closeExec | noFollow, 0);
        if (descriptor < 0) throw new IOException($"ordinary input open failed (errno {Marshal.GetLastPInvokeError()})");
        using var handle = new SafeFileHandle(descriptor, ownsHandle: true);
        var facts = LinuxImageAdmissionNative.Probe(handle);
        Require((facts.Mode & 0xf000) == 0x8000, "input must be an ordinary local file");
        Require(facts.Length <= (ulong)limit, $"input exceeds {limit}-byte limit");
        using var stream = new FileStream(handle, FileAccess.Read);
        byte[] bytes = new byte[(int)facts.Length];
        stream.ReadExactly(bytes);
        Require(stream.ReadByte() == -1, "input grew while reading");
        return bytes;
    }

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mode);

    private static List<Operation> ParseOperations(byte[] bytes, int ramLength)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        var root = Fields(document.RootElement, "v", "operations");
        Require(Number(root["v"], 1, 1) == 1, "unsupported operations version");
        Require(root["operations"].ValueKind == JsonValueKind.Array, "operations must be an array");
        Require(root["operations"].GetArrayLength() <= 128, "at most 128 operations");
        var result = new List<Operation>();
        int initialized = 0, returned = 0, frames = 0;
        foreach (var item in root["operations"].EnumerateArray())
        {
            Require(item.ValueKind == JsonValueKind.Object, "operation must be an object");
            Require(item.TryGetProperty("op", out var name) && name.ValueKind == JsonValueKind.String,
                "operation requires string op");
            string op = name.GetString()!;
            string[] names = op switch
            {
                "out" => ["op", "port", "value"], "in" => ["op", "port"],
                "read" => ["op", "address", "count"], "write" => ["op", "address", "value"],
                "m1" or "resolve" => ["op", "address"],
                "ram-read" => ["op", "offset", "count"], "ram-write" => ["op", "offset", "hex"],
                "reset" or "frame" => ["op"], "pixel" => ["op", "x", "y"],
                _ => throw new ArgumentException($"unknown operation {op}")
            };
            var fields = Fields(item, names);
            int a = 0, b = 0;
            byte[]? data = null;
            if (fields.TryGetValue("port", out var port)) a = Number(port, 0, 65535);
            if (fields.TryGetValue("address", out var address)) a = Number(address, 0, 65535);
            if (fields.TryGetValue("offset", out var offset)) a = Number(offset, 0, ramLength - 1);
            if (fields.TryGetValue("value", out var value)) b = Number(value, 0, 255);
            if (fields.TryGetValue("count", out var count)) b = Number(count, 1, 256);
            if (fields.TryGetValue("hex", out var hex))
            {
                Require(hex.ValueKind == JsonValueKind.String, "hex must be a string");
                string text = hex.GetString()!;
                Require(text.Length is >= 2 and <= 512 && text.Length % 2 == 0
                    && text.All(Uri.IsHexDigit), "hex must encode 1..256 bytes");
                data = Convert.FromHexString(text);
                initialized += data.Length;
            }
            if (op is "ram-read" or "ram-write")
                Require((long)a + (data?.Length ?? b) <= ramLength, "physical range exceeds RAM");
            if (op == "read") Require((long)a + b <= 65536, "logical read range wraps");
            if (op is "read" or "ram-read") returned += b;
            if (op is "in" or "m1") returned++;
            if (op == "frame") Require(++frames <= 4, "at most four frames");
            if (op == "pixel")
            {
                a = Number(fields["x"], 0, 351); b = Number(fields["y"], 0, 295);
                Require(frames > 0, "pixel requires a preceding frame");
                returned += sizeof(uint);
            }
            Require(initialized <= 4096 && returned <= 4096, "initialization/returned data budget exceeds 4096 bytes");
            result.Add(new Operation(op, a, b, data));
        }
        return result;
    }

    private static Dictionary<string, JsonElement> Fields(JsonElement element, params string[] names)
    {
        Require(element.ValueKind == JsonValueKind.Object, "expected object");
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in element.EnumerateObject())
            Require(names.Contains(field.Name, StringComparer.Ordinal) && result.TryAdd(field.Name, field.Value),
                $"unknown/duplicate field {field.Name}");
        Require(result.Count == names.Length, "missing required field");
        return result;
    }

    private static int Number(JsonElement element, int min, int max)
    {
        Require(element.ValueKind == JsonValueKind.Number && element.GetRawText().All(char.IsAsciiDigit)
            && element.TryGetInt32(out _), "expected unsigned decimal integer");
        int value = element.GetInt32();
        Require(value >= min && value <= max, $"integer outside {min}..{max}");
        return value;
    }

    private static List<Dictionary<string, object>> Execute(MemoryProfile profile, byte[][] roms, List<Operation> operations)
    {
        var memory = new HcMemory(profile, roms[0], roms[1], roms[2]);
        var scheduler = new DeviceScheduler();
        var clock = new FrameClock(scheduler, 1);
        var ula = new Ula(memory, new KeyboardMatrix(), scheduler, clock, 2);
        var idle = new PortDevice();
        var decoder = new PortDecoder(profile.RamPaged is not null
            ? HcIoProfile.WithPagedRam(memory, ula, idle, idle, idle, idle, idle)
            : HcIoProfile.Faithful(memory, ula, idle, idle, idle, idle, idle));
        var observations = new List<Dictionary<string, object>>();
        foreach (var operation in operations)
        {
            int a = operation.A, b = operation.B;
            var observation = new Dictionary<string, object> { ["index"] = observations.Count, ["op"] = operation.Op };
            switch (operation.Op)
            {
                case "out": decoder.Write((ushort)a, (byte)b, scheduler.Now); break;
                case "in": observation["value"] = decoder.Read((ushort)a, scheduler.Now); break;
                case "write": memory.Write((ushort)a, (byte)b); break;
                case "read":
                    var bytes = new byte[b];
                    for (int i = 0; i < b; i++) bytes[i] = memory.Read((ushort)(a + i));
                    observation["hex"] = Convert.ToHexString(bytes); break;
                case "m1": observation["value"] = memory.ReadM1((ushort)a); break;
                case "resolve":
                    var location = memory.Resolve((ushort)a);
                    observation["store"] = location.Store.ToString();
                    observation["offset"] = location.Offset;
                    observation["writable"] = location.Writable; break;
                case "ram-write": operation.Bytes!.AsSpan().CopyTo(memory.Ram.Span[a..]); break;
                case "ram-read": observation["hex"] = Convert.ToHexString(memory.Ram.Span.Slice(a, b)); break;
                case "reset": memory.ResetPaging(); break;
                case "frame":
                    scheduler.AdvanceTo(scheduler.Now + FrameTiming.FrameTicks);
                    observation["width"] = ula.LastFrame!.Width;
                    observation["height"] = ula.LastFrame.Height; break;
                case "pixel":
                    observation["argb"] = ula.LastFrame!.Pixels.Span[b * ula.LastFrame.Width + a];
                    observation["frameTick"] = ula.LastFrame.MasterTick; break;
            }
            observation["tick"] = scheduler.Now;
            observations.Add(observation);
        }
        return observations;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new ArgumentException(message);
    }
}
