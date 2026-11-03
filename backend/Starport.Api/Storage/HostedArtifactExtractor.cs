using System.Formats.Tar;
using Starport.Application.Publishing;

namespace Starport.Api.Storage;

/// <summary>Extracts the isolated Job's flat TAR into a fresh private directory.</summary>
public static class HostedArtifactExtractor
{
    public static async Task ExtractAsync(string tarPath, string target, CancellationToken ct = default)
    {
        await using var source = File.OpenRead(tarPath);
        using var tar = new TarReader(source);
        var count = 0;
        long total = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await tar.GetNextEntryAsync(cancellationToken: ct) is { } entry)
        {
            if (++count > GamePackageInspector.MaxFiles + 1 ||
                entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) ||
                !PackagePath.IsSafe(entry.Name) || entry.Name.Length > 512 ||
                !names.Add(entry.Name) || entry.DataStream is null || entry.Length < 0 ||
                entry.Length > GamePackageInspector.MaxExpandedBytes + 1_000_000 - total)
                throw new InvalidDataException("The processor output TAR is invalid.");
            total += entry.Length;
            var path = Path.Combine(target, entry.Name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.Asynchronous);
            await entry.DataStream.CopyToAsync(output, ct);
            if (output.Length != entry.Length)
                throw new InvalidDataException("The processor output TAR has a truncated file.");
        }
    }
}
