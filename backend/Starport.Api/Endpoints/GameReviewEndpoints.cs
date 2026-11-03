using Microsoft.EntityFrameworkCore;
using Npgsql;
using Starport.Api.Auth;
using Starport.Application.Access;
using Starport.Domain.Access;
using Starport.Infrastructure.Database;

namespace Starport.Api.Endpoints;

public sealed record SubmitReviewBody(Guid ReleaseId);
public sealed record DecideReviewBody(bool Approved, string? Reason);
public sealed record GameReleaseDto(Guid Id, DateTimeOffset CreatedAt, string Sha256,
    long Bytes, string ReviewStatus, string? ReviewReason);
public sealed record GameReviewDto(Guid Id, Guid GameId, Guid ReleaseId, int MetadataRevision,
    string Status, DateTimeOffset RequestedAt, string? Reason);
public sealed record PendingReviewDto(Guid Id, Guid GameId, string GameSlug,
    string GameTitle, string GameDescription,
    Guid ReleaseId, string ReleaseSha256, int MetadataRevision, DateTimeOffset RequestedAt);

public static class GameReviewEndpoints
{
    public static void MapGameReviewEndpoints(this WebApplication app)
    {
        var studio = app.MapGroup("/api/workspaces/{workspaceId}/games/{gameId:guid}")
            .RequireAuthorization();
        studio.MapGet("/releases", async (string workspaceId, Guid gameId, HttpContext http,
            StarportDbContext db, IWorkspaceMemberships memberships, CancellationToken ct) =>
        {
            if (!await CanViewStudio(workspaceId, gameId, http, db, memberships, ct))
                return ApiProblems.Problem(404, "not-found", "Not found");
            var game = await db.Games.AsNoTracking().SingleAsync(g => g.Id == gameId, ct);
            var releases = await db.GameReleases.AsNoTracking()
                .Where(r => r.GameId == gameId && r.IsValidated)
                .OrderByDescending(r => r.CreatedAt).ToListAsync(ct);
            var approvals = await db.GameReviewApprovals.AsNoTracking()
                .Where(a => a.GameId == gameId && a.MetadataRevision == game.MetadataRevision)
                .ToListAsync(ct);
            var requests = await db.GameReviewRequests.AsNoTracking()
                .Where(r => r.GameId == gameId && r.MetadataRevision == game.MetadataRevision)
                .ToListAsync(ct);
            var sizes = await db.ReleaseFiles.AsNoTracking()
                .Where(f => releases.Select(r => r.Id).Contains(f.ReleaseId))
                .GroupBy(f => f.ReleaseId)
                .Select(group => new { ReleaseId = group.Key, Bytes = group.Sum(f => f.Bytes) })
                .ToDictionaryAsync(x => x.ReleaseId, x => x.Bytes, ct);
            var result = releases.Select(release =>
            {
                var approved = approvals.Any(a => a.ReleaseId == release.Id &&
                    a.ReleaseSha256 == release.Sha256);
                var request = requests.FirstOrDefault(r => r.ReleaseId == release.Id &&
                    r.ReleaseSha256 == release.Sha256);
                var status = approved ? "Approved" : request?.Status.ToString() ?? "NotRequested";
                return new GameReleaseDto(release.Id, release.CreatedAt, release.Sha256,
                    sizes.GetValueOrDefault(release.Id), status, request?.DecisionReason);
            }).ToList();
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(result);
        });

        studio.MapDelete("/releases/{releaseId:guid}", async (string workspaceId,
            Guid gameId, Guid releaseId, HttpContext http, StarportDbContext db,
            IWorkspaceMemberships memberships, TimeProvider clock, CancellationToken ct) =>
        {
            if (!await CanViewStudio(workspaceId, gameId, http, db, memberships, ct))
                return ApiProblems.Problem(404, "not-found", "Not found");
            var game = await db.Games.SingleAsync(g => g.Id == gameId, ct);
            if (game.ActiveReleaseId == releaseId)
                return ApiProblems.Problem(409, "active-release", "Activate another release before removing this one");
            var release = await db.GameReleases.SingleOrDefaultAsync(r =>
                r.Id == releaseId && r.GameId == gameId, ct);
            if (release is null) return ApiProblems.Problem(404, "release-not-found", "Release not found");
            if (!release.IsValidated) return Results.Ok(new { status = "deleting" });
            release.IsValidated = false;
            game.UpdatedAt = clock.GetUtcNow();
            var pending = await db.GameReviewRequests.Where(r => r.ReleaseId == releaseId &&
                r.Status == GameReviewStatus.Pending).ToListAsync(ct);
            foreach (var request in pending)
            {
                request.Status = GameReviewStatus.Rejected;
                request.DecidedAt = clock.GetUtcNow();
                request.DecisionReason = "Publisher removed this release";
            }
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException)
            { return ApiProblems.Problem(409, "game-conflict", "The game changed; reload it"); }
            return Results.Ok(new { status = "deleting" });
        });

