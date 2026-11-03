using System.Security.Cryptography;
using System.Text.Json;

namespace Starport.Application.Publishing;

/// <summary>Trusted orchestration checks every processor output before it becomes a release.</summary>
public static class ProcessedOutputVerifier
{
    public static async Task<ProcessedPackage> VerifyAsync(
        string outputDirectory, string expectedArchiveSha256, CancellationToken ct = default)
    {
        var inventoryPath = Path.Combine(outputDirectory, "release-inventory.json");
        if (!File.Exists(inventoryPath) || new FileInfo(inventoryPath).Length > 1_000_000)
            throw new InvalidDataException("The processor inventory is missing or too large.");
        var package = JsonSerializer.Deserialize<ProcessedPackage>(await File.ReadAllTextAsync(inventoryPath, ct));
        if (package is null || package.EntryPoint != "index.html" ||
            !string.Equals(package.ArchiveSha256, expectedArchiveSha256, StringComparison.Ordinal) ||
            package.Files.Count is < 5 or > GamePackageInspector.MaxFiles ||
            package.TotalBytes is < 0 or > GamePackageInspector.MaxExpandedBytes)
            throw new InvalidDataException("The processor inventory is invalid.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        var root = Path.GetFullPath(outputDirectory) + Path.DirectorySeparatorChar;
        foreach (var file in package.Files)
        {
            if (file.Path.Length > 512 || !PackagePath.IsSafe(file.Path) || !names.Add(file.Path) ||
                file.Bytes < 0 || file.Bytes > GamePackageInspector.MaxExpandedBytes - total ||
                file.Sha256.Length != 64 || !file.Sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
                throw new InvalidDataException("The processor inventory has an invalid file.");
            var path = Path.GetFullPath(Path.Combine(outputDirectory,
                file.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root, StringComparison.Ordinal) || !File.Exists(path) ||
                HasLinkInPath(outputDirectory, file.Path) ||
                new FileInfo(path).Length != file.Bytes)
                throw new InvalidDataException("The processor output does not match its inventory.");
            await using var input = File.OpenRead(path);
            var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct));
            if (!string.Equals(digest, file.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException("The processor output digest changed.");
            total += file.Bytes;
        }
        if (total != package.TotalBytes || !names.Contains("index.html") ||
            !names.Contains("starport-game.json"))
            throw new InvalidDataException("The processor output is incomplete.");

        var actualFiles = EnumerateSafeFiles(outputDirectory)
            .Select(path => Path.GetRelativePath(outputDirectory, path)
                .Replace(Path.DirectorySeparatorChar, '/'))
            .Where(path => path != "release-inventory.json")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actualFiles.SetEquals(names))
            throw new InvalidDataException("The processor output contains unexpected files.");
        return package;
    }

    private static bool HasLinkInPath(string root, string relative)
    {
        var current = Path.GetFullPath(root);
        foreach (var segment in relative.Split('/'))
        {
            current = Path.Combine(current, segment);
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                return true;
        }
        return false;
    }

    private static IEnumerable<string> EnumerateSafeFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException("The processor output contains a link.");
                if (attributes.HasFlag(FileAttributes.Directory)) pending.Push(path);
                else yield return path;
            }
        }
    }
}
