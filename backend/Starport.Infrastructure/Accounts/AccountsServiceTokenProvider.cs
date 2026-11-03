using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Starport.Infrastructure.Accounts;

/// <summary>Only the service token is cached. Entitlement and membership answers never are.</summary>
public sealed class AccountsServiceTokenProvider(
    IHttpClientFactory clients, IOptions<AccountsAccessOptions> options, TimeProvider clock)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (string Token, DateTimeOffset RefreshAt)? _cached;

    public async Task<string> GetAsync(CancellationToken ct)
    {
        if (_cached is { } cached && clock.GetUtcNow() < cached.RefreshAt)
            return cached.Token;
        await _gate.WaitAsync(ct);
        try
        {
            if (_cached is { } fresh && clock.GetUtcNow() < fresh.RefreshAt)
                return fresh.Token;
            using var request = new HttpRequestMessage(HttpMethod.Post, options.Value.TokenUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = options.Value.ClientId,
                    ["client_secret"] = options.Value.ClientSecret
                })
            };
            try
            {
                using var response = await clients.CreateClient("accounts-token").SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                    throw new AccountsAccessUnavailableException("The Accounts token endpoint rejected Starport.");
                var body = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct);
                if (string.IsNullOrWhiteSpace(body?.AccessToken))
                    throw new AccountsAccessUnavailableException("The Accounts token response was incomplete.");
                var lifetime = TimeSpan.FromSeconds(body.ExpiresIn is > 0 ? body.ExpiresIn.Value : 60);
                _cached = (body.AccessToken, clock.GetUtcNow() +
                    (lifetime > TimeSpan.FromMinutes(1) ? lifetime - TimeSpan.FromSeconds(30) : lifetime / 2));
                return body.AccessToken;
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                throw new AccountsAccessUnavailableException("The Accounts token endpoint is unavailable.", error);
            }
        }
        finally { _gate.Release(); }
    }

    public void Invalidate(string token)
    {
        if (_cached?.Token == token)
            _cached = null;
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_in")] int? ExpiresIn);
}
