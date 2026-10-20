using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Hc2000.Machine.Storage;

namespace Hc2000.HostFiles;

// This record is evidence of unresolved publication, never a substitute for the held flock FD.
internal sealed class ImageAdmissionRecord
{
    internal const int Size = 192;
    internal Guid Domain { get; private init; }
    internal Guid Attempt { get; private init; }
    internal Guid Owner { get; private init; }
    internal byte State { get; private set; }
    private byte[] path = new byte[32];
    private byte[] file = new byte[32];

    internal static ImageAdmissionRecord Clean(Guid domain) => new() { Domain = domain };
    internal ImageAdmissionRecord Intent(Guid attempt, string target) => new()
    {
        Domain = Domain, Attempt = attempt, Owner = Guid.NewGuid(), State = 1,
        path = SHA256.HashData(Encoding.UTF8.GetBytes(target))
    };
    internal ImageAdmissionRecord WithFile(NativeImageFileId id)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, id.DeviceMajor);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..], id.DeviceMinor);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], id.Inode);
        file = SHA256.HashData(bytes);
        return this;
    }
    internal void Quarantine() => State = 2;
    internal byte[] Encode()
    {
        var bytes = new byte[Size];
        "HCADMIT1"u8.CopyTo(bytes);
        bytes[8] = 1; bytes[9] = State;
        Domain.TryWriteBytes(bytes.AsSpan(16)); Attempt.TryWriteBytes(bytes.AsSpan(32));
        Owner.TryWriteBytes(bytes.AsSpan(48)); path.CopyTo(bytes, 64); file.CopyTo(bytes, 96);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(188), VolumeCatalogMetadata.Crc32(bytes.AsSpan(0, 188)));
        return bytes;
    }
    internal static ImageAdmissionRecord Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size || !bytes[..8].SequenceEqual("HCADMIT1"u8) || bytes[8] != 1 || bytes[9] > 2 ||
            bytes[10..16].ContainsAnyExcept((byte)0) || bytes[128..188].ContainsAnyExcept((byte)0) ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[188..]) != VolumeCatalogMetadata.Crc32(bytes[..188]))
            throw new InvalidDataException("Invalid native admission record.");
        var record = new ImageAdmissionRecord
        {
            Domain = new(bytes[16..32]), Attempt = new(bytes[32..48]), Owner = new(bytes[48..64]),
            State = bytes[9], path = bytes[64..96].ToArray(), file = bytes[96..128].ToArray()
        };
        if (record.Domain == Guid.Empty || (record.State == 0 ? bytes[32..128].ContainsAnyExcept((byte)0) :
            record.Attempt == Guid.Empty || record.Owner == Guid.Empty || !bytes[64..96].ContainsAnyExcept((byte)0)))
            throw new InvalidDataException("Invalid native admission state.");
        return record;
    }
}
