using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Hc2000.Machine.Storage;

namespace Hc2000.App.Network.Sftp;

/// <summary>An SFTP version 3 file server (draft-ietf-secsh-filexfer-02) over one stream, serving an
/// <see cref="SftpShareSet"/> as the fixed tree of hc2000-drive-sharing-sftp-v1 §2-5: <c>/A</c>, <c>/C</c> for the shared
/// drives, <c>/u0</c>, <c>/u1</c> under each for the enabled user areas, the CP/M files in them as upper-case 8.3 names.
/// A CP/M R/O file is mode 0444, every time is 0, and a size is the file's records times 128. A write fills a buffer
/// (at most <see cref="MaximumFileBytes"/>) and the directory changes once, at CLOSE, through one <c>CpmFs.Put</c>, so a
/// half-written file is in no listing and no image; only STAT of its own path answers from the buffer, which sshfs needs
/// to create a file. SETSTAT and FSETSTAT accept times and modes and change nothing (the CP/M R/O attribute stays the
/// authority); a size truncates or extends an open write buffer, committed at its CLOSE like a write. Requests outside
/// the v3 subset answer SSH_FX_OP_UNSUPPORTED; a malformed one ends the session with
/// <see cref="EndReason"/>. The server opens no socket: the caller hands it the forwarded channel's stream and closes
/// that channel when <see cref="RunAsync"/> returns.</summary>
public sealed class SftpServer
{
    /// <summary>The most one write handle buffers before CLOSE commits it (contract §4): larger writes are refused.</summary>
    public const int MaximumFileBytes = 4 * 1024 * 1024;

    /// <summary>The largest packet taken, as OpenSSH's own server: 256 KiB, plus room for a WRITE's header.</summary>
    public const int MaximumPacketBytes = 256 * 1024 + 1024;

    private const int MaximumReadBytes = 64 * 1024, MaximumHandles = 64, NamesPerReaddir = 64;
    private readonly SftpShareSet shares;
    private readonly Action<string>? log;
    private readonly Dictionary<string, Handle> handles = new(StringComparer.Ordinal);
    private int started;
    private bool initialized;
    private long nextHandle;

    public SftpServer(SftpShareSet shares, Action<string>? log = null)
    {
        this.shares = shares ?? throw new ArgumentNullException(nameof(shares));
        this.log = log;
    }

    /// <summary>Why the session ended on the server's side (a malformed request, a refused version); null while it runs,
    /// and after the client ended the stream or the caller cancelled.</summary>
    public string? EndReason { get; private set; }

