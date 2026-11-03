using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Starport.Application.Access;
using Starport.Domain.Access;

namespace Starport.Infrastructure.Accounts;

/// <summary>Authoritative hosted workspace checks. Every call reaches Accounts; errors fail closed.</summary>
public sealed class AccountsWorkspaceAccess(
    IHttpClientFactory clients,
    AccountsServiceTokenProvider tokens,
    IOptions<AccountsAccessOptions> options)
    : IWorkspaceEntitlements, IWorkspaceMemberships
{
    public async Task<IReadOnlyList<AccountsMemberWorkspace>> ListAsync(string subject, CancellationToken ct = default)
    {
        var states = await PostAsync<WorkspaceDto[]>("v1/entitlements/workspaces",
            new { product = "starport", accountSub = subject }, ct)
            ?? throw new AccountsAccessUnavailableException("Accounts returned no workspace list.");
        var result = new List<AccountsMemberWorkspace>(states.Length);
        foreach (var state in states)
        {
            if (!Enum.TryParse<WorkspaceRole>(state.Role, true, out var role) ||
                string.IsNullOrWhiteSpace(state.WorkspaceId) || string.IsNullOrWhiteSpace(state.Slug) ||
                string.IsNullOrWhiteSpace(state.Name))
                throw new AccountsAccessUnavailableException("Accounts returned an invalid workspace.");
            result.Add(new(state.WorkspaceId, state.Slug, state.Name, state.Kind, role,
                state.CanPublish, state.PaidThrough));
        }
        return result;
    }

    public async Task<PublishingEntitlement?> GetAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        var results = await GetManyAsync([workspaceId], cancellationToken);
        return results.GetValueOrDefault(workspaceId);
    }

    public async Task<IReadOnlyDictionary<string, PublishingEntitlement?>> GetManyAsync(
        IReadOnlyCollection<string> workspaceIds, CancellationToken cancellationToken = default)
    {
        var ids = workspaceIds.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return new Dictionary<string, PublishingEntitlement?>();
        if (ids.Length > 100) throw new ArgumentOutOfRangeException(nameof(workspaceIds));
        var response = await PostAsync<AvailabilityDto[]>("v1/entitlements/availability",
            new { product = "starport", workspaceIds = ids }, cancellationToken)
            ?? throw new AccountsAccessUnavailableException("Accounts returned no workspace availability.");
        var states = response.ToDictionary(x => x.WorkspaceId, StringComparer.Ordinal);
        var result = new Dictionary<string, PublishingEntitlement?>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (!states.TryGetValue(id, out var state))
                throw new AccountsAccessUnavailableException("Accounts omitted a workspace availability result.");
        // validUntil may be earlier than paidThrough when a retained bundle item ends.
            DateTimeOffset? until = state.PaidThrough is { } paid && state.ValidUntil is { } valid
                ? (paid < valid ? paid : valid)
                : null;
            result[id] = new PublishingEntitlement(id, state.CanServe, until, Limits: state.Limits);
        }
        return result;
    }

    public async Task<WorkspaceMembership?> GetAsync(
        string workspaceId, string subject, CancellationToken cancellationToken = default)
    {
        var result = await PostAsync<ResolveDto>("v1/entitlements/resolve",
            new { product = "starport", workspaceId, accountSub = subject, capability = "publish_games" },
            cancellationToken, allowNotFound: true);
        if (result is null)
            return null;
        if (result.WorkspaceId != workspaceId ||
            !Enum.TryParse<WorkspaceRole>(result.Role, true, out var role))
            throw new AccountsAccessUnavailableException("Accounts returned an invalid membership.");
        return new WorkspaceMembership(workspaceId, subject, role);
    }

    private async Task<T?> PostAsync<T>(string path, object body, CancellationToken ct, bool allowNotFound = false)
    {
        var token = await tokens.GetAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(options.Value.BaseUrl), path))
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var response = await clients.CreateClient("accounts-api").SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound && allowNotFound)
                return default;
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                tokens.Invalidate(token);
            if (!response.IsSuccessStatusCode)
                throw new AccountsAccessUnavailableException($"Accounts entitlement lookup failed ({(int)response.StatusCode}).");
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct)
                ?? throw new AccountsAccessUnavailableException("Accounts entitlement reply was empty.");
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            throw new AccountsAccessUnavailableException("Accounts entitlement lookup is unavailable.", error);
        }
    }

    private sealed record AvailabilityDto(string WorkspaceId, bool CanServe,
        Dictionary<string, int?> Limits, DateTimeOffset? PaidThrough, DateTimeOffset? ValidUntil);
    private sealed record ResolveDto(string WorkspaceId, string Role);
    private sealed record WorkspaceDto(string WorkspaceId, string Slug, string Name, string Kind,
        string Role, bool CanPublish, DateTimeOffset? PaidThrough);
}

public sealed record AccountsMemberWorkspace(string Id, string Slug, string Name, string Kind,
    WorkspaceRole Role, bool CanPublish, DateTimeOffset? PaidThrough);
