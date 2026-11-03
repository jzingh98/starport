using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;
using System.Net.Http.Headers;
using System.Net;
using Google;

namespace Starport.Api.Storage;

/// <summary>Private object storage; raw bucket URLs are never sent to players.</summary>
public sealed class GcsGameStorage(IConfiguration config) : IDisposable
{
    private readonly SemaphoreSlim initialization = new(1, 1);
    private StorageClient? client;
    private UrlSigner? signer;

    public string UploadBucket => Required("Storage:UploadBucket");
    public string ReleaseBucket => Required("Storage:ReleaseBucket");

    public static string SourceKey(Guid uploadId) => $"uploads/{uploadId:N}/source.zip";
    public static string OutputKey(Guid uploadId) => $"uploads/{uploadId:N}/validated.tar";
    public static string ReleaseKey(Guid releaseId, string path) => $"releases/{releaseId:N}/{path}";

    public async Task UploadAsync(string bucket, string key, Stream content,
        string contentType, CancellationToken ct)
    {
        var (storage, _) = await ClientsAsync(ct);
        await storage.UploadObjectAsync(bucket, key, contentType, content,
            cancellationToken: ct);
    }

    public async Task DownloadAsync(string bucket, string key, Stream destination,
        CancellationToken ct, long? start = null, long? end = null)
    {
        var (storage, _) = await ClientsAsync(ct);
        var options = start is null ? null : new DownloadObjectOptions
        {
            Range = new RangeHeaderValue(start, end)
        };
        await storage.DownloadObjectAsync(bucket, key, destination, options,
            cancellationToken: ct);
    }

    public async Task<string> SignReadAsync(string bucket, string key, TimeSpan ttl,
        CancellationToken ct)
    {
        var (_, urlSigner) = await ClientsAsync(ct);
        return await urlSigner.SignAsync(bucket, key, ttl, HttpMethod.Get, cancellationToken: ct);
    }

    public async Task DeleteAsync(string bucket, string key, CancellationToken ct)
    {
        var (storage, _) = await ClientsAsync(ct);
        try { await storage.DeleteObjectAsync(bucket, key, cancellationToken: ct); }
        catch (GoogleApiException error) when (error.HttpStatusCode == HttpStatusCode.NotFound)
        { /* Deleting a missing object is idempotent. */ }
    }

    public async Task<string> SignWriteAsync(string bucket, string key, TimeSpan ttl,
        CancellationToken ct)
    {
        var (_, urlSigner) = await ClientsAsync(ct);
        return await urlSigner.SignAsync(bucket, key, ttl, HttpMethod.Put, cancellationToken: ct);
    }

    private string Required(string name) => config[name] is { Length: > 0 } value
        ? value : throw new InvalidOperationException($"{name} is required for GCS storage.");

    private async Task<(StorageClient, UrlSigner)> ClientsAsync(CancellationToken ct)
    {
        if (client is { } ready && signer is { } signed) return (ready, signed);
        await initialization.WaitAsync(ct);
        try
        {
            if (client is { } existing && signer is { } current) return (existing, current);
            var credential = await GoogleCredential.GetApplicationDefaultAsync(ct);
            signer = UrlSigner.FromCredential(credential);
            client = await StorageClient.CreateAsync(credential);
            return (client, signer);
        }
        finally { initialization.Release(); }
    }

    public void Dispose()
    {
        client?.Dispose();
        initialization.Dispose();
    }
}
