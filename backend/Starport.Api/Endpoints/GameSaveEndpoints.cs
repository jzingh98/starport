using System.Text;
using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Starport.Api.Auth;
using Starport.Application.Access;
using Starport.Infrastructure.Database;

namespace Starport.Api.Endpoints;

public sealed record PutSaveBody(JsonElement Data, string? RequestKey);
public sealed record SaveSummaryDto(string Slot, string Revision, DateTimeOffset UpdatedAt);
public sealed record SaveDto(JsonElement Data, string Revision, DateTimeOffset UpdatedAt);

public static partial class GameSaveEndpoints
{
    private const int MaxSlots = 10;
    private const int MaxDataBytes = 1024 * 1024;

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex SlotPattern();

    public static void MapGameSaveEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/games/{gameId:guid}/saves").RequireAuthorization();
        group.MapGet("", async (Guid gameId, HttpContext http, GameReadService access,
            StarportDbContext db, CancellationToken ct) =>
        {
            var subject = http.User.Subject();
            if (subject is null) return ApiProblems.Problem(401, "sign-in-required", "Sign in required");
            if ((await access.ReadAsync(gameId, subject,
                    preview: http.Request.Query["preview"] == "true", cancellationToken: ct)).Game is null)
                return ApiProblems.Problem(404, "not-found", "Not found");
            var rows = await db.GameSaves.AsNoTracking()
                .Where(s => s.GameId == gameId && s.Subject == subject)
                .OrderBy(s => s.Slot)
                .Select(s => new SaveSummaryDto(s.Slot, '"' + s.Revision.ToString("N") + '"', s.UpdatedAt))
                .ToListAsync(ct);
            return Results.Ok(rows);
        });

        group.MapGet("/{slot}", async (Guid gameId, string slot, HttpContext http,
            GameReadService access, StarportDbContext db, CancellationToken ct) =>
        {
            if (!ValidSlot(slot)) return ApiProblems.Problem(400, "invalid-slot", "Invalid save slot");
            var subject = http.User.Subject();
            if (subject is null) return ApiProblems.Problem(401, "sign-in-required", "Sign in required");
            if ((await access.ReadAsync(gameId, subject,
                    preview: http.Request.Query["preview"] == "true", cancellationToken: ct)).Game is null)
                return ApiProblems.Problem(404, "not-found", "Not found");
            var row = await db.GameSaves.AsNoTracking().SingleOrDefaultAsync(s =>
                s.GameId == gameId && s.Subject == subject && s.Slot == slot, ct);
            if (row is null) return ApiProblems.Problem(404, "save-not-found", "Save not found");
            http.Response.Headers.ETag = Etag(row.Revision);
            http.Response.Headers.CacheControl = "no-store";
            using var document = JsonDocument.Parse(row.DataJson);
            return Results.Ok(new SaveDto(document.RootElement.Clone(), Etag(row.Revision), row.UpdatedAt));
        });

