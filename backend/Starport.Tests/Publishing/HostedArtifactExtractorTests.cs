using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Starport.Api.Storage;
using Starport.Application.Publishing;

namespace Starport.Tests.Publishing;

public sealed class HostedArtifactExtractorTests
{
    [Fact]
    public async Task Real_processor_style_tar_extracts_and_verifies_exact_inventory()
    {
        var root = TempRoot();
        try
        {
            var source = Path.Combine(root, "source");
            var target = Path.Combine(root, "target");
            var tarPath = Path.Combine(root, "validated.tar");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(target);
            var entries = new Dictionary<string, string>
            {
                ["starport-game.json"] = "{\"schemaVersion\":1,\"engine\":\"godot\",\"entrypoint\":\"index.html\"}",
                ["index.html"] = "<html>game</html>",
                ["index.js"] = "game();",
                ["index.wasm"] = "wasm",
                ["index.pck"] = "pck"
            };
            var files = new List<ProcessedFile>();
            foreach (var (name, content) in entries)
            {
                var bytes = Encoding.UTF8.GetBytes(content);
                await File.WriteAllBytesAsync(Path.Combine(source, name), bytes);
                files.Add(new ProcessedFile(name, bytes.Length,
                    Convert.ToHexStringLower(SHA256.HashData(bytes))));
            }
            var archiveSha = new string('a', 64);
            var inventory = new ProcessedPackage("index.html", archiveSha, files,
                files.Sum(f => f.Bytes));
            await File.WriteAllTextAsync(Path.Combine(source, "release-inventory.json"),
                JsonSerializer.Serialize(inventory));
            await using (var output = File.Create(tarPath))
            await using (var writer = new TarWriter(output, leaveOpen: true))
            {
                await writer.WriteEntryAsync(Path.Combine(source, "release-inventory.json"),
                    "release-inventory.json");
                foreach (var file in files)
                    await writer.WriteEntryAsync(Path.Combine(source, file.Path), file.Path);
            }

            await HostedArtifactExtractor.ExtractAsync(tarPath, target);
            var verified = await ProcessedOutputVerifier.VerifyAsync(target, archiveSha);
            Assert.Equal(files.Count, verified.Files.Count);
            Assert.Equal("<html>game</html>", await File.ReadAllTextAsync(Path.Combine(target, "index.html")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Rejects_symlink_and_duplicate_entries()
    {
        var root = TempRoot();
        try
        {
            var symlinkTar = Path.Combine(root, "symlink.tar");
            await using (var output = File.Create(symlinkTar))
            await using (var writer = new TarWriter(output, leaveOpen: true))
            {
                var link = new PaxTarEntry(TarEntryType.SymbolicLink, "index.html")
                { LinkName = "/etc/passwd" };
                await writer.WriteEntryAsync(link);
            }
            var target = Path.Combine(root, "target-a");
            Directory.CreateDirectory(target);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                HostedArtifactExtractor.ExtractAsync(symlinkTar, target));
            Assert.Empty(Directory.EnumerateFileSystemEntries(target));

            var duplicateTar = Path.Combine(root, "duplicate.tar");
            var source = Path.Combine(root, "source.txt");
            await File.WriteAllTextAsync(source, "same file");
            await using (var output = File.Create(duplicateTar))
            await using (var writer = new TarWriter(output, leaveOpen: true))
            {
                await writer.WriteEntryAsync(source, "index.html");
                await writer.WriteEntryAsync(source, "index.html");
            }
            target = Path.Combine(root, "target-b");
            Directory.CreateDirectory(target);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                HostedArtifactExtractor.ExtractAsync(duplicateTar, target));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string TempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "starport-tar-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
