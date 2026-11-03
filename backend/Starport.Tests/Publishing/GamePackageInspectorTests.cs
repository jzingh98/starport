using System.IO.Compression;
using System.Text;
using Starport.Application.Publishing;

namespace Starport.Tests.Publishing;

public sealed class GamePackageInspectorTests
{
    [Fact]
    public void Accepts_a_minimal_web_export_without_executing_it()
    {
        using var zip = Package(("starport-game.json", "{\"schemaVersion\":1,\"engine\":\"godot\",\"entrypoint\":\"index.html\"}"),
            ("index.html", "<html></html>"), ("game.js", "start"), ("game.wasm", "wasm"), ("game.pck", "pack"));
        var result = GamePackageInspector.Inspect(zip);
        Assert.Equal("index.html", result.EntryPoint);
        Assert.Equal(5, result.Files.Count);
    }

    [Theory]
    [InlineData("../escape.js")]
    [InlineData("/absolute.js")]
    [InlineData("game/../../escape.js")]
    [InlineData("C:/escape.js")]
    [InlineData("game\\escape.js")]
    public void Refuses_paths_that_could_escape_the_release_prefix(string badPath)
    {
        using var zip = Package((badPath, "bad"));
        Assert.Throws<InvalidDataException>(() => GamePackageInspector.Inspect(zip));
    }

    [Fact]
    public void Refuses_case_collisions_that_could_overwrite_a_file()
    {
        using var zip = Package(("Game.js", "first"), ("game.js", "second"));
        Assert.Throws<InvalidDataException>(() => GamePackageInspector.Inspect(zip));
    }

    [Fact]
    public void Refuses_symlinks_even_when_their_names_are_safe()
    {
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            var link = archive.CreateEntry("link.js");
            link.ExternalAttributes = unchecked((int)0xA0000000);
        }
        zip.Position = 0;
        Assert.Throws<InvalidDataException>(() => GamePackageInspector.Inspect(zip));
    }

    [Fact]
    public void Refuses_a_flood_of_directory_entries()
    {
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
            for (var i = 0; i <= GamePackageInspector.MaxEntries; i++)
                archive.CreateEntry($"directory-{i}/");
        zip.Position = 0;
        Assert.Throws<InvalidDataException>(() => GamePackageInspector.Inspect(zip));
    }

    private static MemoryStream Package(params (string Path, string Data)[] entries)
    {
        var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, data) in entries)
            {
                var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8, leaveOpen: false);
                writer.Write(data);
            }
        output.Position = 0;
        return output;
    }
}
