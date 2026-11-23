using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hc2000.Asm;

namespace Hc2000.Examples;

/// <summary>Builds the shipped lending-interrupt demo ROM from its public assembly source.</summary>
public static class LendingFrameAssets
{
    public const string SourcePath = "examples/lending-frame/oneframe.asm";
    public const string IncludedServicePath = "examples/x2-service/x2.service.asm";
    public const string BinaryName = "oneframe.bin";
    public const string ManifestName = "manifest.json";
    public const ushort Origin = 0x0000;
    public const int MaxLength = 0x1000;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static LendingFrameManifest Generate(string repository, string outputDirectory, string configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);

        var sourcePath = Path.Combine(repository, SourcePath);
        var servicePath = Path.Combine(repository, IncludedServicePath);
        if (!File.Exists(sourcePath))
            throw new InvalidOperationException($"Missing lending-frame source '{SourcePath}'.");
        if (!File.Exists(servicePath))
            throw new InvalidOperationException($"Missing X2 service source '{IncludedServicePath}'.");

        var assembled = Assembler.Assemble(sourcePath);
        if (!assembled.Success)
            throw new InvalidOperationException("Failed to assemble examples/lending-frame/oneframe.asm: " +
                string.Join("; ", assembled.Diagnostics));
        var binary = assembled.ToBinary();
        if (binary.Length is 0 or > MaxLength)
            throw new InvalidOperationException(
                $"Assembled {BinaryName} is {binary.Length} bytes; it must be between 1 and {MaxLength} bytes.");

        Directory.CreateDirectory(outputDirectory);
        File.WriteAllBytes(Path.Combine(outputDirectory, BinaryName), binary);

        var manifest = new LendingFrameManifest(
            Schema: "hc2000-lending-frame-assets-v1",
            Configuration: configuration,
            Assembler: ComputeAssemblerProvenance(repository),
            SourcePath: SourcePath,
            SourceSha256: Sha256Hex(File.ReadAllBytes(sourcePath)),
            IncludedServicePath: IncludedServicePath,
            IncludedServiceSha256: Sha256Hex(File.ReadAllBytes(servicePath)),
            Binary: BinaryName,
            Origin: Origin,
            Length: binary.Length,
            MaxLength: MaxLength,
            Sha256: Sha256Hex(binary));

        File.WriteAllText(
            Path.Combine(outputDirectory, ManifestName),
            JsonSerializer.Serialize(manifest, JsonOptions) + "\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return manifest;
    }

    private static string ComputeAssemblerProvenance(string repository)
    {
        using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var relative in Directory.GetFiles(Path.Combine(repository, "src/Hc2000.Asm"), "*.cs")
            .Select(path => Path.GetRelativePath(repository, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal))
        {
            incremental.AppendData(Encoding.UTF8.GetBytes(relative + "\n"));
            incremental.AppendData(File.ReadAllBytes(Path.Combine(repository, relative)));
            incremental.AppendData("\n"u8);
        }
        return "hc2000-asm-source-sha256:" + Convert.ToHexString(incremental.GetHashAndReset()).ToLowerInvariant();
    }

    private static string Sha256Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

public sealed record LendingFrameManifest(
    string Schema,
    string Configuration,
    string Assembler,
    string SourcePath,
    string SourceSha256,
    string IncludedServicePath,
    string IncludedServiceSha256,
    string Binary,
    ushort Origin,
    int Length,
    int MaxLength,
    string Sha256);
