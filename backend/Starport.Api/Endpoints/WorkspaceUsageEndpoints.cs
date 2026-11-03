using Microsoft.EntityFrameworkCore;
using Starport.Api.Auth;
using Starport.Application.Access;
using Starport.Domain.Access;
using Starport.Infrastructure.Database;

namespace Starport.Api.Endpoints;

public sealed record WorkspaceUsageDto(int Games, int PublishedGames, long RetainedBytes,
    long DeliveredBytesThisMonth, IReadOnlyDictionary<string, int?> Limits,
    DateTimeOffset? PaidThrough);

public static class WorkspaceUsageEndpoints
{
    public static void MapWorkspaceUsageEndpoints(this WebApplication app)
    {
        app.MapGet("/api/workspaces/{workspaceId}/usage", async (string workspaceId,
            HttpContext http, IWorkspaceMemberships memberships,
            IWorkspaceEntitlements entitlements, StarportDbContext db,
            TimeProvider clock, CancellationToken ct) =>
        {
            var subject = http.User.Subject();
            if (subject is null || await memberships.GetAsync(workspaceId, subject, ct) is not
                { Role: not WorkspaceRole.Viewer })
                return ApiProblems.Problem(404, "not-found", "Not found");
            var entitlement = await entitlements.GetAsync(workspaceId, ct);
            var games = await db.Games.CountAsync(g => g.WorkspaceId == workspaceId, ct);
            var published = await db.Games.CountAsync(g => g.WorkspaceId == workspaceId &&
                g.IsPublished, ct);
            var retained = await (from file in db.ReleaseFiles.AsNoTracking()
                                  join release in db.GameReleases.AsNoTracking()
                                      on file.ReleaseId equals release.Id
                                  join game in db.Games.AsNoTracking()
                                      on release.GameId equals game.Id
                                  where game.WorkspaceId == workspaceId
                                  select (long?)file.Bytes).SumAsync(ct) ?? 0;
            var now = clock.GetUtcNow().UtcDateTime;
            var month = new DateOnly(now.Year, now.Month, 1);
            var delivered = await db.WorkspaceDeliveryUsage.AsNoTracking()
                .Where(u => u.WorkspaceId == workspaceId && u.Month == month)
                .Select(u => (long?)u.Bytes).SingleOrDefaultAsync(ct) ?? 0;
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new WorkspaceUsageDto(games, published, retained, delivered,
                entitlement?.Limits ?? new Dictionary<string, int?>(), entitlement?.PaidThrough));
        }).RequireAuthorization();
    }
}
