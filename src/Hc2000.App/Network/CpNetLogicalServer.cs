using System.Buffers.Binary;
using System.Text;
using System.Security.Cryptography;
using Hc2000.Machine.Network;
using Hc2000.Machine.Storage;

namespace Hc2000.App.Network;

internal enum CpNetAuthenticationTransition { None, LoginAccepted, LoginRejected, LoggedOff }

// Unconnected read-only prototype; a real guest path and several response semantics remain
// unverified. File operations use FCB values, not MP/M open handles or lock identities.
internal sealed class CpNetLogicalServer : IDisposable
{
    private readonly CpmFs resource;
    private readonly byte[] password;
    private readonly int resourceDisk;
    private (byte Requester, byte Server)? login;
    private int currentDisk;
    private bool searchArmed;
    private int searchUser;
    private int searchPosition = -1;
    private byte[]? searchPattern;
    private bool disposed;

    // CP/M 2.2 FCB field offsets (manual CH5 Figure 5-2).
    private const int FcbName = 1, FcbNameLen = 8, FcbExt = 9, FcbExtLen = 3,
        FcbEx = 12, FcbS2 = 14, FcbCr = 32, FcbR0 = 33, FcbR1 = 34, FcbR2 = 35, FcbLen = 36;
    // CP/M 2.2 directory entry (32 bytes).
    private const int DirUser = 0, DirEntryLen = 32, DirFree = 0xe5;
    private const int Wildcard = 0x3f;
    // Return / directory / error codes (CP/M 2.2 CH5): directory "not found" = 0xFF; READ
    // errors per Function 33: 0x01 reading unwritten data (used for both sequential and random
    // end of file; Function 20 only requires a nonzero value), 0x06 seek past the physical end
    // of disk (a nonzero random r2). Extended error code 0x0C = function not implemented.
    private const byte Ok = 0, DirNotFound = 0xff, ReadUnwrittenData = 0x01, SeekUnwrittenExtent = 0x04, SeekPastEnd = 0x06, ExtErrorTag = 0xff, ErrNotImplemented = 0x0c;
    private const int ReadResponseBytes = 165; // retcode + 36 FCB + 128 data (data at 0x25..0xA4)
    private const int ReadDataOffset = 0x25;

    public CpNetLogicalServer(CpmFs resource, ReadOnlySpan<byte> password, int resourceDisk = 0)
    {
        this.resource = resource ?? throw new ArgumentNullException(nameof(resource));
        if (password.Length != 8) throw new ArgumentException("A CP/NET password must be eight bytes.", nameof(password));
        if (resourceDisk is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(resourceDisk));
        this.password = password.ToArray();
        this.resourceDisk = resourceDisk;
        currentDisk = resourceDisk;
    }

    /// <summary>The disk selected by the last SELECT DISK (informational; single resource).</summary>
    public int CurrentDisk => currentDisk;
    public bool IsAuthenticated => !disposed && login.HasValue;
    public CpNetAuthenticationTransition AuthenticationTransition { get; private set; }

