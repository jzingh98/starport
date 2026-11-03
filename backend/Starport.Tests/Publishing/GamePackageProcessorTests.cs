using System.IO.Compression;
using System.Security.Cryptography;
using Starport.Processor;

namespace Starport.Tests.Publishing;

public sealed class GamePackageProcessorTests
{
    [Fact]
    public async Task Produces_an_inventory_of_exact_extracted_bytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "starport-processor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var archivePath = Path.Combine(root, "upload.zip");
            var output = Path.Combine(root, "release");
            await using (var archiveStream = File.Create(archivePath))
            using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                Write(archive, "starport-game.json", "{\"schemaVersion\":1,\"engine\":\"godot\",\"entrypoint\":\"index.html\"}");
                Write(archive, "index.html", "<html><script src=\"index.js\"></script></html>");
                Write(archive, "index.js", "const game = true;");
                Write(archive, "index.wasm", "wasm bytes");
                Write(archive, "index.pck", "pack bytes");
            }

            var package = await GamePackageProcessor.ProcessAsync(archivePath, output);
            Assert.Equal(5, package.Files.Count);
            Assert.Equal("index.html", package.EntryPoint);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(archivePath))),
                package.ArchiveSha256);
            Assert.Equal("<html><script src=\"index.js\"></script></html>",
                await File.ReadAllTextAsync(Path.Combine(output, "index.html")));
            Assert.True(File.Exists(Path.Combine(output, "release-inventory.json")));
            Assert.Equal(package.Files.Sum(f => f.Bytes), package.TotalBytes);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Write(ZipArchive archive, string path, string text)
    {
        using var stream = new StreamWriter(archive.CreateEntry(path).Open());
        stream.Write(text);
    }
}
