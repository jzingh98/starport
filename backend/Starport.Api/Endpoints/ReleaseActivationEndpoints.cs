using Microsoft.EntityFrameworkCore;
using Starport.Api.Auth;
using Starport.Application.Access;
using Starport.Domain.Access;
using Starport.Infrastructure.Database;

namespace Starport.Api.Endpoints;

public sealed record ActivateReleaseBody(Guid ReleaseId);
public sealed record ActiveReleaseDto(Guid GameId, Guid ReleaseId, bool IsPublished,
    bool PublicReviewApproved, bool IsModerationSuspended);

public static class ReleaseActivationEndpoints
{
    public static void MapReleaseActivationEndpoints(this WebApplication app)
    {
        app.MapPut("/api/workspaces/{workspaceId}/games/{gameId:guid}/active-release", async (
            string workspaceId, Guid gameId, ActivateReleaseBody? body, HttpContext http,
            StarportDbContext db, IWorkspaceMemberships memberships, IWorkspaceEntitlements entitlements,
            TimeProvider clock, CancellationToken ct) =>
        {
            if (body is null || body.ReleaseId == Guid.Empty)
                return ApiProblems.Problem(400, "invalid-release", "Select a release");

            // Ownership comes from the game row. The path's workspace ID is only a
            // consistency check and is never an authority for entitlement lookups.
            var game = await db.Games.SingleOrDefaultAsync(g => g.Id == gameId, ct);
            if (game is null || game.WorkspaceId != workspaceId)
                return ApiProblems.Problem(404, "not-found", "Not found");

            var subject = http.User.Subject();
            if (subject is null)
                return ApiProblems.Problem(404, "not-found", "Not found");
            var membership = await memberships.GetAsync(game.WorkspaceId, subject, ct);
            var entitlement = await entitlements.GetAsync(game.WorkspaceId, ct);
            var permission = GameAccess.CanPublish(game.WorkspaceId, subject, membership, entitlement,
                clock.GetUtcNow());
            if (permission != AccessDecision.Allowed)
                return permission switch
                {
                    AccessDecision.NotFound => ApiProblems.Problem(404, "not-found", "Not found"),
                    AccessDecision.Forbidden => ApiProblems.Problem(403, "publisher-role-required", "Publishing role required"),
                    _ => ApiProblems.Problem(402, "publishing-plan-required", "Publishing plan required")
                };

            var release = await db.GameReleases.AsNoTracking().SingleOrDefaultAsync(r =>
                r.Id == body.ReleaseId && r.GameId == game.Id && r.IsValidated, ct);
            if (release is null)
                return ApiProblems.Problem(404, "release-not-found", "Validated release not found");

            // A public listing is reviewed for the exact bytes and metadata revision.
            // Moderators can approve this candidate before activation, preserving
            // the previously approved active release during review.
            var approved = game.Audience == GameAudience.Public &&
                await db.GameReviewApprovals.AsNoTracking().AnyAsync(a =>
                    a.GameId == game.Id && a.ReleaseId == release.Id &&
                    a.ReleaseSha256 == release.Sha256 && a.MetadataRevision == game.MetadataRevision, ct);
            if (game.Audience == GameAudience.Public && !approved)
                return ApiProblems.Problem(409, "review-required", "Public review is required");
            if (game.IsModerationSuspended)
                game.IsPublished = false;
            game.ActiveReleaseId = release.Id;
            game.UpdatedAt = clock.GetUtcNow();

            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException)
            {
                return ApiProblems.Problem(409, "game-conflict", "The game changed; reload it");
            }
            return Results.Ok(new ActiveReleaseDto(game.Id, release.Id, game.IsPublished,
                approved, game.IsModerationSuspended));
        }).RequireAuthorization();
    }
}
