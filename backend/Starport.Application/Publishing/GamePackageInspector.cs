using System.IO.Compression;
using System.Text.Json;

namespace Starport.Application.Publishing;

public sealed record PackageFile(string Path, long Bytes);
public sealed record PackageInspection(string EntryPoint, IReadOnlyList<PackageFile> Files, long TotalBytes);

/// <summary>
/// Inspects an untrusted Godot web export without extracting or executing it. A separate isolated
/// processor will perform the later copy into the immutable release store.
/// </summary>
public static class GamePackageInspector
{
    public const long MaxArchiveBytes = 256L * 1024 * 1024;
    public const long MaxExpandedBytes = 512L * 1024 * 1024;
    public const int MaxFiles = 2_000;
    public const int MaxEntries = 4_000;
    public const int MaxManifestBytes = 32 * 1024;

    public static PackageInspection Inspect(Stream input)
    {
        if (!input.CanRead || !input.CanSeek || input.Length > MaxArchiveBytes)
            throw new InvalidDataException("The package must be a seekable ZIP no larger than 256 MiB.");

        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<PackageFile>();
        long expanded = 0;
        var entries = 0;
        ZipArchiveEntry? manifest = null;

        foreach (var entry in archive.Entries)
        {
            if (++entries > MaxEntries)
                throw new InvalidDataException("The package has too many entries.");
            ValidatePath(entry.FullName);
            if (!names.Add(entry.FullName))
                throw new InvalidDataException("The package contains duplicate or case-colliding paths.");

            // The upper 16 bits encode Unix file type when the archive was made on Unix.
            var type = (entry.ExternalAttributes >> 16) & 0xF000;
            if (type is not (0 or 0x8000 or 0x4000))
                throw new InvalidDataException("Links and special files are not allowed in game packages.");
            if (entry.FullName.EndsWith('/'))
                continue;
            if (entry.Length < 0 || entry.Length > MaxExpandedBytes - expanded)
                throw new InvalidDataException("The expanded game is too large.");
            expanded += entry.Length;
            if (entry.Length > 0 && (entry.CompressedLength == 0 ||
                entry.Length / Math.Max(1, entry.CompressedLength) > 200))
                throw new InvalidDataException("An entry expands too much.");
            files.Add(new PackageFile(entry.FullName, entry.Length));
            if (files.Count > MaxFiles)
                throw new InvalidDataException("The package has too many files.");
            if (entry.FullName == "starport-game.json")
                manifest = entry;
        }

        if (manifest is null || manifest.Length > MaxManifestBytes)
            throw new InvalidDataException("A small starport-game.json manifest is required at the package root.");
        try
        {
            using var manifestStream = manifest.Open();
            using var document = JsonDocument.Parse(manifestStream, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number ||
                !schema.TryGetInt32(out var schemaVersion) || schemaVersion != 1 ||
                !root.TryGetProperty("engine", out var engine) || engine.ValueKind != JsonValueKind.String ||
                engine.GetString() != "godot" ||
                !root.TryGetProperty("entrypoint", out var point) || point.ValueKind != JsonValueKind.String ||
                point.GetString() != "index.html")
                throw new InvalidDataException("The v1 manifest must declare a Godot index.html export.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("The game manifest is not valid JSON.", error);
        }
        if (!files.Any(f => f.Path == "index.html") || !files.Any(f => f.Path.EndsWith(".wasm", StringComparison.Ordinal)) ||
            !files.Any(f => f.Path.EndsWith(".pck", StringComparison.Ordinal)) ||
            !files.Any(f => f.Path.EndsWith(".js", StringComparison.Ordinal)))
            throw new InvalidDataException("The Godot web export is incomplete.");
        return new PackageInspection("index.html", files, expanded);
    }

    private static void ValidatePath(string path)
    {
        if (!PackagePath.IsSafe(path, directory: path.EndsWith('/')))
            throw new InvalidDataException("The package contains an invalid path.");
    }
}
