using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Starport.Application.Publishing;

namespace Starport.Processor;

/// <summary>One-shot bounded extraction. Run only in an isolated process with no application credentials.</summary>
public static class GamePackageProcessor
{
    public static async Task<ProcessedPackage> ProcessAsync(
        string archivePath, string outputDirectory, CancellationToken ct = default)
    {
        if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
            throw new IOException("The output directory already exists.");

        await using var input = File.OpenRead(archivePath);
        var inspection = GamePackageInspector.Inspect(input);
        input.Position = 0;
        var archiveHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct));
        input.Position = 0;

        var root = Path.GetFullPath(outputDirectory) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(outputDirectory);
        var results = new List<ProcessedFile>(inspection.Files.Count);
        try
        {
            using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.FullName.EndsWith('/')) continue;
                var destination = Path.GetFullPath(Path.Combine(outputDirectory,
                    entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!destination.StartsWith(root, StringComparison.Ordinal))
                    throw new InvalidDataException("The package contains a path outside its release directory.");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 64 * 1024, FileOptions.Asynchronous);
                await using var source = entry.Open();
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[64 * 1024];
                long written = 0;
                while (true)
                {
                    var count = await source.ReadAsync(buffer, ct);
                    if (count == 0) break;
                    written += count;
                    if (written > entry.Length || written > GamePackageInspector.MaxExpandedBytes)
                        throw new InvalidDataException("An entry exceeded its declared size.");
                    await target.WriteAsync(buffer.AsMemory(0, count), ct);
                    hash.AppendData(buffer, 0, count);
                }
                if (written != entry.Length)
                    throw new InvalidDataException("An entry did not match its declared size.");
                results.Add(new(entry.FullName, written, Convert.ToHexStringLower(hash.GetHashAndReset())));
            }
            var package = new ProcessedPackage(inspection.EntryPoint, archiveHash, results,
                results.Sum(f => f.Bytes));
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "release-inventory.json"),
                JsonSerializer.Serialize(package, new JsonSerializerOptions { WriteIndented = true }), ct);
            return package;
        }
        catch
        {
            Directory.Delete(outputDirectory, recursive: true);
            throw;
        }
    }
}
