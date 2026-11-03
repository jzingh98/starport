using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Starport.Api.Auth;
using Starport.Infrastructure.Database;

namespace Starport.Api.Endpoints;

public sealed record MySaveSummaryDto(Guid GameId, string GameTitle, string Slot,
    string Revision, DateTimeOffset UpdatedAt);

/// <summary>Player-owned save export and deletion remain available after a publisher goes offline.</summary>
public static class PlayerDataEndpoints
{
    public static void MapPlayerDataEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/me/saves").RequireAuthorization();
        group.MapGet("", async (HttpContext http, StarportDbContext db, CancellationToken ct) =>
        {
            var subject = http.User.Subject();
            if (subject is null) return ApiProblems.Problem(401, "sign-in-required", "Sign in required");
            var rows = await (from save in db.GameSaves.AsNoTracking()
                              join game in db.Games.AsNoTracking() on save.GameId equals game.Id
                              where save.Subject == subject
                              orderby save.UpdatedAt descending
                              select new { save.GameId, game.Title, save.Slot,
                                  save.Revision, save.UpdatedAt })
                .Take(500).ToListAsync(ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(rows.Select(row => new MySaveSummaryDto(row.GameId, row.Title,
                row.Slot, Etag(row.Revision), row.UpdatedAt)));
        });

        group.MapGet("/export", async (HttpContext http, StarportDbContext db,
            CancellationToken ct) =>
        {
            var subject = http.User.Subject();
            if (subject is null) return ApiProblems.Problem(401, "sign-in-required", "Sign in required");
            http.Response.ContentType = "application/json; charset=utf-8";
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers.ContentDisposition =
                "attachment; filename=starport-saves.json";
            await using var writer = new Utf8JsonWriter(http.Response.Body);
            writer.WriteStartObject();
            writer.WriteString("exportedAt", DateTimeOffset.UtcNow);
            writer.WriteStartArray("saves");
            await foreach (var row in db.GameSaves.AsNoTracking()
                               .Where(s => s.Subject == subject)
                               .OrderBy(s => s.GameId).ThenBy(s => s.Slot)
                               .AsAsyncEnumerable().WithCancellation(ct))
            {
                writer.WriteStartObject();
                writer.WriteString("gameId", row.GameId);
                writer.WriteString("slot", row.Slot);
                writer.WriteString("revision", Etag(row.Revision));
                writer.WriteString("updatedAt", row.UpdatedAt);
                writer.WritePropertyName("data");
                using var data = JsonDocument.Parse(row.DataJson);
                data.RootElement.WriteTo(writer);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            await writer.FlushAsync(ct);
            return Results.Empty;
        });

        group.MapGet("/{gameId:guid}/{slot}", async (Guid gameId, string slot,
            HttpContext http, StarportDbContext db, CancellationToken ct) =>
        {
            var subject = http.User.Subject();
            if (subject is null) return ApiProblems.Problem(401, "sign-in-required", "Sign in required");
            var save = await db.GameSaves.AsNoTracking().SingleOrDefaultAsync(s =>
                s.GameId == gameId && s.Subject == subject && s.Slot == slot, ct);
            if (save is null) return ApiProblems.Problem(404, "save-not-found", "Save not found");
            http.Response.Headers.ETag = Etag(save.Revision);
            http.Response.Headers.CacheControl = "no-store";
            using var data = JsonDocument.Parse(save.DataJson);
            return Results.Ok(new SaveDto(data.RootElement.Clone(), Etag(save.Revision), save.UpdatedAt));
        });

        group.MapDelete("/{gameId:guid}/{slot}", async (Guid gameId, string slot,
            HttpContext http, StarportDbContext db, CancellationToken ct) =>
        {
            var subject = http.User.Subject();
            if (subject is null) return ApiProblems.Problem(401, "sign-in-required", "Sign in required");
            var previous = ParseEtag(http.Request.Headers.IfMatch.ToString());
            if (previous is null)
                return ApiProblems.Problem(428, "save-precondition-required", "Set If-Match to delete a save");
            var deleted = await db.GameSaves.Where(s => s.GameId == gameId && s.Subject == subject &&
                s.Slot == slot && s.Revision == previous.Value).ExecuteDeleteAsync(ct);
            return deleted == 0 ? ApiProblems.Problem(412, "save-conflict", "Save revision changed") :
                Results.NoContent();
        });
    }

    private static string Etag(Guid revision) => '"' + revision.ToString("N") + '"';
    private static Guid? ParseEtag(string value) => value.Length == 34 &&
        value[0] == '"' && value[^1] == '"' &&
        Guid.TryParseExact(value[1..^1], "N", out var revision) ? revision : null;
}
