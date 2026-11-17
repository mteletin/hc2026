using System.Net;
using System.Security.Cryptography;
using Hc2000.App.Media;
using Hc2000.Machine.Storage;

namespace Hc2000.App.Network;

internal enum CpNetResourceFormat { Dsk, RawFloppy, HddVolume, PagedMVolume }

// Per-machine configuration only; it never starts work or persists in a profile/snapshot.
internal sealed class CpNetServerSettings : IDisposable
{
    private byte[] login = [];
    private char[] identityPassword = [];
    private bool identityPasswordValid = true;
    private bool disposed;

    public string ImagePath { get; set; } = "";
    public string IdentityPath { get; set; } = "";
    public string Address { get; set; } = "127.0.0.1";
    public string Port { get; set; } = "";
    public string ServerId { get; set; } = "1";
    public string ResourceDisk { get; set; } = "0";
    public CpNetResourceFormat Format { get; set; } = CpNetResourceFormat.Dsk;
    public int FloppyPreset { get; set; }
    public bool LoginConfigured => login.Length == 8;
    public bool IdentityConfigured => !string.IsNullOrWhiteSpace(IdentityPath);

    public void SetLoginPassword(ReadOnlySpan<char> value)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        CryptographicOperations.ZeroMemory(login);
        login = [];
        if (value.Length != 8) return;
        var bytes = new byte[8];
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] is < (char)32 or > (char)126)
            {
                CryptographicOperations.ZeroMemory(bytes);
                return;
            }
            bytes[index] = (byte)value[index];
        }
        if (!bytes.AsSpan().ContainsAnyExcept((byte)' '))
        {
            CryptographicOperations.ZeroMemory(bytes);
            return;
        }
        login = bytes;
    }

    public void SetIdentityPassword(ReadOnlySpan<char> value)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Array.Clear(identityPassword);
        identityPasswordValid = value.Length <= 256;
        identityPassword = identityPasswordValid ? value.ToArray() : [];
    }

    public bool CanStart => !disposed && TryResolve(out _, out _, out _, out _, out _);

    public CpNetServerActivation CreateActivation()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!TryResolve(out var endpoint, out var serverId, out var resourceDisk, out var descriptor, out var parameters))
            throw new InvalidOperationException("Complete the server resource, identity, address, port and eight-byte LOGIN password.");
        return new(ImagePath, descriptor!, parameters!, IdentityPath, identityPassword, login, endpoint!, serverId, resourceDisk);
    }

    private bool TryResolve(out IPEndPoint? endpoint, out byte serverId, out int resourceDisk,
        out MediaImageDescriptor? descriptor, out Dpb? parameters)
    {
        endpoint = null; serverId = 0; resourceDisk = 0; descriptor = null; parameters = null;
        if (string.IsNullOrWhiteSpace(ImagePath) || ImagePath.Length > 4096 || ImagePath.Contains('\0') ||
            string.IsNullOrWhiteSpace(IdentityPath) || IdentityPath.Length > 4096 || IdentityPath.Contains('\0') ||
            !LoginConfigured || !identityPasswordValid ||
            !IPAddress.TryParse(Address, out var address) || !CpNetTlsConnection.IsUnicast(address) ||
            !int.TryParse(Port, out var port) || port is < 1 or > 65535 ||
            !byte.TryParse(ServerId, out serverId) || serverId == 0xff ||
            !int.TryParse(ResourceDisk, out resourceDisk) || resourceDisk is < 0 or > 15 ||
            !Enum.IsDefined(Format)) return false;
        endpoint = new(address, port);
        if (Format is CpNetResourceFormat.Dsk or CpNetResourceFormat.RawFloppy)
        {
            var presets = Dpb.Presets();
            if (FloppyPreset < 0 || FloppyPreset >= presets.Count) return false;
            parameters = presets[FloppyPreset];
            descriptor = Format == CpNetResourceFormat.Dsk ? MediaImageDescriptor.DskContainer : MediaImageDescriptor.RawFloppy(parameters.Geometry);
        }
        else
        {
            descriptor = MediaImageDescriptor.HddRaw8MiB;
            parameters = Format == CpNetResourceFormat.HddVolume ? Dpb.HddVolume() : Dpb.MVolumeV2();
        }
        return true;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        CryptographicOperations.ZeroMemory(login);
        Array.Clear(identityPassword);
        login = [];
        identityPassword = [];
    }
}
