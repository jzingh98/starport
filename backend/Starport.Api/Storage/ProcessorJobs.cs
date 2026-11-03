using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Starport.Api.Storage;

/// <summary>Minimal in-cluster client with a service account limited to processor Jobs.</summary>
public sealed class ProcessorJobs : IDisposable
{
    private const string ServiceAccount = "/var/run/secrets/kubernetes.io/serviceaccount";
    private readonly HttpClient client;
    private readonly string namespaceName;
    private readonly string image;
    private readonly string jobServiceAccount;
    private readonly string instanceName;

    public ProcessorJobs(IConfiguration config)
    {
        namespaceName = config["Processing:Namespace"] ??
            throw new InvalidOperationException("Processing:Namespace is required.");
        image = config["Processing:Image"] ??
            throw new InvalidOperationException("Processing:Image is required.");
        jobServiceAccount = config["Processing:JobServiceAccount"] ?? "starport-processor";
        instanceName = config["Processing:InstanceName"] ??
            throw new InvalidOperationException("Processing:InstanceName is required.");
        var host = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST") ??
            throw new InvalidOperationException("The Kubernetes API host is missing.");
        var port = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_PORT") ?? "443";
        var authority = new UriBuilder("https", host, int.Parse(port)).Uri;
        var ca = X509Certificate2.CreateFromPemFile(Path.Combine(ServiceAccount, "ca.crt"));
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
            {
                if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
                    return false;
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(ca);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(new X509Certificate2(certificate));
            }
        };
        client = new HttpClient(handler) { BaseAddress = authority, Timeout = TimeSpan.FromSeconds(15) };
    }

    private string PathFor(string? jobName = null) =>
        $"apis/batch/v1/namespaces/{Uri.EscapeDataString(namespaceName)}/jobs" +
        (jobName is null ? "" : "/" + Uri.EscapeDataString(jobName));

    public static string Name(Guid uploadId) => "starport-" + uploadId.ToString("N");

    public async Task<ProcessorJobState> GetAsync(Guid uploadId, CancellationToken ct)
    {
        using var request = await RequestAsync(HttpMethod.Get, PathFor(Name(uploadId)), ct);
        using var response = await client.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return ProcessorJobState.Missing;
        response.EnsureSuccessStatusCode();
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct),
            cancellationToken: ct);
        if (!body.RootElement.TryGetProperty("status", out var status)) return ProcessorJobState.Running;
        if (status.TryGetProperty("succeeded", out var succeeded) && succeeded.GetInt32() > 0)
            return ProcessorJobState.Succeeded;
        if (status.TryGetProperty("failed", out var failed) && failed.GetInt32() > 0)
            return ProcessorJobState.Failed;
        return ProcessorJobState.Running;
    }

    public async Task CreateAsync(Guid uploadId, string sourceUrl, string outputUrl,
        CancellationToken ct)
    {
        var name = Name(uploadId);
        var job = new
        {
            apiVersion = "batch/v1", kind = "Job",
            metadata = new { name, @namespace = namespaceName,
                labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/name"] = "starport",
                    ["app.kubernetes.io/instance"] = instanceName,
                    ["app.kubernetes.io/component"] = "processor"
                } },
            spec = new
            {
                backoffLimit = 0, activeDeadlineSeconds = 600, ttlSecondsAfterFinished = 3600,
                template = new
                {
                    metadata = new { labels = new Dictionary<string, string>
                        {
                            ["app.kubernetes.io/name"] = "starport",
                            ["app.kubernetes.io/instance"] = instanceName,
                            ["app.kubernetes.io/component"] = "processor"
                        } },
                    spec = new
                    {
                        restartPolicy = "Never", automountServiceAccountToken = false,
                        enableServiceLinks = false,
                        serviceAccountName = jobServiceAccount,
                        securityContext = new { runAsNonRoot = true, runAsUser = 10001L,
                            runAsGroup = 10001L, seccompProfile = new { type = "RuntimeDefault" } },
                        containers = new[]
                        {
                            new
                            {
                                name = "processor", image,
                                command = new[] { "dotnet", "/app/Starport.Processor.dll" },
                                args = new[] { "--remote", sourceUrl, outputUrl },
                                securityContext = new { allowPrivilegeEscalation = false,
                                    readOnlyRootFilesystem = true, capabilities = new { drop = new[] { "ALL" } } },
                                resources = new { requests = new Dictionary<string, string>
                                    { ["cpu"] = "250m", ["memory"] = "512Mi", ["ephemeral-storage"] = "1Gi" },
                                    limits = new Dictionary<string, string>
                                    { ["cpu"] = "2", ["memory"] = "2Gi", ["ephemeral-storage"] = "3Gi" } },
                                volumeMounts = new[] { new { name = "scratch", mountPath = "/tmp" } }
                            }
                        },
                        volumes = new[] { new { name = "scratch", emptyDir = new { sizeLimit = "2Gi" } } }
                    }
                }
            }
        };
        using var request = await RequestAsync(HttpMethod.Post, PathFor(), ct);
        request.Content = JsonContent.Create(job);
        using var response = await client.SendAsync(request, ct);
        if (response.StatusCode != HttpStatusCode.Conflict) response.EnsureSuccessStatusCode();
    }

    private static async Task<HttpRequestMessage> RequestAsync(HttpMethod method, string path,
        CancellationToken ct)
    {
        var token = (await File.ReadAllTextAsync(Path.Combine(ServiceAccount, "token"), ct)).Trim();
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    public void Dispose() => client.Dispose();
}

public enum ProcessorJobState { Missing, Running, Succeeded, Failed }
