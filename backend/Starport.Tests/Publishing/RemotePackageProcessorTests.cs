using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using Starport.Processor;

namespace Starport.Tests.Publishing;

public sealed class RemotePackageProcessorTests
{
    [Fact]
    public async Task Signed_url_mode_uploads_a_flat_validated_tar()
    {
        byte[] archive;
        await using (var stream = new MemoryStream())
        {
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                Write(zip, "starport-game.json", "{\"schemaVersion\":1,\"engine\":\"godot\",\"entrypoint\":\"index.html\"}");
                Write(zip, "index.html", "<html>game</html>");
                Write(zip, "index.js", "game();");
                Write(zip, "index.wasm", "wasm");
                Write(zip, "index.pck", "pck");
            }
            archive = stream.ToArray();
        }
        byte[]? uploaded = null;
        using var handler = new Handler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) };
            if (request.Method == HttpMethod.Put)
            {
                uploaded = await request.Content!.ReadAsByteArrayAsync(ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        });
        const string signed = "https://storage.googleapis.com/starport-test/object?X-Goog-Signature=abc";
        await RemotePackageProcessor.ProcessAsync(signed, signed, CancellationToken.None, handler);

        Assert.NotNull(uploaded);
        using var tar = new TarReader(new MemoryStream(uploaded));
        var names = new List<string>();
        TarEntry? entry;
        while ((entry = tar.GetNextEntry()) is not null) names.Add(entry.Name);
        Assert.Equal(new[] { "release-inventory.json", "starport-game.json", "index.html",
            "index.js", "index.wasm", "index.pck" }, names);
    }

    [Fact]
    public async Task Rejects_a_non_gcs_capability_before_network_access()
    {
        using var handler = new Handler((_, _) =>
            Task.FromException<HttpResponseMessage>(new Exception("Network must not be reached.")));
        await Assert.ThrowsAsync<ArgumentException>(() => RemotePackageProcessor.ProcessAsync(
            "https://evil.example/object?X-Goog-Signature=abc",
            "https://storage.googleapis.com/bucket/object?X-Goog-Signature=abc",
            CancellationToken.None, handler));
    }

    private static void Write(ZipArchive archive, string path, string contents)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open());
        writer.Write(contents);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => send(request, ct);
    }
}
