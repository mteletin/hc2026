using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Hc2000.App.Audio;

/// <summary>Private SDL2 2.32.10 C ABI binding. No callbacks, video/event init,
/// global SDL_Quit, runtime downloads, or system/CWD library search.</summary>
public sealed class SdlQueuedAudioSink : IQueuedAudioSink
{
    public const string BackendVersion = "2.32.10";
    private readonly int owner = Environment.CurrentManagedThreadId;
    private readonly Api api;
    private IntPtr module;
    private uint device;
    private bool initialized;
    public int SampleRate { get; private set; }
    public int BlockSamples { get; private set; }
    public string Driver { get; private set; } = "";
    public string DeviceName { get; private set; } = "";
    public string BinaryPath { get; }
    public string BinarySha256 { get; }
    public ushort Format { get; private set; }
    public byte Channels { get; private set; }

    private SdlQueuedAudioSink(string path)
    {
        BinaryPath = path;
        BinarySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        module = NativeLibrary.Load(path);
        try { api = new Api(module); }
        catch { NativeLibrary.Free(module); module = IntPtr.Zero; throw; }
    }

    public static SdlQueuedAudioSink Open(int desiredFrequency = 48000)
    {
        if (desiredFrequency is not (44100 or 48000))
            throw new ArgumentOutOfRangeException(nameof(desiredFrequency));
        var (rid, file) = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ("win-x64", "SDL2.dll")
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? ("linux-x64", "libSDL2-2.0.so.0")
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ?
                (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64", "libSDL2.dylib")
            : throw new IOException("Unsupported native audio OS.");
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64 && rid != "osx-arm64")
            throw new IOException("Unsupported native audio architecture.");
        var path = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", file);
        if (!File.Exists(path)) throw new IOException($"Pinned SDL2 artifact not packaged: {path}");
        var sink = new SdlQueuedAudioSink(path);
        try
        {
            sink.api.GetVersion(out var version);
            if (version.Major != 2 || version.Minor != 32 || version.Patch != 10)
                throw new IOException($"Expected SDL2 {BackendVersion}, obtained {version.Major}.{version.Minor}.{version.Patch}.");
            if (sink.api.Init(0x10) != 0) throw sink.Error("SDL_InitSubSystem(AUDIO)");
            sink.initialized = true;
            var desired = new AudioSpec { Frequency = desiredFrequency, Format = FloatFormat, Channels = 1, Samples = 512 };
            sink.device = sink.api.OpenDevice(IntPtr.Zero, 0, ref desired, out var obtained, 1); // frequency change only
            if (sink.device == 0) throw sink.Error("SDL_OpenAudioDevice");
            ValidateObtained(obtained.Frequency, obtained.Format, obtained.Channels, obtained.Samples);
            sink.SampleRate = obtained.Frequency;
            sink.Format = obtained.Format;
            sink.Channels = obtained.Channels;
            sink.BlockSamples = obtained.Samples;
            sink.Driver = Text(sink.api.Driver());
            sink.DeviceName = Text(sink.api.DeviceName(0, 0)); // enumeration label, not a hardware-position claim
            return sink;
        }
        catch { sink.Dispose(); throw; }
    }

    public static ushort FloatFormat => BitConverter.IsLittleEndian ? (ushort)0x8120 : (ushort)0x9120;
    public static void ValidateObtained(int frequency, ushort format, byte channels, ushort samples)
    {
        if (frequency is not (44100 or 48000) || format != FloatFormat || channels != 1 || samples == 0)
            throw new IOException($"Unsupported obtained PCM: {frequency}Hz, 0x{format:x4}, {channels} channels, block {samples}.");
    }
    public static int AudioSpecSize => Marshal.SizeOf<AudioSpec>();
    public static int CallbackOffset => checked((int)Marshal.OffsetOf<AudioSpec>(nameof(AudioSpec.Callback)));

