using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Starport.Api.Auth;
using Starport.Application.Access;
using Starport.Domain.Access;
using Starport.Infrastructure.Accounts;
using Starport.Infrastructure.Database;

namespace Starport.Api.Endpoints;

public sealed record ShareWithPersonBody(string? Email, string? Subject, DateTimeOffset? ExpiresAt);
public sealed record PersonShareDto(string Subject, string? Email, DateTimeOffset? ExpiresAt);

/// <summary>Publisher-managed grants for the People audience. Grants are stored against stable subjects.</summary>
public static class PeopleSharingEndpoints
{
    private const int MaxActiveShares = 100;
    public static void MapPeopleSharingEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/workspaces/{workspaceId}/games/{gameId:guid}/people")
            .RequireAuthorization();
        group.MapGet("", ListAsync);
        group.MapPut("", UpsertAsync);
        group.MapDelete("/{personSubject}", RevokeAsync);
    }

    public static async Task<IResult> ListAsync(string workspaceId, Guid gameId, HttpContext http,
        StarportDbContext db, IWorkspaceMemberships memberships, CancellationToken ct)
    {
        var error = await EditorErrorAsync(workspaceId, gameId, http, db, memberships, null, null, ct);
        if (error is not null) return error;
        var grants = await db.GamePlayGrants.AsNoTracking().Where(g => g.GameId == gameId)
            .OrderBy(g => g.Email).ThenBy(g => g.Subject)
            .Select(g => new PersonShareDto(g.Subject, g.Email, g.ExpiresAt))
            .ToListAsync(ct);
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(grants);
    }

    public static async Task<IResult> UpsertAsync(string workspaceId, Guid gameId, ShareWithPersonBody? body,
        HttpContext http, StarportDbContext db, IWorkspaceMemberships memberships,
        IWorkspaceEntitlements entitlements, IConfiguration config, IServiceProvider services,
        TimeProvider clock, CancellationToken ct)
    {
        var error = await EditorErrorAsync(workspaceId, gameId, http, db, memberships, entitlements, clock, ct);
        if (error is not null) return error;
        if (body is null || body.ExpiresAt <= clock.GetUtcNow())
            return ApiProblems.Problem(400, "invalid-person-share", "Invalid person share");
        var hasEmail = !string.IsNullOrWhiteSpace(body.Email);
        var hasSubject = !string.IsNullOrWhiteSpace(body.Subject);
        if (hasEmail == hasSubject ||
            (hasEmail && (body.Email!.Trim().Length > 254 ||
                          !new EmailAddressAttribute().IsValid(body.Email.Trim()))) ||
            (hasSubject && (config["Workspaces:Source"] != "local" ||
                            !PeopleIdentityResolver.ValidSubject(body.Subject))))
            return ApiProblems.Problem(400, "invalid-person-share", "Invalid person share");
        var subject = await PeopleIdentityResolver.ResolveAsync(body, config, services, ct);
        if (subject is null)
            return ApiProblems.Problem(404, "player-not-found", "Player not found");
        if (subject.Length > 256)
            return ApiProblems.Problem(400, "invalid-person-share", "Invalid person share");

        // Serialize quota checks for editors of the same game on hosted PostgreSQL.
        await using var transaction = db.Database.IsNpgsql()
            ? await db.Database.BeginTransactionAsync(ct) : null;
        if (transaction is not null)
            await db.Games.FromSqlInterpolated($"SELECT * FROM \"Games\" WHERE \"Id\" = {gameId} FOR UPDATE")
                .AsNoTracking().SingleAsync(ct);
        var existing = await db.GamePlayGrants.SingleOrDefaultAsync(g =>
            g.GameId == gameId && g.Subject == subject, ct);
        var now = clock.GetUtcNow();
        if (existing is null || existing.ExpiresAt is not null && existing.ExpiresAt <= now)
        {
            var active = await db.GamePlayGrants.CountAsync(g => g.GameId == gameId &&
                (g.ExpiresAt == null || g.ExpiresAt > now), ct);
            if (active >= MaxActiveShares)
                return ApiProblems.Problem(409, "share-limit-reached", "Active share limit reached");
        }
        var email = body.Email?.Trim().ToLowerInvariant();
        if (existing is null)
            db.GamePlayGrants.Add(new GamePlayGrantRecord
            {
                GameId = gameId, Subject = subject, Email = email, ExpiresAt = body.ExpiresAt
            });
        else
        {
            if (email is not null) existing.Email = email;
            existing.ExpiresAt = body.ExpiresAt;
        }
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException dbError) when (dbError.InnerException is PostgresException { SqlState: "23505" })
        {
            return ApiProblems.Problem(409, "share-conflict", "The share changed; retry");
        }
        if (transaction is not null)
            await transaction.CommitAsync(ct);
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new PersonShareDto(subject, existing?.Email ?? email, body.ExpiresAt));
    }

    public static async Task<IResult> RevokeAsync(string workspaceId, Guid gameId, string personSubject,
        HttpContext http, StarportDbContext db, IWorkspaceMemberships memberships,
        IWorkspaceEntitlements entitlements, TimeProvider clock, CancellationToken ct)
    {
        var error = await EditorErrorAsync(workspaceId, gameId, http, db, memberships, entitlements, clock, ct);
        if (error is not null) return error;
        if (string.IsNullOrWhiteSpace(personSubject) || personSubject.Length > 256)
            return ApiProblems.Problem(400, "invalid-person-share", "Invalid person share");
        var existing = await db.GamePlayGrants.SingleOrDefaultAsync(g =>
            g.GameId == gameId && g.Subject == personSubject, ct);
        if (existing is not null)
        {
            db.GamePlayGrants.Remove(existing);
            await db.SaveChangesAsync(ct);
        }
        return Results.NoContent();
    }

    private static async Task<IResult?> EditorErrorAsync(string workspaceId, Guid gameId,
        HttpContext http, StarportDbContext db, IWorkspaceMemberships memberships,
        IWorkspaceEntitlements? entitlements, TimeProvider? clock, CancellationToken ct)
    {
        var game = await db.Games.AsNoTracking().SingleOrDefaultAsync(g => g.Id == gameId, ct);
        if (game is null || game.WorkspaceId != workspaceId)
            return ApiProblems.Problem(404, "not-found", "Not found");
        var subject = http.User.Subject();
        var member = subject is null ? null : await memberships.GetAsync(workspaceId, subject, ct);
        if (member is null || member.Role == WorkspaceRole.Viewer)
            return ApiProblems.Problem(404, "not-found", "Not found");
        if (entitlements is null) return null;
        var entitlement = await entitlements.GetAsync(workspaceId, ct);
        return GameAccess.CanPublish(workspaceId, subject, member, entitlement, clock!.GetUtcNow()) ==
               AccessDecision.Allowed
            ? null
            : ApiProblems.Problem(402, "publishing-plan-required", "Publishing plan required");
    }
}

