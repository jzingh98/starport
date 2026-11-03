using Microsoft.EntityFrameworkCore;
using System.Data;
using Npgsql;
using Starport.Api.Auth;
using Starport.Application.Access;
using Starport.Domain.Access;
using Starport.Infrastructure.Database;

namespace Starport.Api.Endpoints;

public sealed record UpdateGameBody(string? Title, string? Description, GameAudience Audience);
public sealed record SetPublicationBody(bool Published);
public sealed record StudioGameDto(Guid Id, string WorkspaceId, string Slug, string Title, string Description,
    GameAudience Audience, bool IsPublished, bool HasValidatedRelease, bool PublicReviewApproved,
    bool IsModerationSuspended, int MetadataRevision, Guid? ActiveReleaseId);

public static class PublicationEndpoints
{
    public static void MapPublicationEndpoints(this WebApplication app)
    {
        var studio = app.MapGroup("/api/workspaces/{workspaceId}/games").RequireAuthorization();

        studio.MapGet("/{gameId:guid}", async (string workspaceId, Guid gameId, HttpContext http,
            StarportDbContext db, IWorkspaceMemberships memberships, CancellationToken ct) =>
        {
            var game = await db.Games.AsNoTracking().SingleOrDefaultAsync(g => g.Id == gameId, ct);
            if (game is null || game.WorkspaceId != workspaceId ||
                await memberships.GetAsync(game.WorkspaceId, http.User.Subject()!, ct) is not
                    { Role: not WorkspaceRole.Viewer })
                return ApiProblems.Problem(404, "not-found", "Not found");
            return Results.Ok(await ToDto(game, db, ct));
        });

        studio.MapPut("/{gameId:guid}", async (string workspaceId, Guid gameId, UpdateGameBody? body,
            HttpContext http, StarportDbContext db, IWorkspaceMemberships memberships,
            IWorkspaceEntitlements entitlements, TimeProvider clock, CancellationToken ct) =>
        {
            if (body?.Title is null || body.Description is null ||
                string.IsNullOrWhiteSpace(body.Title) || body.Title.Trim().Length > 200 ||
                body.Description.Length > 2000 || !Enum.IsDefined(body.Audience))
                return ApiProblems.Problem(400, "invalid-game", "Invalid game details");
            var game = await db.Games.SingleOrDefaultAsync(g => g.Id == gameId, ct);
            if (game is null || game.WorkspaceId != workspaceId)
                return ApiProblems.Problem(404, "not-found", "Not found");
            var decision = await CanEdit(game, http.User.Subject()!, memberships, entitlements, clock, ct);
            if (decision != AccessDecision.Allowed)
                return EditProblem(decision);

            var title = body.Title.Trim();
            var description = body.Description.Trim();
            if (game.Title != title || game.Description != description || game.Audience != body.Audience)
            {
                var wasPublic = game.Audience == GameAudience.Public;
                game.Title = title;
                game.Description = description;
                game.Audience = body.Audience;
                game.MetadataRevision++;
                game.UpdatedAt = clock.GetUtcNow();
                // Public content cannot remain visible after changing reviewed metadata or audience.
                if (wasPublic || body.Audience == GameAudience.Public)
                    game.IsPublished = false;
                try { await db.SaveChangesAsync(ct); }
                catch (DbUpdateConcurrencyException)
                {
                    return ApiProblems.Problem(409, "game-conflict", "The game changed; reload it");
                }
            }
            return Results.Ok(await ToDto(game, db, ct));
        });

        studio.MapPut("/{gameId:guid}/publication", async (
            string workspaceId, Guid gameId, SetPublicationBody? body, HttpContext http,
            StarportDbContext db, IWorkspaceMemberships memberships, IWorkspaceEntitlements entitlements,
            TimeProvider clock, CancellationToken ct) =>
        {
            if (body is null)
                return ApiProblems.Problem(400, "invalid-publication", "Invalid publication request");
            var game = await db.Games.SingleOrDefaultAsync(g => g.Id == gameId, ct);
            if (game is null || game.WorkspaceId != workspaceId)
                return ApiProblems.Problem(404, "not-found", "Not found");
            var subject = http.User.Subject()!;
            var membership = await memberships.GetAsync(game.WorkspaceId, subject, ct);
            if (membership is not { Role: not WorkspaceRole.Viewer })
                return ApiProblems.Problem(404, "not-found", "Not found");

            Starport.Domain.Access.PublishingEntitlement? entitlement = null;
            if (body.Published)
            {
                entitlement = await entitlements.GetAsync(game.WorkspaceId, ct);
                var permission = GameAccess.CanPublish(game.WorkspaceId, subject, membership, entitlement,
                    clock.GetUtcNow());
                if (permission != AccessDecision.Allowed)
                    return EditProblem(permission);
                if (game.Audience == GameAudience.Private)
                    return ApiProblems.Problem(400, "audience-required", "Choose who can play first");
                var snapshot = await new GameRepository(db).GetAsync(game.Id, ct);
                if (snapshot is null || !snapshot.Access.HasValidatedRelease || game.IsModerationSuspended)
                    return ApiProblems.Problem(409, "release-required", "A validated release is required");
                if (game.Audience == GameAudience.Public && !snapshot.Access.PublicReviewApproved)
                    return ApiProblems.Problem(409, "review-required", "Public review is required");
            }
            await using var quotaTransaction = body.Published && !game.IsPublished &&
                entitlement?.Limit("published_games") is not null
                ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)
                : null;
            if (quotaTransaction is not null)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    SELECT 1 FROM "Workspaces" WHERE "Id" = {game.WorkspaceId} FOR NO KEY UPDATE
                    """, ct);
                var published = await db.Games.CountAsync(g => g.WorkspaceId == game.WorkspaceId &&
                    g.IsPublished, ct);
                if (published >= entitlement!.Limit("published_games"))
                    return ApiProblems.Problem(402, "published-game-limit", "Published game limit reached");
            }
            game.IsPublished = body.Published;
            game.UpdatedAt = clock.GetUtcNow();
            try
            {
                await db.SaveChangesAsync(ct);
                if (quotaTransaction is not null) await quotaTransaction.CommitAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                return ApiProblems.Problem(409, "game-conflict", "The game changed; reload it");
            }
            catch (Exception error) when (error is PostgresException { SqlState: "40001" } ||
                error is DbUpdateException { InnerException: PostgresException { SqlState: "40001" } })
            {
                return ApiProblems.Problem(409, "publication-conflict", "Another game was published; retry");
            }
            return Results.Ok(await ToDto(game, db, ct));
        });

        app.MapPost("/api/admin/games/{gameId:guid}/approval", async (
            Guid gameId, HttpContext http, IConfiguration config, StarportDbContext db,
            TimeProvider clock, CancellationToken ct) =>
            await ApproveRelease(gameId, null, http, config, db, clock, ct))
            .RequireAuthorization();

        app.MapPost("/api/admin/games/{gameId:guid}/releases/{releaseId:guid}/approval", async (
            Guid gameId, Guid releaseId, HttpContext http, IConfiguration config, StarportDbContext db,
            TimeProvider clock, CancellationToken ct) =>
            await ApproveRelease(gameId, releaseId, http, config, db, clock, ct))
            .RequireAuthorization();
    }

    private static async Task<IResult> ApproveRelease(Guid gameId, Guid? releaseId,
        HttpContext http, IConfiguration config, StarportDbContext db,
        TimeProvider clock, CancellationToken ct)
        {
            if (!OperatorAuthorization.CanModerate(http.User, config))
                return ApiProblems.Problem(404, "not-found", "Not found");
            var game = await db.Games.SingleOrDefaultAsync(g => g.Id == gameId, ct);
            if (game is null || (releaseId is null && game.ActiveReleaseId is null) ||
                game.IsModerationSuspended)
                return ApiProblems.Problem(404, "not-found", "Not found");
            var release = await db.GameReleases.SingleOrDefaultAsync(r =>
                r.Id == (releaseId ?? game.ActiveReleaseId) && r.GameId == game.Id && r.IsValidated, ct);
            if (release is null)
                return ApiProblems.Problem(409, "release-required", "A validated release is required");
            var approved = await db.GameReviewApprovals.AnyAsync(a => a.GameId == game.Id &&
                a.ReleaseId == release.Id && a.MetadataRevision == game.MetadataRevision &&
                a.ReleaseSha256 == release.Sha256, ct);
            if (!approved)
                db.GameReviewApprovals.Add(new GameReviewApprovalRecord
                {
                    Id = Guid.NewGuid(), GameId = game.Id, ReleaseId = release.Id,
                    ReleaseSha256 = release.Sha256, MetadataRevision = game.MetadataRevision,
                    ReviewerSubject = http.User.Subject()!, ApprovedAt = clock.GetUtcNow()
                });
            var request = await db.GameReviewRequests.SingleOrDefaultAsync(r =>
                r.GameId == game.Id && r.ReleaseId == release.Id &&
                r.MetadataRevision == game.MetadataRevision &&
                r.Status == GameReviewStatus.Pending, ct);
            if (request is not null)
            {
                request.Status = GameReviewStatus.Approved;
                request.ReviewerSubject = http.User.Subject();
                request.DecidedAt = clock.GetUtcNow();
            }
            if (approved && request is null) return Results.NoContent();
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException error) when (error.InnerException is PostgresException { SqlState: "23505" })
            {
                return Results.NoContent();
            }
            return Results.NoContent();
        }

    private static async Task<AccessDecision> CanEdit(GameRecord game, string subject,
        IWorkspaceMemberships memberships, IWorkspaceEntitlements entitlements,
        TimeProvider clock, CancellationToken ct)
    {
        var membership = await memberships.GetAsync(game.WorkspaceId, subject, ct);
        var entitlement = await entitlements.GetAsync(game.WorkspaceId, ct);
        return GameAccess.CanPublish(game.WorkspaceId, subject, membership, entitlement, clock.GetUtcNow());
    }

    private static IResult EditProblem(AccessDecision decision) => decision switch
    {
        AccessDecision.NotFound => ApiProblems.Problem(404, "not-found", "Not found"),
        AccessDecision.Forbidden => ApiProblems.Problem(403, "publisher-role-required", "Publishing role required"),
        _ => ApiProblems.Problem(402, "publishing-plan-required", "Publishing plan required")
    };

    private static async Task<StudioGameDto> ToDto(GameRecord game, StarportDbContext db, CancellationToken ct)
    {
        var access = await new GameRepository(db).GetAsync(game.Id, ct);
        return new(game.Id, game.WorkspaceId, game.Slug, game.Title, game.Description,
            game.Audience, game.IsPublished, access?.Access.HasValidatedRelease == true,
            access?.Access.PublicReviewApproved == true, game.IsModerationSuspended,
            game.MetadataRevision, game.ActiveReleaseId);
    }
}