        studio.MapPost("/review-requests", async (string workspaceId, Guid gameId,
            SubmitReviewBody? body, HttpContext http, StarportDbContext db,
            IWorkspaceMemberships memberships, IWorkspaceEntitlements entitlements,
            TimeProvider clock, CancellationToken ct) =>
        {
            if (body is null || body.ReleaseId == Guid.Empty)
                return ApiProblems.Problem(400, "invalid-review-request", "Select a release");
            var game = await db.Games.SingleOrDefaultAsync(g => g.Id == gameId &&
                g.WorkspaceId == workspaceId, ct);
            if (game is null) return ApiProblems.Problem(404, "not-found", "Not found");
            var subject = http.User.Subject();
            var member = subject is null ? null : await memberships.GetAsync(workspaceId, subject, ct);
            var entitlement = await entitlements.GetAsync(workspaceId, ct);
            var decision = GameAccess.CanPublish(workspaceId, subject, member, entitlement,
                clock.GetUtcNow());
            if (decision != AccessDecision.Allowed)
                return decision switch
                {
                    AccessDecision.Forbidden => ApiProblems.Problem(403, "publisher-role-required", "Publishing role required"),
                    AccessDecision.PublisherOffline => ApiProblems.Problem(402, "publishing-plan-required", "Publishing plan required"),
                    _ => ApiProblems.Problem(404, "not-found", "Not found")
                };
            if (game.Audience != GameAudience.Public || game.IsModerationSuspended)
                return ApiProblems.Problem(409, "public-audience-required", "Choose Public access before review");
            var release = await db.GameReleases.AsNoTracking().SingleOrDefaultAsync(r =>
                r.Id == body.ReleaseId && r.GameId == gameId && r.IsValidated, ct);
            if (release is null) return ApiProblems.Problem(404, "release-not-found", "Validated release not found");
            var existing = await db.GameReviewRequests.SingleOrDefaultAsync(r =>
                r.GameId == gameId && r.ReleaseId == release.Id &&
                r.MetadataRevision == game.MetadataRevision, ct);
            if (existing is not null)
                return Results.Ok(ToDto(existing));
            var approval = await db.GameReviewApprovals.AsNoTracking().SingleOrDefaultAsync(a =>
                a.GameId == gameId && a.ReleaseId == release.Id &&
                a.ReleaseSha256 == release.Sha256 &&
                a.MetadataRevision == game.MetadataRevision, ct);
            var request = new GameReviewRequestRecord
            {
                Id = Guid.NewGuid(), GameId = gameId, ReleaseId = release.Id,
                MetadataRevision = game.MetadataRevision, ReleaseSha256 = release.Sha256,
                Status = approval is null ? GameReviewStatus.Pending : GameReviewStatus.Approved,
                RequestedBySubject = subject!, RequestedAt = clock.GetUtcNow(),
                ReviewerSubject = approval?.ReviewerSubject, DecidedAt = approval?.ApprovedAt
            };
            db.GameReviewRequests.Add(request);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException error) when (error.InnerException is PostgresException { SqlState: "23505" })
                { return ApiProblems.Problem(409, "review-request-conflict", "Review request already exists"); }
            return Results.Created($"/api/workspaces/{workspaceId}/games/{gameId}/review-requests/{request.Id}",
                ToDto(request));
        });

        app.MapGet("/api/admin/review-requests", async (HttpContext http, IConfiguration config,
            StarportDbContext db, CancellationToken ct) =>
        {
            if (!OperatorAuthorization.CanModerate(http.User, config))
                return ApiProblems.Problem(404, "not-found", "Not found");
            var rows = await (from request in db.GameReviewRequests.AsNoTracking()
                              join game in db.Games.AsNoTracking() on request.GameId equals game.Id
                              where request.Status == GameReviewStatus.Pending
                              orderby request.RequestedAt
                              select new PendingReviewDto(request.Id, game.Id, game.Slug,
                                  game.Title, game.Description,
                                  request.ReleaseId, request.ReleaseSha256,
                                  request.MetadataRevision, request.RequestedAt))
                .Take(100).ToListAsync(ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(rows);
        }).RequireAuthorization();

        app.MapPost("/api/admin/review-requests/{requestId:guid}/decision", async (
            Guid requestId, DecideReviewBody? body, HttpContext http, IConfiguration config,
            StarportDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (!OperatorAuthorization.CanModerate(http.User, config))
                return ApiProblems.Problem(404, "not-found", "Not found");
            if (body is null || body.Reason?.Length > 500)
                return ApiProblems.Problem(400, "invalid-review-decision", "Invalid review decision");
            var request = await db.GameReviewRequests.SingleOrDefaultAsync(r => r.Id == requestId, ct);
            if (request is null) return ApiProblems.Problem(404, "not-found", "Not found");
            if (request.Status != GameReviewStatus.Pending)
                return ApiProblems.Problem(409, "review-already-decided", "Review already decided");
            var game = await db.Games.AsNoTracking().SingleOrDefaultAsync(g => g.Id == request.GameId, ct);
            var release = await db.GameReleases.AsNoTracking().SingleOrDefaultAsync(r =>
                r.Id == request.ReleaseId && r.GameId == request.GameId && r.IsValidated, ct);
            if (game is null || release is null)
                return ApiProblems.Problem(404, "not-found", "Not found");
            if (body.Approved)
            {
                if (game.Audience != GameAudience.Public || game.IsModerationSuspended ||
                    game.MetadataRevision != request.MetadataRevision ||
                    release.Sha256 != request.ReleaseSha256)
                    return ApiProblems.Problem(409, "review-stale", "Game changed since review was requested");
                db.GameReviewApprovals.Add(new GameReviewApprovalRecord
                {
                    Id = Guid.NewGuid(), GameId = game.Id, ReleaseId = release.Id,
                    ReleaseSha256 = release.Sha256, MetadataRevision = request.MetadataRevision,
                    ReviewerSubject = http.User.Subject()!, ApprovedAt = clock.GetUtcNow()
                });
            }
            request.Status = body.Approved ? GameReviewStatus.Approved : GameReviewStatus.Rejected;
            request.ReviewerSubject = http.User.Subject();
            request.DecidedAt = clock.GetUtcNow();
            request.DecisionReason = body.Approved ? null : body.Reason?.Trim();
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException error) when (error.InnerException is PostgresException { SqlState: "23505" })
                { return ApiProblems.Problem(409, "review-conflict", "Review changed; reload it"); }
            return Results.Ok(ToDto(request));
        }).RequireAuthorization();
    }

    private static async Task<bool> CanViewStudio(string workspaceId, Guid gameId,
        HttpContext http, StarportDbContext db, IWorkspaceMemberships memberships,
        CancellationToken ct)
    {
        var game = await db.Games.AsNoTracking().SingleOrDefaultAsync(g => g.Id == gameId, ct);
        var subject = http.User.Subject();
        return game is not null && game.WorkspaceId == workspaceId && subject is not null &&
            await memberships.GetAsync(workspaceId, subject, ct) is
                { Role: not WorkspaceRole.Viewer };
    }

    private static GameReviewDto ToDto(GameReviewRequestRecord request) =>
        new(request.Id, request.GameId, request.ReleaseId, request.MetadataRevision,
            request.Status.ToString(), request.RequestedAt, request.DecisionReason);
}
