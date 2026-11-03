using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Starport.Infrastructure.Accounts;
using Starport.Infrastructure.Database;

namespace Starport.Api.Storage;

/// <summary>
/// Applies Accounts' durable serving-loss feed to publication state. The cursor and game updates
/// commit together; a restart or a second replica can safely resume from the same cursor.
/// </summary>
public sealed class AccountsServingLossConsumer(
    IServiceScopeFactory scopes,
    IHttpClientFactory clients,
    AccountsServiceTokenProvider tokens,
    IOptions<AccountsAccessOptions> options,
    TimeProvider clock,
    ILogger<AccountsServingLossConsumer> logger) : BackgroundService
{
    private const string CursorId = "starport-serving-loss";
    private const int PageSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await ConsumeOnceAsync(stoppingToken) == PageSize)
                    continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogError(error, "Accounts serving-loss consumption failed");
            }
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }

    /// <returns>The number of events applied in this transaction.</returns>
    public async Task<int> ConsumeOnceAsync(CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StarportDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // The seeded row serializes consumers across replicas. It also prevents cursor advancement
        // while another replica is still applying an earlier page.
        var cursor = await db.AccountsEventCursors
            .FromSqlRaw("SELECT * FROM \"AccountsEventCursors\" WHERE \"Id\" = 'starport-serving-loss' FOR UPDATE")
            .SingleAsync(ct);
        var page = await ReadPageAsync(cursor.LastEventId, ct);
        if (page.Events is null || page.NextCursor < cursor.LastEventId ||
            page.Events.Count > PageSize ||
            (page.Events.Count == 0 && page.NextCursor != cursor.LastEventId) ||
            (page.Events.Count > 0 && page.NextCursor != page.Events[^1].Id))
            throw new AccountsAccessUnavailableException("Accounts returned an invalid serving-loss page.");

        var prior = cursor.LastEventId;
        foreach (var item in page.Events)
        {
            if (item.Id <= prior || item.WorkspaceId is not { Length: > 0 } ||
                item.Product != "starport" || item.Kind != "serving_lost")
                throw new AccountsAccessUnavailableException("Accounts returned an invalid serving-loss event.");
            prior = item.Id;
            var updated = await db.Games.Where(g => g.WorkspaceId == item.WorkspaceId && g.IsPublished)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(g => g.IsPublished, false)
                    .SetProperty(g => g.UpdatedAt, clock.GetUtcNow()), ct);
            if (updated > 0)
                logger.LogInformation("Unpublished {Count} games for workspace {WorkspaceId} after Accounts event {EventId}",
                    updated, item.WorkspaceId, item.Id);
        }
        if (page.Events.Count > 0)
        {
            cursor.LastEventId = page.NextCursor;
            cursor.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return page.Events.Count;
    }

    private async Task<ServingLossPage> ReadPageAsync(long after, CancellationToken ct)
    {
        var token = await tokens.GetAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri(new Uri(options.Value.BaseUrl), $"v1/entitlements/events?after={after}&limit={PageSize}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var response = await clients.CreateClient("accounts-api").SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                tokens.Invalidate(token);
            if (!response.IsSuccessStatusCode)
                throw new AccountsAccessUnavailableException($"Accounts serving-loss feed failed ({(int)response.StatusCode}).");
            return await response.Content.ReadFromJsonAsync<ServingLossPage>(cancellationToken: ct)
                ?? throw new AccountsAccessUnavailableException("Accounts returned no serving-loss page.");
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            throw new AccountsAccessUnavailableException("Accounts serving-loss feed is unavailable.", error);
        }
    }

    private sealed record ServingLossPage(IReadOnlyList<ServingLossEvent>? Events, long NextCursor);
    private sealed record ServingLossEvent(long Id, string? WorkspaceId, string? Product,
        string? Kind, DateTimeOffset EffectiveAt);
}