/// <summary>Hosted lookup is service-scoped; self-hosted operators may configure local email mappings.</summary>
public static class PeopleIdentityResolver
{
    public static async Task<string?> ResolveAsync(ShareWithPersonBody body, IConfiguration config,
        IServiceProvider services, CancellationToken ct)
    {
        var email = body.Email?.Trim();
        if (string.IsNullOrWhiteSpace(email) == string.IsNullOrWhiteSpace(body.Subject))
            return null;
        if (email is not null && (email.Length > 254 || !new EmailAddressAttribute().IsValid(email)))
            return null;
        if (config["Workspaces:Source"] == "local")
        {
            if (email is null)
                return ValidSubject(body.Subject) ? body.Subject!.Trim() : null;
            var mapping = config.GetSection("Sharing:LocalUsers").GetChildren()
                .FirstOrDefault(child => string.Equals(child.Key, email, StringComparison.OrdinalIgnoreCase))?.Value;
            return ValidSubject(mapping) ? mapping!.Trim() : null;
        }
        if (config["Workspaces:Source"] != "accounts" || email is null)
            return null;
        var tokens = services.GetRequiredService<AccountsServiceTokenProvider>();
        var options = services.GetRequiredService<IOptions<AccountsAccessOptions>>().Value;
        var clients = services.GetRequiredService<IHttpClientFactory>();
        var token = await tokens.GetAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(new Uri(options.BaseUrl), "v1/entitlements/identity/resolve"))
        {
            Content = JsonContent.Create(new { product = "starport", email })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var response = await clients.CreateClient("accounts-api").SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                tokens.Invalidate(token);
            if (!response.IsSuccessStatusCode)
                throw new AccountsAccessUnavailableException($"Accounts player lookup failed ({(int)response.StatusCode}).");
            var resolved = await response.Content.ReadFromJsonAsync<ResolvedPlayer>(cancellationToken: ct);
            return ValidSubject(resolved?.AccountSub) ? resolved!.AccountSub :
                throw new AccountsAccessUnavailableException("Accounts returned an invalid player identity.");
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            throw new AccountsAccessUnavailableException("Accounts player lookup is unavailable.", error);
        }
    }

    public static bool ValidSubject(string? subject) =>
        !string.IsNullOrWhiteSpace(subject) && subject.Trim().Length <= 256 &&
        !subject.Any(char.IsControl) && !subject.Contains('/');

    private sealed record ResolvedPlayer(string? AccountSub);
}