    /// <summary>Serves one session until the client ends the stream (it returns), a malformed request ends it (it returns
    /// with <see cref="EndReason"/>), the caller cancels (OperationCanceledException) or the stream fails (its own
    /// exception). However it ends, an unfinished write is discarded, never committed, and the log says so.</summary>
    public async Task RunAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException("An SFTP server serves one session.");
        log?.Invoke(string.Create(CultureInfo.InvariantCulture,
            $"SFTP v3 serving {string.Join("; ", shares.Shares)}; files up to {MaximumFileBytes / (1024 * 1024)} MiB, each committed whole at CLOSE"));
        var prefix = new byte[4];
        try
        {
            while (true)
            {
                var read = await stream.ReadAtLeastAsync(prefix, prefix.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
                if (read == 0) return;
                if (read < prefix.Length)
                {
                    End("the stream ended inside a packet length");
                    return;
                }
                var length = BinaryPrimitives.ReadUInt32BigEndian(prefix);
                if (length is < 1 or > MaximumPacketBytes)
                {
                    End(string.Create(CultureInfo.InvariantCulture, $"a packet of {length} bytes; packets are 1 to {MaximumPacketBytes} bytes"));
                    return;
                }
                var body = new byte[length];
                if (await stream.ReadAtLeastAsync(body, body.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false) < body.Length)
                {
                    End("the stream ended inside a packet");
                    return;
                }
                var reply = Answer(body, out var reason);
                if (reply is not null)
                {
                    await stream.WriteAsync(reply, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                if (reason is null) continue;
                End(reason);
                return;
            }
        }
        finally { Discard(); }
    }

    private void End(string reason)
    {
        EndReason = reason;
        log?.Invoke("SFTP session ended: " + reason);
    }

    private void Discard()
    {
        foreach (var file in handles.Values.OfType<FileHandle>().Where(f => f.Writable && f.Dirty))
            log?.Invoke(string.Create(CultureInfo.InvariantCulture,
                $"discarded the unfinished write of {file.Path} ({file.Length} bytes): the session ended before its CLOSE"));
        handles.Clear();
    }

    // One reply per request; a reason ends the session after the reply (if any) is sent.
    private byte[]? Answer(byte[] body, out string? reason)
    {
        reason = null;
        var request = new SftpReader(body);
        var type = request.Byte();
        if (!initialized)
        {
            if (type != SftpPacket.Init) reason = $"the first packet is {SftpPacket.Describe(type)}, not INIT";
            else if (body.Length < 5) reason = "an INIT without a version";
            else if (request.UInt32() is var version and < 3)
                reason = string.Create(CultureInfo.InvariantCulture, $"the client speaks SFTP version {version}; this server speaks version 3 only");
            if (reason is not null) return null;
            initialized = true;
            return new SftpWriter(SftpPacket.Version).UInt32(3).ToPacket();
        }
        if (type == SftpPacket.Init)
        {
            reason = "a second INIT";
            return null;
        }
        if (body.Length < 5)
        {
            reason = $"a {SftpPacket.Describe(type)} packet without a request id";
            return null;
        }
        var id = request.UInt32();
        try { return Dispatch(type, id, request); }
        catch (SftpMalformedException ex)
        {
            reason = string.Create(CultureInfo.InvariantCulture, $"malformed {SftpPacket.Describe(type)} request {id}: {ex.Message}");
            return Status(id, SftpStatus.BadMessage, reason);
        }
    }

    private byte[] Dispatch(byte type, uint id, SftpReader request)
    {
        switch (type)
        {
            case SftpPacket.Realpath: return Realpath(id, request.Text());
            case SftpPacket.Stat or SftpPacket.Lstat: return Stat(id, request.Text());
            case SftpPacket.Fstat: return Fstat(id, request.String());
            case SftpPacket.Opendir: return Opendir(id, request.Text());
            case SftpPacket.Readdir: return Readdir(id, request.String());
            case SftpPacket.Open:
                var path = request.Text();
                var flags = request.UInt32();
                request.Attributes();
                return Open(id, path, flags);
            case SftpPacket.Read:
                var readHandle = request.String();
                var readOffset = request.UInt64();
                return Read(id, readHandle, readOffset, request.UInt32());
            case SftpPacket.Write:
                var writeHandle = request.String();
                var writeOffset = request.UInt64();
                return Write(id, writeHandle, writeOffset, request.String());
            case SftpPacket.Setstat:
                var statPath = request.Text();
                return Setstat(id, statPath, request.Attributes());
            case SftpPacket.Fsetstat:
                var statHandle = request.String();
                return Fsetstat(id, statHandle, request.Attributes());
            case SftpPacket.Close: return Close(id, request.String());
            case SftpPacket.Remove: return Remove(id, request.Text());
            case SftpPacket.Rename:
                var from = request.Text();
                return Rename(id, from, request.Text());
            default: return Unsupported(id, type, request);
        }
    }

    private static byte[] Unsupported(uint id, byte type, SftpReader request)
    {
        var why = type switch
        {
            SftpPacket.Mkdir or SftpPacket.Rmdir => "the tree is fixed: drives and user areas come from the share profile",
            SftpPacket.Readlink or SftpPacket.Symlink => "CP/M has no links",
            SftpPacket.Extended => "no extension is served",
            _ => "it is not an SFTP version 3 request",
        };
        var name = SftpPacket.Describe(type);
        if (type == SftpPacket.Extended)
            try { name = "EXTENDED " + request.Text(); }
            catch (SftpMalformedException) { }
        return Status(id, SftpStatus.OpUnsupported, $"{name} is not served: {why}");
    }

    // ---- the tree ----

    /// <summary>Where a path points: depth 0 the root, 1 a drive, 2 a user area, 3 a name in it (which may not exist).</summary>
    private sealed record Place(string Path, int Depth, SftpShare? Share = null, int User = 0, string? Name = null);

    private static List<string> Parts(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(part);
        }
        return parts;
    }

    // Relative paths start at the root, which is the session's working directory.
    private Place? Locate(string path)
    {
        var parts = Parts(path);
        var normal = "/" + string.Join('/', parts);
        if (parts.Count == 0) return new(normal, 0);
        if (parts.Count > 3 || parts[0].Length != 1 || shares.Find(parts[0][0]) is not { } share) return null;
        if (parts.Count == 1) return new(normal, 1, share);
        var user = share.UserAreas.Where(u => parts[1] == "u" + u.ToString(CultureInfo.InvariantCulture)).Select(u => (int?)u).FirstOrDefault();
        if (user is null) return null;
        return parts.Count == 2 ? new(normal, 2, share, user.Value) : new(normal, 3, share, user.Value, parts[2]);
    }

    private static string FilePath(SftpShare share, int user, string name) =>
        string.Create(CultureInfo.InvariantCulture, $"/{share.Letter}/u{user}/{name}");

    private readonly record struct Attributes(uint Permissions, long Size)
    {
        public bool Directory => (Permissions & 0xf000) == 0x4000;
    }

    private static Attributes DirectoryAttributes(bool writable) => new(0x4000 | (writable ? 0b111_101_101u : 0b101_101_101u), 0);

    private static Attributes FileAttributes(bool readOnly, long size) => new(0x8000 | (readOnly ? 0b100_100_100u : 0b110_100_100u), size);

    private static Attributes FileAttributes(SftpShare share, CpmFile file) =>
        FileAttributes(file.ReadOnly || share.Mode == SftpShareMode.ReadOnly, file.Records * 128L);

    private Attributes? AttributesOf(Place place)
    {
        if (place.Depth < 3) return DirectoryAttributes(place.Depth == 2 && place.Share!.Mode == SftpShareMode.ReadWrite);
        if (Pending(place.Share!, place.User, place.Name!) is { } pending) return FileAttributes(false, pending.Length);
        return place.Share!.Find(place.Name!, place.User) is { } file ? FileAttributes(place.Share, file) : null;
    }

    // The newest write handle on that name, for STAT of a file being written.
    private FileHandle? Pending(SftpShare share, int user, string name) => handles.Values.OfType<FileHandle>()
        .Where(f => f.Writable && f.Share == share && f.User == user && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
        .MaxBy(f => f.Sequence);

    // ---- handles ----

    private abstract class Handle(long sequence)
    {
        public long Sequence { get; } = sequence;
    }

    private sealed class DirectoryHandle(long sequence, Attributes attributes, IReadOnlyList<(string Name, Attributes Attributes)> entries)
        : Handle(sequence)
    {
        public Attributes Attributes { get; } = attributes;
        public IReadOnlyList<(string Name, Attributes Attributes)> Entries { get; } = entries;
        public int Next { get; set; }
    }

    private sealed class FileHandle(long sequence, SftpShare share, int user, string name, byte[] data, bool writable) : Handle(sequence)
    {
        public SftpShare Share { get; } = share;
        public int User { get; } = user;
        public string Name { get; } = name;
        public string Path => FilePath(Share, User, Name);
        public bool Writable { get; } = writable;
        public bool ReadOnlyFile { get; init; }
        public bool Append { get; init; }
        public bool System { get; init; }
        public bool Dirty { get; set; }
        public string? Refused { get; set; }
        public byte[] Data { get; private set; } = data;
        public int Length { get; private set; } = data.Length;

        public void Put(long offset, ReadOnlySpan<byte> bytes)
        {
            var end = checked((int)offset + bytes.Length);
            Room(end);
            bytes.CopyTo(Data.AsSpan((int)offset));
            Length = Math.Max(Length, end);
            Dirty = true;
        }

        /// <summary>A size from SETSTAT/FSETSTAT: truncate, or extend with zeros.</summary>
        public void Resize(int size)
        {
            Room(size);
            if (size < Length) Array.Clear(Data, size, Length - size);
            Length = size;
            Dirty = true;
        }

        // The caller keeps the end within the cap. Bytes past the length are always zero, so a gap reads as zeros.
        private void Room(int end)
        {
            if (end <= Data.Length) return;
            var grown = new byte[Math.Min(MaximumFileBytes, Math.Max(end, Data.Length * 2))];
            Data.AsSpan(0, Length).CopyTo(grown);
            Data = grown;
        }
    }

    private byte[] NewHandle(uint id, Func<long, Handle> create)
    {
        if (handles.Count >= MaximumHandles)
            return Status(id, SftpStatus.Failure, string.Create(CultureInfo.InvariantCulture, $"too many open handles: at most {MaximumHandles}"));
        var handle = create(++nextHandle);
        var name = handle.Sequence.ToString(CultureInfo.InvariantCulture);
        handles.Add(name, handle);
        return new SftpWriter(SftpPacket.Handle, id).String(Encoding.ASCII.GetBytes(name)).ToPacket();
    }

    private T? Find<T>(byte[] handle) where T : Handle =>
        handle.Length <= 20 && handles.TryGetValue(Encoding.ASCII.GetString(handle), out var found) ? found as T : null;

    // ---- requests ----

    private byte[] Realpath(uint id, string path)
    {
        var normal = "/" + string.Join('/', Parts(path));
        var attributes = Locate(normal) is { } place ? AttributesOf(place) : null;
        return Names(id, [(normal, normal, attributes)]);
    }

    private byte[] Stat(uint id, string path)
    {
        var place = Locate(path);
        if ((place is null ? null : AttributesOf(place)) is { } attributes) return Attrs(id, attributes);
        return Status(id, SftpStatus.NoSuchFile, NoSuchFile(path, place));
    }

    private byte[] Fstat(uint id, byte[] handle) => Find<Handle>(handle) switch
    {
        DirectoryHandle directory => Attrs(id, directory.Attributes),
        FileHandle file => Attrs(id, FileAttributes(file.ReadOnlyFile, file.Length)),
        _ => InvalidHandle(id),
    };

    private byte[] Opendir(uint id, string path)
    {
        var place = Locate(path);
        if (place is null) return Status(id, SftpStatus.NoSuchFile, NoSuchFile(path, place));
        if (place.Depth == 3)
            return place.Share!.Find(place.Name!, place.User) is null
                ? Status(id, SftpStatus.NoSuchFile, NoSuchFile(path, place))
                : Status(id, SftpStatus.Failure, place.Path + " is a file, not a directory");
        var entries = place.Depth switch
        {
            0 => shares.Shares.Select(s => (s.Letter.ToString(), DirectoryAttributes(false))).ToArray(),
            1 => place.Share!.UserAreas.Select(u => ("u" + u.ToString(CultureInfo.InvariantCulture), DirectoryAttributes(place.Share.Mode == SftpShareMode.ReadWrite))).ToArray(),
            _ => place.Share!.Files(place.User).Select(f => (f.Name, FileAttributes(place.Share, f))).ToArray(),
        };
        return NewHandle(id, sequence => new DirectoryHandle(sequence, AttributesOf(place)!.Value, entries));
    }

    private byte[] Readdir(uint id, byte[] handle)
    {
        if (Find<Handle>(handle) is not { } found) return InvalidHandle(id);
        if (found is not DirectoryHandle directory) return Status(id, SftpStatus.Failure, "READDIR needs a directory handle");
        if (directory.Next >= directory.Entries.Count) return Status(id, SftpStatus.Eof, "end of directory");
        var batch = directory.Entries.Skip(directory.Next).Take(NamesPerReaddir).ToArray();
        directory.Next += batch.Length;
        return Names(id, batch.Select(e => (e.Name, LongName(e.Name, e.Attributes), (Attributes?)e.Attributes)));
    }

    private byte[] Open(uint id, string path, uint flags)
    {
        var place = Locate(path);
        if (place is null) return Status(id, SftpStatus.NoSuchFile, NoSuchFile(path, place));
        if (place.Depth < 3) return Status(id, SftpStatus.Failure, place.Path + " is a directory");
        var share = place.Share!;
        var write = (flags & (SftpFlags.Write | SftpFlags.Append | SftpFlags.Create | SftpFlags.Truncate)) != 0;
        if (!write)
        {
            if (share.Find(place.Name!, place.User) is not { } file) return Status(id, SftpStatus.NoSuchFile, NoSuchFile(path, place));
            byte[] bytes;
            try { bytes = share.Read(file); }
            catch (InvalidDataException ex) { return Status(id, SftpStatus.Failure, $"{FilePath(share, file.User, file.Name)} cannot be read: {ex.Message}"); }
            return NewHandle(id, sequence => new FileHandle(sequence, share, file.User, file.Name, bytes, writable: false)
                { ReadOnlyFile = file.ReadOnly || share.Mode == SftpShareMode.ReadOnly });
        }
        if (ReadOnlyShare(share) is { } readOnly) return Refuse(id, SftpStatus.PermissionDenied, "open " + place.Path, readOnly);
        if (SftpShare.Canonical(place.Name!, out var badName) is not { } name) return Refuse(id, SftpStatus.BadName, "open " + place.Path, badName!);
        var target = FilePath(share, place.User, name);
        var existing = share.Find(name, place.User);
        if (existing is { ReadOnly: true }) return Refuse(id, SftpStatus.PermissionDenied, "open " + target, target + " has the CP/M R/O attribute");
        if ((flags & SftpFlags.Exclusive) != 0 && (existing is not null || Pending(share, place.User, name) is not null))
            return Refuse(id, SftpStatus.Failure, "open " + target, target + " exists");
        if (existing is null && (flags & SftpFlags.Create) == 0) return Status(id, SftpStatus.NoSuchFile, NoSuchFile(target, place));
        var data = Array.Empty<byte>();
        if (existing is null)
        {
            if (share.WhyNotFits(name, place.User, 0) is { } full) return Refuse(id, SftpStatus.Failure, "open " + target, full);
        }
        else if ((flags & SftpFlags.Truncate) == 0)
        {
            if (existing.Records * 128L > MaximumFileBytes) return Refuse(id, SftpStatus.Failure, "open " + target, OverCap(target));
            try { data = share.Read(existing); }
            catch (InvalidDataException ex) { return Status(id, SftpStatus.Failure, $"{target} cannot be read: {ex.Message}"); }
        }
        return NewHandle(id, sequence => new FileHandle(sequence, share, place.User, name, data, writable: true)
        {
            Append = (flags & SftpFlags.Append) != 0, System = existing?.System ?? false,
            Dirty = existing is null || (flags & SftpFlags.Truncate) != 0,
        });
    }

    private byte[] Read(uint id, byte[] handle, ulong offset, uint length)
    {
        if (Find<Handle>(handle) is not { } found) return InvalidHandle(id);
        if (found is not FileHandle file) return Status(id, SftpStatus.Failure, "READ needs a file handle");
        if (offset >= (ulong)file.Length) return Status(id, SftpStatus.Eof, "end of file");
        var count = (int)Math.Min(Math.Min(length, MaximumReadBytes), (ulong)file.Length - offset);
        return new SftpWriter(SftpPacket.Data, id).String(file.Data.AsSpan((int)offset, count)).ToPacket();
    }

    private byte[] Write(uint id, byte[] handle, ulong offset, byte[] data)
    {
        if (Find<Handle>(handle) is not { } found) return InvalidHandle(id);
        if (found is not FileHandle { Writable: true } file) return Status(id, SftpStatus.PermissionDenied, "the handle is not open for writing");
        // After one refused WRITE the buffer has a hole: later WRITEs and the CLOSE are refused too, without a new log line.
        if (file.Refused is { } refused) return Status(id, SftpStatus.Failure, refused);
        var start = file.Append ? (ulong)file.Length : offset;
        var end = start + (ulong)data.Length;
        var why = start > MaximumFileBytes || end > MaximumFileBytes ? OverCap(file.Path)
            : end > (ulong)file.Length ? file.Share.WhyNotFits(file.Name, file.User, (long)end) : null;
        if (why is not null)
        {
            file.Refused = why;
            return Refuse(id, SftpStatus.Failure, "write " + file.Path, why);
        }
        file.Put((long)start, data);
        return Status(id, SftpStatus.Ok, "");
    }

    // Times, modes and owners are accepted and change nothing (contract §4). A size by path applies only to the buffer of a
    // write handle open on that path; applied or refused, the log says so.
    private byte[] Setstat(uint id, string path, ulong? size)
    {
        var place = Locate(path);
        if (place is null) return Status(id, SftpStatus.NoSuchFile, NoSuchFile(path, place));
        if (size is null)
            return AttributesOf(place) is null ? Status(id, SftpStatus.NoSuchFile, NoSuchFile(path, place)) : Status(id, SftpStatus.Ok, "");
        var what = string.Create(CultureInfo.InvariantCulture, $"setstat {place.Path} size {size}");
        if (ReadOnlyShare(place.Share) is { } readOnly) return Refuse(id, SftpStatus.PermissionDenied, what, readOnly);
        if (place.Depth < 3) return Refuse(id, SftpStatus.PermissionDenied, what, place.Path + " is a directory of the fixed tree");
        if (Pending(place.Share!, place.User, place.Name!) is not { } file)
            return place.Share!.Find(place.Name!, place.User) is null
                ? Refuse(id, SftpStatus.NoSuchFile, what, NoSuchFile(path, place))
                : Refuse(id, SftpStatus.Failure, what, place.Path + " is not open for writing: a size applies only to an open write buffer, committed at its CLOSE");
        if (file.Refused is { } refused) return Status(id, SftpStatus.Failure, refused);
        if (Resize(file, size.Value) is { } why) return Refuse(id, SftpStatus.Failure, what, why);
        log?.Invoke(string.Create(CultureInfo.InvariantCulture, $"sized the open write buffer of {file.Path} to {size} bytes by path; it is committed at its CLOSE"));
        return Status(id, SftpStatus.Ok, "");
    }

    private byte[] Fsetstat(uint id, byte[] handle, ulong? size)
    {
        if (Find<Handle>(handle) is not { } found) return InvalidHandle(id);
        if (size is null) return Status(id, SftpStatus.Ok, "");
        if (found is not FileHandle file) return Status(id, SftpStatus.PermissionDenied, "a size applies only to a file handle open for writing");
        var what = string.Create(CultureInfo.InvariantCulture, $"fsetstat {file.Path} size {size}");
        if (!file.Writable)
            return Refuse(id, SftpStatus.PermissionDenied, what,
                ReadOnlyShare(file.Share) ?? file.Path + " is open for reading: a size applies only to a handle open for writing");
        if (file.Refused is { } refused) return Status(id, SftpStatus.Failure, refused);
        if (Resize(file, size.Value) is { } why) return Refuse(id, SftpStatus.Failure, what, why);
        return Status(id, SftpStatus.Ok, "");
    }

    // A truncate always fits; an extension is checked as a WRITE that grows the file, and a refusal leaves the buffer
    // uncommitted.
    private static string? Resize(FileHandle file, ulong size)
    {
        var why = size > MaximumFileBytes ? OverCap(file.Path)
            : size > (ulong)file.Length ? file.Share.WhyNotFits(file.Name, file.User, (long)size) : null;
        if (why is not null) file.Refused = why;
        else file.Resize((int)size);
        return why;
    }

    private byte[] Close(uint id, byte[] handle)
    {
        if (Find<Handle>(handle) is not { } found) return InvalidHandle(id);
        handles.Remove(Encoding.ASCII.GetString(handle));
        if (found is not FileHandle { Writable: true, Dirty: true } file) return Status(id, SftpStatus.Ok, "");
        if (file.Refused is { } refused) return Refuse(id, SftpStatus.Failure, "close " + file.Path, "not written: " + refused);
        var existed = file.Share.Find(file.Name, file.User) is not null;
        if (file.Share.WhyNotFits(file.Name, file.User, file.Length) is { } full) return Refuse(id, SftpStatus.Failure, "close " + file.Path, full);
        try { file.Share.Write(file.Name, file.User, file.Data.AsSpan(0, file.Length), file.System); }
        catch (InvalidDataException ex) { return Refuse(id, SftpStatus.Failure, "close " + file.Path, ex.Message); }
        log?.Invoke(string.Create(CultureInfo.InvariantCulture, $"{(existed ? "rewrote" : "created")} {file.Path} ({file.Length} bytes)"));
        return Status(id, SftpStatus.Ok, "");
    }

    private byte[] Remove(uint id, string path)
    {
        var place = Locate(path);
        if (place is null) return Status(id, SftpStatus.NoSuchFile, NoSuchFile(path, place));
        if (ReadOnlyShare(place.Share) is { } readOnly) return Refuse(id, SftpStatus.PermissionDenied, "remove " + place.Path, readOnly);
        if (place.Depth < 3) return Status(id, SftpStatus.PermissionDenied, place.Path + " is a directory of the fixed tree");
        if (place.Share!.Find(place.Name!, place.User) is not { } file) return Status(id, SftpStatus.NoSuchFile, NoSuchFile(path, place));
        var target = FilePath(place.Share, file.User, file.Name);
        if (file.ReadOnly) return Refuse(id, SftpStatus.PermissionDenied, "remove " + target, target + " has the CP/M R/O attribute");
        place.Share.Delete(file);
        log?.Invoke("removed " + target);
        return Status(id, SftpStatus.Ok, "");
    }

    private byte[] Rename(uint id, string from, string to)
    {
        var source = Locate(from);
        var destination = Locate(to);
        if (source is null) return Status(id, SftpStatus.NoSuchFile, NoSuchFile(from, source));
        if (destination is null) return Status(id, SftpStatus.NoSuchFile, NoSuchFile(to, destination));
        if ((ReadOnlyShare(source.Share) ?? ReadOnlyShare(destination.Share)) is { } readOnly)
            return Refuse(id, SftpStatus.PermissionDenied, $"rename {source.Path} to {destination.Path}", readOnly);
        if (source.Depth < 3) return Status(id, SftpStatus.PermissionDenied, source.Path + " is a directory of the fixed tree");
        if (destination.Depth < 3) return Status(id, SftpStatus.Failure, destination.Path + " exists");
        if (source.Share!.Find(source.Name!, source.User) is not { } file) return Status(id, SftpStatus.NoSuchFile, NoSuchFile(from, source));
        var share = destination.Share!;
        var was = FilePath(source.Share, file.User, file.Name);
        if (SftpShare.Canonical(destination.Name!, out var badName) is not { } name)
            return Refuse(id, SftpStatus.BadName, $"rename {was} to {destination.Path}", badName!);
        var target = FilePath(share, destination.User, name);
        if (file.ReadOnly) return Refuse(id, SftpStatus.PermissionDenied, $"rename {was}", was + " has the CP/M R/O attribute");
        var same = share == source.Share && destination.User == file.User && string.Equals(name, file.Name, StringComparison.OrdinalIgnoreCase);
        if (same && name == file.Name) return Status(id, SftpStatus.Ok, "");
        if (!same && share.Find(name, destination.User) is not null) return Refuse(id, SftpStatus.Failure, $"rename {was}", target + " exists");
        try
        {
            if (share == source.Share) share.Move(file, name, destination.User);
            else
            {
                var data = source.Share.Read(file);
                if (share.WhyNotFits(name, destination.User, data.Length) is { } full) return Refuse(id, SftpStatus.Failure, $"rename {was}", full);
                share.Write(name, destination.User, data, file.System);
                source.Share.Delete(file);
            }
        }
        catch (InvalidDataException ex) { return Refuse(id, SftpStatus.Failure, $"rename {was}", ex.Message); }
        log?.Invoke($"renamed {was} to {target}");
        return Status(id, SftpStatus.Ok, "");
    }

    // ---- replies ----

    private static string? ReadOnlyShare(SftpShare? share) => share is null || share.Mode == SftpShareMode.ReadWrite ? null
        : share.ReadOnlyReason ?? $"{share.Letter}: is shared read-only";

    private static string OverCap(string path) =>
        string.Create(CultureInfo.InvariantCulture, $"{path} would pass the {MaximumFileBytes / (1024 * 1024)} MiB write buffer cap");

    private static string NoSuchFile(string path, Place? place) =>
        place is { Depth: 3 } && SftpShare.Canonical(place.Name!, out _) is null
            ? $"no such file: {path} (CP/M names are 8.3)"
            : $"no such file: {path}";

    // A refused write path, also in the connection log: sshfs keeps only the status code, so the log keeps the reason.
    private byte[] Refuse(uint id, uint code, string what, string why)
    {
        log?.Invoke($"refused {what}: {why}");
        return Status(id, code, why);
    }

    private static byte[] InvalidHandle(uint id) => Status(id, SftpStatus.Failure, "no such handle");

    private static byte[] Status(uint id, uint code, string message) =>
        new SftpWriter(SftpPacket.Status, id).UInt32(code).String(message).String("en").ToPacket();

    private static byte[] Attrs(uint id, Attributes attributes) => WriteAttributes(new SftpWriter(SftpPacket.Attrs, id), attributes).ToPacket();

    private static byte[] Names(uint id, IEnumerable<(string Name, string LongName, Attributes? Attributes)> names)
    {
        var list = names.ToArray();
        var writer = new SftpWriter(SftpPacket.Name, id).UInt32((uint)list.Length);
        foreach (var (name, longName, attributes) in list)
        {
            writer.String(name).String(longName);
            if (attributes is { } value) WriteAttributes(writer, value);
            else writer.UInt32(0);
        }
        return writer.ToPacket();
    }

    // Size, mode and times; no owner (CP/M has none), and every time 0 (contract §3).
    private static SftpWriter WriteAttributes(SftpWriter writer, Attributes attributes) => writer
        .UInt32(SftpFlags.Size | SftpFlags.Permissions | SftpFlags.Times)
        .UInt64((ulong)attributes.Size).UInt32(attributes.Permissions).UInt32(0).UInt32(0);

    private static string LongName(string name, Attributes attributes)
    {
        var mode = new StringBuilder(attributes.Directory ? "d" : "-");
        for (var bit = 8; bit >= 0; bit--) mode.Append((attributes.Permissions & (1u << bit)) == 0 ? '-' : "rwx"[(8 - bit) % 3]);
        return string.Create(CultureInfo.InvariantCulture, $"{mode}    1 cpm      cpm      {attributes.Size,8} Jan  1  1970 {name}");
    }
}
