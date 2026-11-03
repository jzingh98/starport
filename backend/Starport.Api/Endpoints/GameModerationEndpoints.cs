using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Starport.Api.Auth;
using Starport.Application.Access;
using Starport.Infrastructure.Database;

namespace Starport.Api.Endpoints;

public sealed record CreateGameReportBody(string? Category, string? Description);
public sealed record ResolveGameReportBody(string? Resolution);
public sealed record SuspendGameBody(bool Suspended, string? Reason);
public sealed record GameReportDto(Guid Id, Guid GameId, string GameTitle, string Category,
    string Description, string ReporterSubject, DateTimeOffset CreatedAt,
    bool IsModerationSuspended);

public static class GameModerationEndpoints
{
    private static readonly HashSet<string> Categories = new(StringComparer.Ordinal)
        { "abuse", "copyright", "malware", "other" };

    public static void MapGameModerationEndpoints(this WebApplication app)
    {
        app.MapPost("/api/games/{gameId:guid}/reports", async (Guid gameId,
            CreateGameReportBody? body, HttpContext http, GameReadService access,
            StarportDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var subject = http.User.Subject();
            if (subject is null)
                return ApiProblems.Problem(401, "sign-in-required", "Sign in required");
            var category = body?.Category?.Trim().ToLowerInvariant();
            var description = body?.Description?.Trim();
            if (category is null || !Categories.Contains(category) ||
                string.IsNullOrWhiteSpace(description) || description.Length > 2000)
                return ApiProblems.Problem(400, "invalid-report", "Choose a reason and describe the issue");
            if ((await access.ReadAsync(gameId, subject, cancellationToken: ct)).Game is null)
                return ApiProblems.Problem(404, "not-found", "Not found");
            var existing = await db.GameReports.AsNoTracking().FirstOrDefaultAsync(r =>
                r.GameId == gameId && r.ReporterSubject == subject &&
                r.Status == GameReportStatus.New, ct);
            if (existing is not null)
                return Results.Ok(new { id = existing.Id, status = existing.Status.ToString() });
            var report = new GameReportRecord
            {
                Id = Guid.NewGuid(), GameId = gameId, ReporterSubject = subject,
                Category = category, Description = description, CreatedAt = clock.GetUtcNow()
            };
            db.GameReports.Add(report);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/admin/reports/{report.Id}",
                new { id = report.Id, status = report.Status.ToString() });
        }).RequireAuthorization().RequireRateLimiting("report");

        app.MapGet("/api/admin/reports", async (HttpContext http, IConfiguration config,
            StarportDbContext db, CancellationToken ct) =>
        {
            if (!OperatorAuthorization.CanModerate(http.User, config))
                return ApiProblems.Problem(404, "not-found", "Not found");
            var reports = await (from report in db.GameReports.AsNoTracking()
                                 join game in db.Games.AsNoTracking()
                                     on report.GameId equals game.Id
                                 where report.Status == GameReportStatus.New
                                 orderby report.CreatedAt
                                 select new GameReportDto(report.Id, game.Id, game.Title,
                                     report.Category, report.Description,
                                     report.ReporterSubject, report.CreatedAt,
                                     game.IsModerationSuspended))
                .Take(100).ToListAsync(ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(reports);
        }).RequireAuthorization();

        app.MapPost("/api/admin/reports/{reportId:guid}/resolve", async (Guid reportId,
            ResolveGameReportBody? body, HttpContext http, IConfiguration config,
            StarportDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (!OperatorAuthorization.CanModerate(http.User, config))
                return ApiProblems.Problem(404, "not-found", "Not found");
            var resolution = body?.Resolution?.Trim();
            if (string.IsNullOrWhiteSpace(resolution) || resolution.Length > 1000)
                return ApiProblems.Problem(400, "invalid-resolution", "Resolution is required");
            var changed = await db.GameReports.Where(r => r.Id == reportId &&
                r.Status == GameReportStatus.New)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(r => r.Status, GameReportStatus.Resolved)
                    .SetProperty(r => r.ResolvedBySubject, http.User.Subject())
                    .SetProperty(r => r.ResolvedAt, clock.GetUtcNow())
                    .SetProperty(r => r.Resolution, resolution), ct);
            return changed == 0 ? ApiProblems.Problem(404, "not-found", "Not found") :
                Results.NoContent();
        }).RequireAuthorization();

        app.MapPut("/api/admin/games/{gameId:guid}/suspension", async (Guid gameId,
            SuspendGameBody? body, HttpContext http, IConfiguration config,
            StarportDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (!OperatorAuthorization.CanModerate(http.User, config))
                return ApiProblems.Problem(404, "not-found", "Not found");
            var reason = body?.Reason?.Trim();
            if (string.IsNullOrWhiteSpace(reason) || reason.Length > 1000)
                return ApiProblems.Problem(400, "invalid-suspension", "A reason is required");
            var game = await db.Games.SingleOrDefaultAsync(g => g.Id == gameId, ct);
            if (game is null) return ApiProblems.Problem(404, "not-found", "Not found");
            game.IsModerationSuspended = body!.Suspended;
            if (body.Suspended) game.IsPublished = false;
            game.UpdatedAt = clock.GetUtcNow();
            db.GameModerationActions.Add(new GameModerationActionRecord
            {
                Id = Guid.NewGuid(), GameId = game.Id,
                ModeratorSubject = http.User.Subject()!, Suspended = body.Suspended,
                Reason = reason, CreatedAt = clock.GetUtcNow()
            });
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException)
            { return ApiProblems.Problem(409, "game-conflict", "The game changed; reload it"); }
            return Results.Ok(new { gameId, suspended = game.IsModerationSuspended,
                published = game.IsPublished });
        }).RequireAuthorization();
    }
}