    /// <summary>Handle one request frame (FMT 00). Returns null when the frame is not a request
    /// this server answers; never throws for a malformed request (answers with an extended error).</summary>
    public CpNetFrame? Handle(CpNetFrame request)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        AuthenticationTransition = CpNetAuthenticationTransition.None;
        if (request.Format != 0) return null;
        var p = request.Payload;
        if (request.Function == 0x40) return Login(request, p);
        if (login != (request.Source, request.Destination)) return NotImplemented(request);
        if (request.Function == 0x41) return Logoff(request, p);
        try
        {
            return request.Function switch
            {
                0x0e => SelectDisk(request, p),
                0x0f => OpenFile(request, p),
                0x10 => CloseFile(request, p),
                0x11 => SearchFirst(request, p),
                0x12 => SearchNext(request, p),
                0x13 or 0x16 or 0x17 or 0x1e => ReadOnlyMutation(request, p, 37),
                0x14 => ReadSequential(request, p),
                0x15 or 0x22 or 0x28 => ReadOnlyMutation(request, p, 165),
                0x18 or 0x1d => GetDriveVector(request, p),
                0x1b => GetAllocationVector(request, p),
                0x1c => ProtectDisk(request, p),
                0x1f => GetDiskParameters(request, p),
                0x21 => ReadRandom(request, p),
                0x23 => ComputeFileSize(request, p),
                0x24 => SetRandomRecord(request, p),
                0x25 => ResetDrive(request, p),
                0x27 => p.Length == 2 ? BuildResponse(request, new byte[] { Ok }) : NotImplemented(request),
                0x2a or 0x2b => BuildResponse(request, new byte[] { ExtErrorTag, ErrNotImplemented }, 0),
                0x42 or 0x43 or 0x44 or 0x45 => BuildResponse(request, new byte[] { ExtErrorTag, ErrNotImplemented }, 0),
                _ => NotImplemented(request),
            };
        }
        catch (ArgumentException)
        {
            return NotImplemented(request);
        }
    }

    private CpNetFrame Login(CpNetFrame request, ReadOnlySpan<byte> payload)
    {
        var requester = (request.Source, request.Destination);
        if (payload.Length != 8 || !CryptographicOperations.FixedTimeEquals(payload, password) ||
            (login.HasValue && login.Value != requester))
        {
            AuthenticationTransition = CpNetAuthenticationTransition.LoginRejected;
            return BuildResponse(request, new byte[] { DirNotFound });
        }
        if (!login.HasValue) AuthenticationTransition = CpNetAuthenticationTransition.LoginAccepted;
        login = requester;
        return BuildResponse(request, new byte[] { Ok });
    }

    private CpNetFrame Logoff(CpNetFrame request, ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 1) return NotImplemented(request);
        login = null;
        AuthenticationTransition = CpNetAuthenticationTransition.LoggedOff;
        searchPattern = null;
        searchPosition = -1;
        searchArmed = false;
        currentDisk = resourceDisk;
        return BuildResponse(request, new byte[] { Ok });
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        CryptographicOperations.ZeroMemory(password);
        login = null;
        searchPattern = null;
        searchArmed = false;
        AuthenticationTransition = CpNetAuthenticationTransition.None;
    }

    private CpNetFrame SelectDisk(CpNetFrame req, ReadOnlySpan<byte> p)
    {
        if (p.Length < 1) return NotImplemented(req);
        if (p[0] != resourceDisk) return DriveError(req);
        currentDisk = p[0];
        return BuildResponse(req, new byte[] { Ok });
    }

    private CpNetFrame ResetDrive(CpNetFrame req, ReadOnlySpan<byte> p)
    {
        if (p.Length != 2) return NotImplemented(req);
        if ((BinaryPrimitives.ReadUInt16LittleEndian(p) & (1 << resourceDisk)) != 0)
        {
            searchPattern = null;
            searchPosition = -1;
            searchArmed = false;
        }
        return BuildResponse(req, new byte[] { Ok });
    }

    private CpNetFrame GetDriveVector(CpNetFrame req, ReadOnlySpan<byte> p)
    {
        if (p.Length != 1) return NotImplemented(req);
        var vector = (ushort)(1 << resourceDisk);
        return BuildResponse(req, new byte[] { (byte)vector, (byte)(vector >> 8) });
    }

    private CpNetFrame GetAllocationVector(CpNetFrame req, ReadOnlySpan<byte> p)
    {
        if (p.Length != 1) return NotImplemented(req);
        if (p[0] != resourceDisk) return DriveError(req);
        var dpb = resource.Parameters;
        if (dpb.Dsm is < 0 or > 2047) return NotImplemented(req);
        var allocation = new byte[256];
        allocation[0] = dpb.Al0;
        allocation[1] = dpb.Al1;
        for (var block = resource.MaximumBlock + 1; block <= dpb.Dsm; block++)
            allocation[block / 8] |= (byte)(0x80 >> (block % 8));
        for (var index = 0; index < resource.DirectoryEntryCount; index++)
        {
            var entry = resource.GetDirectoryEntry(index);
            if (entry[0] == 0xe5) continue;
            for (var offset = 16; offset < 32; offset += dpb.WordAllocations ? 2 : 1)
            {
                var block = dpb.WordAllocations ? BinaryPrimitives.ReadUInt16LittleEndian(entry.AsSpan(offset, 2)) : entry[offset];
                if (block != 0) allocation[block / 8] |= (byte)(0x80 >> (block % 8));
            }
        }
        return BuildResponse(req, allocation);
    }

    private CpNetFrame ProtectDisk(CpNetFrame req, ReadOnlySpan<byte> p)
    {
        if (p.Length != 1) return NotImplemented(req);
        return p[0] == resourceDisk ? BuildResponse(req, new byte[] { Ok }) : DriveError(req);
    }

    private CpNetFrame GetDiskParameters(CpNetFrame req, ReadOnlySpan<byte> p)
    {
        if (p.Length != 1) return NotImplemented(req);
        if (p[0] != resourceDisk) return DriveError(req);
        var dpb = resource.Parameters;
        if (dpb.Spt is < 0 or > ushort.MaxValue || dpb.Bsh is < 0 or > byte.MaxValue ||
            dpb.Blm is < 0 or > byte.MaxValue || dpb.Exm is < 0 or > byte.MaxValue ||
            dpb.Dsm is < 0 or > ushort.MaxValue || dpb.Drm is < 0 or > ushort.MaxValue ||
            dpb.Cks is < 0 or > ushort.MaxValue || dpb.Off is < 0 or > ushort.MaxValue)
            return NotImplemented(req);
        var data = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0), (ushort)dpb.Spt);
        data[2] = (byte)dpb.Bsh;
        data[3] = (byte)dpb.Blm;
        data[4] = (byte)dpb.Exm;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(5), (ushort)dpb.Dsm);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(7), (ushort)dpb.Drm);
        data[9] = dpb.Al0;
        data[10] = dpb.Al1;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(11), (ushort)dpb.Cks);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(13), (ushort)dpb.Off);
        return BuildResponse(req, data);
    }

    private CpNetFrame OpenFile(CpNetFrame req, ReadOnlySpan<byte> p)
    {
        if (p.Length < 45) return NotImplemented(req);
        if (!IsResourceDrive(p[1], currentDisk)) return DriveError(req);
        var user = p[0];
        var fcb = p.Slice(1, FcbLen).ToArray();
        var name = FcbName83(fcb);
        var response = new byte[1 + FcbLen];
        if (!LoadExtent(fcb, name, user, LogicalExtent(fcb)))
        {
            response[0] = DirNotFound;
            fcb.CopyTo(response, 1);
            return BuildResponse(req, response);
        }
        response[0] = Ok;
        fcb.CopyTo(response, 1);
        return BuildResponse(req, response);
    }

    private CpNetFrame CloseFile(CpNetFrame req, ReadOnlySpan<byte> p)
    {
        if (p.Length < 45) return NotImplemented(req);
        if (!IsResourceDrive(p[1], currentDisk)) return DriveError(req);
        var user = p[0];
        var fcb = p.Slice(1, FcbLen).ToArray();
        var response = new byte[1 + FcbLen];
        response[0] = resource.GetVirtualRecordCount(FcbName83(fcb), user) is null ? DirNotFound : Ok;
        fcb.CopyTo(response, 1);
        return BuildResponse(req, response);
    }

    private CpNetFrame SearchFirst(CpNetFrame req, ReadOnlySpan<byte> p)
    {
        if (p.Length < 38) return NotImplemented(req);
        searchArmed = false;
        if (!IsResourceDrive(p[2], p[0])) return DriveError(req);
        var user = p[1];
        searchUser = user;
        searchPattern = p.Slice(2 + FcbName, 11).ToArray();
        searchArmed = true;
        searchPosition = -1;
        for (var i = 0; i < resource.DirectoryEntryCount; i++)
        {
            var entry = resource.GetDirectoryEntry(i);
            if (entry[DirUser] != user || entry[DirUser] == DirFree) continue;
            if (!Matches(entry, searchPattern)) continue;
            searchPosition = i;
            return SearchResponse(req, entry);
        }
        searchArmed = false;
        return SearchNoMatch(req);
    }

    private CpNetFrame SearchNext(CpNetFrame req, ReadOnlySpan<byte> p)
    {
        if (p.Length < 2 || searchPattern is null || !searchArmed || p[1] != searchUser) return SearchNoMatch(req);
        var user = p[1];
        for (var i = searchPosition + 1; i < resource.DirectoryEntryCount; i++)
        {
            var entry = resource.GetDirectoryEntry(i);
            if (entry[DirUser] != user || entry[DirUser] == DirFree) continue;
            if (!Matches(entry, searchPattern)) continue;
            searchPosition = i;
            return SearchResponse(req, entry);
        }
        searchArmed = false;
        return SearchNoMatch(req);
    }

    private CpNetFrame ReadSequential(CpNetFrame req, ReadOnlySpan<byte> p)
    {
        if (p.Length < 37) return NotImplemented(req);
        if (!IsResourceDrive(p[1], currentDisk)) return DriveError(req);
        var fcb = p.Slice(1, FcbLen).ToArray();
        var name = FcbName83(fcb);
        var absolute = LogicalExtent(fcb) * 128 + fcb[FcbCr];
        if (absolute > 65535) return ReadResponse(req, fcb, ReadUnwrittenData, null);
        var data = new byte[128];
        if (resource.ReadRecord(name, p[0], absolute, data) != CpmRecordReadResult.Success)
            return ReadResponse(req, fcb, ReadUnwrittenData, null);
        var next = absolute + 1;
        SetLogicalExtent(fcb, next / 128);
        fcb[FcbCr] = (byte)(next % 128);
        if (next % 128 == 0) LoadExtent(fcb, name, p[0], next / 128);
        return ReadResponse(req, fcb, Ok, data);
    }

    private CpNetFrame ReadRandom(CpNetFrame req, ReadOnlySpan<byte> p)
    {
        if (p.Length < 37) return NotImplemented(req);
        if (!IsResourceDrive(p[1], currentDisk)) return DriveError(req);
        var fcb = p.Slice(1, FcbLen).ToArray();
        var name = FcbName83(fcb);
        if (fcb[FcbR2] != 0) return ReadResponse(req, fcb, SeekPastEnd, null);
        var absolute = fcb[FcbR0] | (fcb[FcbR1] << 8);
        var data = new byte[128];
        var result = resource.ReadRecord(name, p[0], absolute, data);
        if (result == CpmRecordReadResult.MissingExtent) return ReadResponse(req, fcb, SeekUnwrittenExtent, null);
        if (result == CpmRecordReadResult.Unwritten) return ReadResponse(req, fcb, ReadUnwrittenData, null);
        // A random read sets the logical extent and current record to the read position:
        // a following sequential read rereads the last randomly read record (Function 33).
        LoadExtent(fcb, name, p[0], absolute / 128);
        fcb[FcbCr] = (byte)(absolute % 128);
        return ReadResponse(req, fcb, Ok, data);
    }

    private CpNetFrame ComputeFileSize(CpNetFrame req, ReadOnlySpan<byte> p)
    {
        if (p.Length < 37) return NotImplemented(req);
        if (!IsResourceDrive(p[1], currentDisk)) return DriveError(req);
        var user = p[0];
        var fcb = p.Slice(1, FcbLen).ToArray();
        var records = resource.GetVirtualRecordCount(FcbName83(fcb), user);
        var response = new byte[1 + FcbLen];
        if (records is null)
        {
            response[0] = DirNotFound;
            fcb.CopyTo(response, 1);
            return BuildResponse(req, response);
        }
        // Virtual size: the record address of the record following the end of the file.
        fcb[FcbR0] = (byte)records.Value; fcb[FcbR1] = (byte)(records.Value >> 8); fcb[FcbR2] = (byte)(records.Value >> 16);
        response[0] = Ok;
        fcb.CopyTo(response, 1);
        return BuildResponse(req, response);
    }

    private CpNetFrame SetRandomRecord(CpNetFrame req, ReadOnlySpan<byte> p)
    {
        if (p.Length < 37) return NotImplemented(req);
        if (!IsResourceDrive(p[1], currentDisk)) return DriveError(req);
        var fcb = p.Slice(1, FcbLen).ToArray();
        var record = LogicalExtent(fcb) * 128 + fcb[FcbCr];
        fcb[FcbR0] = (byte)record;
        fcb[FcbR1] = (byte)(record >> 8);
        fcb[FcbR2] = (byte)(record >> 16);
        var response = new byte[1 + FcbLen];
        fcb.CopyTo(response, 1);
        return BuildResponse(req, response);
    }

    private static int LogicalExtent(byte[] fcb) => (fcb[FcbEx] & 0x1f) | ((fcb[FcbS2] & 0x3f) << 5);

    private bool LoadExtent(byte[] fcb, string name, int user, int extent)
    {
        if (!resource.TryGetFileExtent(name, user, extent, out var entry, out var records)) return false;
        SetLogicalExtent(fcb, extent);
        fcb[13] = 0;
        fcb[15] = (byte)records;
        entry.AsSpan(16, 16).CopyTo(fcb.AsSpan(16, 16));
        return true;
    }

    private static void SetLogicalExtent(byte[] fcb, int extent)
    {
        fcb[FcbEx] = (byte)(extent & 0x1f);
        fcb[FcbS2] = (byte)((fcb[FcbS2] & 0xc0) | ((extent >> 5) & 0x3f));
    }

    private static CpNetFrame ReadResponse(CpNetFrame req, byte[] fcb, byte retcode, byte[]? data)
    {
        var response = new byte[ReadResponseBytes];
        response[0] = retcode;
        fcb.CopyTo(response, 1);
        data?.AsSpan().CopyTo(response.AsSpan(ReadDataOffset, 128));
        return BuildResponse(req, response);
    }

    private static CpNetFrame SearchResponse(CpNetFrame req, byte[] entry)
    {
        var response = new byte[1 + DirEntryLen];
        response[0] = Ok;
        entry.CopyTo(response, 1);
        return BuildResponse(req, response);
    }

    private static CpNetFrame SearchNoMatch(CpNetFrame req)
    {
        var response = new byte[1 + DirEntryLen];
        response[0] = DirNotFound;
        return BuildResponse(req, response);
    }

    private static CpNetFrame NotImplemented(CpNetFrame req)
    {
        // Extended error: [0xFF, 0x0C] = function not implemented on the server.
        return BuildResponse(req, new byte[] { ExtErrorTag, ErrNotImplemented }, 0);
    }

    private bool IsResourceDrive(byte fcbDrive, int defaultDisk) =>
        (fcbDrive == 0 ? defaultDisk : fcbDrive - 1) == resourceDisk;

    private static CpNetFrame DriveError(CpNetFrame req) => BuildResponse(req, new byte[] { ExtErrorTag, 0x04 }, 0);

    private CpNetFrame ReadOnlyMutation(CpNetFrame req, ReadOnlySpan<byte> payload, int minimumLength)
    {
        if (payload.Length < minimumLength) return NotImplemented(req);
        return IsResourceDrive(payload[1], currentDisk)
            ? BuildResponse(req, new byte[] { ExtErrorTag, 0x02 }, 0) : DriveError(req);
    }

    // Match an 11-byte name+type pattern (CP/M '?') against a directory entry's bytes 1..11.
    private static bool Matches(byte[] entry, byte[] pattern)
    {
        for (var i = 0; i < 11; i++)
        {
            var pat = pattern[i] & 0x7f;
            if (pat == Wildcard) continue;
            if (pat != (entry[1 + i] & 0x7f)) return false;
        }
        return true;
    }

    // The FCB name+type as a CP/M 8.3 string (high bits cleared, space-trimmed).
    private static string FcbName83(ReadOnlySpan<byte> fcb)
    {
        var stem = new byte[FcbNameLen];
        fcb.Slice(FcbName, FcbNameLen).CopyTo(stem);
        var ext = new byte[FcbExtLen];
        fcb.Slice(FcbExt, FcbExtLen).CopyTo(ext);
        for (var i = 0; i < stem.Length; i++) stem[i] &= 0x7f;
        for (var i = 0; i < ext.Length; i++) ext[i] &= 0x7f;
        var s = Encoding.ASCII.GetString(stem).TrimEnd(' ');
        var e = Encoding.ASCII.GetString(ext).TrimEnd(' ');
        return e.Length == 0 ? s : s + "." + e;
    }

    private static CpNetFrame BuildResponse(CpNetFrame request, ReadOnlySpan<byte> data, byte? function = null)
    {
        if (data.Length is < 1 or > CpNetFrame.MaxPayloadBytes)
            throw new ArgumentException("CP/NET response payload out of range.");
        var bytes = new byte[CpNetFrame.HeaderBytes + data.Length];
        bytes[0] = 1;
        bytes[1] = request.Source;      // DID = the requester
        bytes[2] = request.Destination; // SID = the server
        bytes[3] = function ?? request.Function;
        bytes[4] = (byte)(data.Length - 1);
        data.CopyTo(bytes.AsSpan(CpNetFrame.HeaderBytes));
        return CpNetFrame.TryParse(bytes, out var frame, out var error)
            ? frame!
            : throw new ArgumentException($"CP/NET response frame invalid: {error}.");
    }
}
