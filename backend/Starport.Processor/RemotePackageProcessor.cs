using System.Formats.Tar;
using System.Net;
using Starport.Application.Publishing;

namespace Starport.Processor;

/// <summary>
/// Processes one archive with one-object GCS capabilities. This runs in the isolated Job:
/// no workload identity, Kubernetes token, service credential, or database access.
/// </summary>
public static class RemotePackageProcessor
{
    private const long MaxTarBytes = GamePackageInspector.MaxExpandedBytes + 4L * 1024 * 1024;

    public static async Task ProcessAsync(string sourceUrl, string outputUrl, CancellationToken ct,
        HttpMessageHandler? transport = null)
    {
        var source = RequireGcsSignedUrl(sourceUrl);
        var output = RequireGcsSignedUrl(outputUrl);
        using var client = new HttpClient(transport ?? new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false
        }) { Timeout = Timeout.InfiniteTimeSpan };
        var scratch = Path.Combine(Path.GetTempPath(), "starport-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var archive = Path.Combine(scratch, "source.zip");
            var release = Path.Combine(scratch, "release");
            var tar = Path.Combine(scratch, "validated.tar");
            using (var get = new HttpRequestMessage(HttpMethod.Get, source))
            using (var response = await client.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (response.StatusCode != HttpStatusCode.OK ||
                    response.Content.Headers.ContentLength > GamePackageInspector.MaxArchiveBytes)
                    throw new InvalidDataException("The source object is unavailable or too large.");
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                await using var target = new FileStream(archive, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 64 * 1024, FileOptions.Asynchronous);
                var buffer = new byte[64 * 1024];
                long total = 0;
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, ct);
                    if (read == 0) break;
                    total += read;
                    if (total > GamePackageInspector.MaxArchiveBytes)
                        throw new InvalidDataException("The source object is too large.");
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }

            var processed = await GamePackageProcessor.ProcessAsync(archive, release, ct);
            await using (var file = new FileStream(tar, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                await using var writer = new TarWriter(file, leaveOpen: true);
                await writer.WriteEntryAsync(Path.Combine(release, "release-inventory.json"),
                    "release-inventory.json", ct);
                foreach (var entry in processed.Files)
                    await writer.WriteEntryAsync(Path.Combine(release, entry.Path), entry.Path, ct);
            }
            if (new FileInfo(tar).Length > MaxTarBytes)
                throw new InvalidDataException("The validated artifact is too large.");

            await using var artifact = new FileStream(tar, FileMode.Open, FileAccess.Read,
                FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
            using var content = new StreamContent(artifact);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-tar");
            using var put = new HttpRequestMessage(HttpMethod.Put, output) { Content = content };
            using var result = await client.SendAsync(put, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!result.IsSuccessStatusCode)
                throw new IOException("The validated artifact could not be stored.");
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static Uri RequireGcsSignedUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Host != "storage.googleapis.com" ||
            uri.Port != 443 || uri.UserInfo.Length != 0 ||
            !uri.Query.Contains("X-Goog-Signature=", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A signed Google Cloud Storage URL is required.");
        return uri;
    }
}