    public int QueuedSamples
    {
        get
        {
            RequireOpen();
            var bytes = api.GetQueued(device);
            if (bytes % sizeof(float) != 0 || bytes / sizeof(float) > int.MaxValue) throw new IOException("Invalid SDL queue byte count.");
            return (int)(bytes / sizeof(float));
        }
    }
    public void Queue(ReadOnlySpan<float> samples)
    {
        RequireOpen();
        if (samples.Length == 0) return;
        var copy = samples.ToArray();
        var pinned = GCHandle.Alloc(copy, GCHandleType.Pinned);
        try
        {
            if (api.Queue(device, pinned.AddrOfPinnedObject(), checked((uint)copy.Length * sizeof(float))) != 0)
                throw Error("SDL_QueueAudio");
        }
        finally { pinned.Free(); }
    }
    public void Pause(bool paused) { RequireOpen(); api.Pause(device, paused ? 1 : 0); }
    public void Clear() { RequireOpen(); api.Clear(device); }
    private IOException Error(string operation) => new($"{operation}: {Text(api.GetError())}");
    private static string Text(IntPtr pointer) => Marshal.PtrToStringUTF8(pointer) ?? "unknown";
    private void RequireOwner()
    {
        if (Environment.CurrentManagedThreadId != owner) throw new InvalidOperationException("SDL audio belongs to the emulation worker.");
    }
    private void RequireOpen() { RequireOwner(); ObjectDisposedException.ThrowIf(device == 0, this); }
    public void Dispose()
    {
        RequireOwner();
        if (device != 0) { api.Pause(device, 1); api.Clear(device); api.Close(device); device = 0; }
        if (initialized) { api.Quit(0x10); initialized = false; }
        if (module != IntPtr.Zero) { NativeLibrary.Free(module); module = IntPtr.Zero; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioSpec
    {
        public int Frequency;
        public ushort Format;
        public byte Channels, Silence;
        public ushort Samples, Padding;
        public uint Size;
        public IntPtr Callback, UserData;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct SdlVersion { public byte Major, Minor, Patch; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitFn(uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void QuitFn(uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void VersionFn(out SdlVersion version);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr TextFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr DeviceNameFn(int index, int capture);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint OpenFn(IntPtr name, int capture, ref AudioSpec desired, out AudioSpec obtained, int changes);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void CloseFn(uint device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void PauseFn(uint device, int paused);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int QueueFn(uint device, IntPtr samples, uint bytes);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint GetQueuedFn(uint device);
    private sealed class Api
    {
        public readonly InitFn Init;
        public readonly QuitFn Quit;
        public readonly VersionFn GetVersion;
        public readonly TextFn GetError, Driver;
        public readonly DeviceNameFn DeviceName;
        public readonly OpenFn OpenDevice;
        public readonly CloseFn Close, Clear;
        public readonly PauseFn Pause;
        public readonly QueueFn Queue;
        public readonly GetQueuedFn GetQueued;
        public Api(IntPtr module)
        {
            T Bind<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(module, name));
            Init = Bind<InitFn>("SDL_InitSubSystem"); Quit = Bind<QuitFn>("SDL_QuitSubSystem");
            GetVersion = Bind<VersionFn>("SDL_GetVersion"); GetError = Bind<TextFn>("SDL_GetError");
            Driver = Bind<TextFn>("SDL_GetCurrentAudioDriver"); DeviceName = Bind<DeviceNameFn>("SDL_GetAudioDeviceName");
            OpenDevice = Bind<OpenFn>("SDL_OpenAudioDevice"); Close = Bind<CloseFn>("SDL_CloseAudioDevice");
            Clear = Bind<CloseFn>("SDL_ClearQueuedAudio"); Pause = Bind<PauseFn>("SDL_PauseAudioDevice");
            Queue = Bind<QueueFn>("SDL_QueueAudio"); GetQueued = Bind<GetQueuedFn>("SDL_GetQueuedAudioSize");
        }
    }
}
