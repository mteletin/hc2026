using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Hc2000.Machine.Storage;

namespace Hc2000.HostFiles;

/// <summary>Build-host factory generator; no guest boot files, host admission or machine authority.</summary>
public static class HddFactoryImages
{
    public const string ContainerName = "HC2000-HDD.hdd";
    public const string ReceiptName = "factory-hdd.sha256";

    /// <summary>Writes the factory container and receipt, returning the container path.
    /// The CLI refuses replacement; the repeatable build target explicitly permits it.</summary>
    public static string[] GenerateInto(string outputDirectory, bool overwrite = false)
    {
        Directory.CreateDirectory(outputDirectory);
        var path = Path.GetFullPath(Path.Combine(outputDirectory, ContainerName));
        var receiptPath = Path.GetFullPath(Path.Combine(outputDirectory, ReceiptName));
        if (!overwrite && (File.Exists(path) || File.Exists(receiptPath)))
            throw new IOException($"Refusing to replace existing factory image or receipt in {outputDirectory}; remove it first.");
        var temporary = path + ".hc-factory-" + Guid.NewGuid().ToString("N");
        var temporaryReceipt = temporary + ".sha256";
        // Identity is fixed for the reproducible factory artifact, not minted at build time.
        var id = Convert.FromHexString("484332303030464143544F5259444546");
        var entries = ImmutableArray.CreateBuilder<HddCatalogEntry>();
        var receipt = new StringBuilder();
        try
        {
            using (var container = File.Create(temporary))
            {
                foreach (var slot in new[] { "D", "E", "F" })
                {
                    // HDD DPB: no floppy skew or system tracks; never zero-filled `hdd create`.
                    var bytes = CpmFs.FormatHddVolume();
                    var name = slot + ".DSK";
                    entries.Add(new(name, false, (uint)bytes.Length, new((uint)entries.Count), VolumeCatalogMetadata.Crc32(bytes)));
                    container.Write(bytes);
                    receipt.Append(Hash(bytes)).Append("  ").Append(ContainerName).Append('#').Append(name).Append('\n');
                }
                var metadata = HddCatalogCodec.EncodeHdd(id, 1, entries.ToImmutable(), new(40 * 1024 * 1024, 4, 4096, 8192));
                if (!metadata.Success) throw new InvalidOperationException(metadata.Failure!.Detail);
                container.Write(metadata.Value!.Index.AsSpan());
                container.Write(metadata.Value.Footer.AsSpan());
            }
            using (var container = File.OpenRead(temporary))
                receipt.Append(Convert.ToHexString(SHA256.HashData(container)).ToLowerInvariant()).Append("  ").Append(ContainerName).Append('\n');
            File.WriteAllText(temporaryReceipt, receipt.ToString(), new UTF8Encoding(false));
            File.Move(temporary, path, overwrite);
            File.Move(temporaryReceipt, receiptPath, overwrite);
            return [path];
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (File.Exists(temporaryReceipt)) File.Delete(temporaryReceipt);
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