        group.MapPut("/{slot}", async (Guid gameId, string slot, PutSaveBody? body,
            HttpContext http, GameReadService access, StarportDbContext db,
            TimeProvider clock, CancellationToken ct) =>
        {
            if (!ValidSlot(slot)) return ApiProblems.Problem(400, "invalid-slot", "Invalid save slot");
            if (body is null || body.Data.ValueKind == JsonValueKind.Undefined)
                return ApiProblems.Problem(400, "invalid-save", "Save data is required");
            var data = body.Data.GetRawText();
            if (Encoding.UTF8.GetByteCount(data) > MaxDataBytes)
                return ApiProblems.Problem(413, "save-too-large", "Save data exceeds 1 MiB");
            var requestKey = string.IsNullOrWhiteSpace(body.RequestKey) ? null : body.RequestKey.Trim();
            if (requestKey is { Length: > 128 } || requestKey?.Any(char.IsControl) == true)
                return ApiProblems.Problem(400, "invalid-request-key", "Invalid save request key");
            var subject = http.User.Subject();
            if (subject is null) return ApiProblems.Problem(401, "sign-in-required", "Sign in required");
            if ((await access.ReadAsync(gameId, subject,
                    preview: http.Request.Query["preview"] == "true", cancellationToken: ct)).Game is null)
                return ApiProblems.Problem(404, "not-found", "Not found");
            var now = clock.GetUtcNow();
            var revision = Guid.NewGuid();
            var previous = http.Request.Headers.IfMatch.ToString();
            var precondition = string.IsNullOrEmpty(previous)
                ? http.Request.Headers.IfNoneMatch.ToString() : previous;
            var dataHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(data)));
            if (requestKey is not null)
            {
                var prior = await db.GameSaveMutations.AsNoTracking().SingleOrDefaultAsync(m =>
                    m.GameId == gameId && m.Subject == subject && m.RequestKey == requestKey, ct);
                if (prior is not null)
                {
                    if (prior.Slot != slot || prior.DataSha256 != dataHash ||
                        prior.Precondition != precondition)
                        return ApiProblems.Problem(409, "request-key-reused", "Save request key was reused");
                    http.Response.Headers.ETag = Etag(prior.Revision);
                    http.Response.Headers.CacheControl = "no-store";
                    return Results.Ok(new SaveSummaryDto(slot, Etag(prior.Revision), prior.UpdatedAt));
                }
            }
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, ct);
            try
            {
                if (!string.IsNullOrEmpty(previous))
                {
                    var old = ParseEtag(previous);
                    if (old is null) return ApiProblems.Problem(412, "save-conflict", "Save revision changed");
                    var changed = await db.GameSaves.Where(s => s.GameId == gameId &&
                        s.Subject == subject && s.Slot == slot && s.Revision == old.Value)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(s => s.DataJson, data)
                            .SetProperty(s => s.Revision, revision)
                            .SetProperty(s => s.UpdatedAt, now), ct);
                    if (changed == 0) return ApiProblems.Problem(412, "save-conflict", "Save revision changed");
                }
                else
                {
                    if (precondition != "*")
                        return ApiProblems.Problem(428, "save-precondition-required", "Set If-None-Match: * when creating a save");
                    if (await db.GameSaves.CountAsync(s => s.GameId == gameId && s.Subject == subject, ct) >= MaxSlots)
                        return ApiProblems.Problem(409, "save-limit", "Save slot limit reached");
                    db.GameSaves.Add(new GameSaveRecord
                    {
                        GameId = gameId, Subject = subject, Slot = slot, DataJson = data,
                        Revision = revision, UpdatedAt = now
                    });
                }
                if (requestKey is not null)
                    db.GameSaveMutations.Add(new GameSaveMutationRecord
                    {
                        GameId = gameId, Subject = subject, RequestKey = requestKey,
                        Slot = slot, DataSha256 = dataHash, Precondition = precondition,
                        Revision = revision, UpdatedAt = now, CreatedAt = now
                    });
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            catch (DbUpdateException error) when (error.InnerException is PostgresException { SqlState: "23505" })
            {
                return (error.InnerException as PostgresException)?.ConstraintName == "PK_GameSaves"
                    ? ApiProblems.Problem(412, "save-conflict", "Save revision changed")
                    : ApiProblems.Problem(409, "save-write-conflict", "Save changed; retry");
            }
            catch (Exception error) when (error is PostgresException { SqlState: "40001" } ||
                error is DbUpdateException { InnerException: PostgresException { SqlState: "40001" } })
            { return ApiProblems.Problem(409, "save-write-conflict", "Save changed; retry"); }
            http.Response.Headers.ETag = Etag(revision);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new SaveSummaryDto(slot, Etag(revision), now));
        }).WithMetadata(new RequestSizeLimitAttribute(1_100_000));

        group.MapDelete("/{slot}", async (Guid gameId, string slot, HttpContext http,
            GameReadService access, StarportDbContext db, CancellationToken ct) =>
        {
            if (!ValidSlot(slot)) return ApiProblems.Problem(400, "invalid-slot", "Invalid save slot");
            var subject = http.User.Subject();
            if (subject is null) return ApiProblems.Problem(401, "sign-in-required", "Sign in required");
            if ((await access.ReadAsync(gameId, subject,
                    preview: http.Request.Query["preview"] == "true", cancellationToken: ct)).Game is null)
                return ApiProblems.Problem(404, "not-found", "Not found");
            var previous = ParseEtag(http.Request.Headers.IfMatch.ToString());
            if (previous is null)
                return ApiProblems.Problem(428, "save-precondition-required", "Set If-Match to delete a save");
            var removed = await db.GameSaves.Where(s => s.GameId == gameId && s.Subject == subject &&
                s.Slot == slot && s.Revision == previous.Value).ExecuteDeleteAsync(ct);
            return removed == 0 ? ApiProblems.Problem(412, "save-conflict", "Save revision changed") :
                Results.NoContent();
        });
    }

    private static bool ValidSlot(string slot) => SlotPattern().IsMatch(slot);
    private static string Etag(Guid revision) => '"' + revision.ToString("N") + '"';
    private static Guid? ParseEtag(string value) => value.Length == 34 &&
        value[0] == '"' && value[^1] == '"' &&
        Guid.TryParseExact(value[1..^1], "N", out var revision) ? revision : null;
}
