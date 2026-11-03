using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Starport.Application.Access;
using Starport.Domain.Access;

namespace Starport.Infrastructure.Database;

public sealed class GameRepository(StarportDbContext db) : IGameRepository
{
    public async Task<GameDetails?> GetAsync(Guid gameId, CancellationToken cancellationToken = default)
    {
        var row = await Project(db.Games.AsNoTracking().Where(g => g.Id == gameId))
            .SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : ToDetails(row);
    }

    public async Task<IReadOnlyList<GameDetails>> ListPublicCandidatesAsync(
        int skip, int take, CancellationToken cancellationToken = default)
    {
        var rows = await Project(db.Games.AsNoTracking()
            .Where(g => g.IsPublished && g.Audience == GameAudience.Public)
            .OrderByDescending(g => g.UpdatedAt).ThenByDescending(g => g.Id)
            .Skip(skip).Take(take)).ToListAsync(cancellationToken);
        return rows.Select(ToDetails).ToList();
    }

    private IQueryable<GameRow> Project(IQueryable<GameRecord> games) => games.Select(g => new GameRow(
        g.Id, g.WorkspaceId, g.Slug, g.Title, g.Description, g.Audience, g.IsPublished,
        g.IsModerationSuspended,
        g.ActiveReleaseId != null && db.GameReleases.Any(r =>
            r.Id == g.ActiveReleaseId && r.GameId == g.Id && r.IsValidated),
        g.ActiveReleaseId != null && db.GameReviewApprovals.Any(a =>
            a.GameId == g.Id && a.ReleaseId == g.ActiveReleaseId &&
            a.MetadataRevision == g.MetadataRevision && db.GameReleases.Any(r =>
                r.Id == a.ReleaseId && r.GameId == g.Id && r.IsValidated &&
                r.Sha256 == a.ReleaseSha256))));

    private static GameDetails ToDetails(GameRow row) => new(
        new GameSnapshot(row.Id, row.WorkspaceId, row.Audience, row.IsPublished,
            row.HasValidatedRelease, row.PublicReviewApproved, row.IsModerationSuspended),
        row.Slug, row.Title, row.Description);

    private sealed record GameRow(Guid Id, string WorkspaceId, string Slug, string Title,
        string Description, GameAudience Audience, bool IsPublished, bool IsModerationSuspended,
        bool HasValidatedRelease, bool PublicReviewApproved);
}

public sealed class PlayGrantRepository(StarportDbContext db) : IPlayGrantRepository
{
    public async Task<PersonPlayGrant?> GetAsync(Guid gameId, string subject, CancellationToken cancellationToken = default)
    {
        var row = await db.GamePlayGrants.AsNoTracking()
            .SingleOrDefaultAsync(g => g.GameId == gameId && g.Subject == subject, cancellationToken);
        return row is null ? null : new PersonPlayGrant(row.GameId, row.Subject, row.ExpiresAt);
    }
}

public sealed class LocalWorkspaceEntitlements(StarportDbContext db) : IWorkspaceEntitlements
{
    public async Task<PublishingEntitlement?> GetAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        var row = await db.WorkspacePublishing.AsNoTracking()
            .SingleOrDefaultAsync(e => e.WorkspaceId == workspaceId, cancellationToken);
        return row is null ? null : new PublishingEntitlement(row.WorkspaceId, row.CanPublish, row.PaidThrough,
            row.Suspended, JsonSerializer.Deserialize<Dictionary<string, int?>>(row.LimitsJson));
    }

    public async Task<IReadOnlyDictionary<string, PublishingEntitlement?>> GetManyAsync(
        IReadOnlyCollection<string> workspaceIds, CancellationToken cancellationToken = default)
    {
        var ids = workspaceIds.Distinct(StringComparer.Ordinal).ToArray();
        var rows = await db.WorkspacePublishing.AsNoTracking()
            .Where(e => ids.Contains(e.WorkspaceId)).ToListAsync(cancellationToken);
        var result = ids.ToDictionary(id => id, _ => (PublishingEntitlement?)null, StringComparer.Ordinal);
        foreach (var row in rows)
            result[row.WorkspaceId] = new PublishingEntitlement(row.WorkspaceId, row.CanPublish,
                row.PaidThrough, row.Suspended,
                JsonSerializer.Deserialize<Dictionary<string, int?>>(row.LimitsJson));
        return result;
    }
}

public sealed class LocalWorkspaceMemberships(StarportDbContext db) : IWorkspaceMemberships
{
    public async Task<WorkspaceMembership?> GetAsync(
        string workspaceId, string subject, CancellationToken cancellationToken = default)
    {
        var row = await db.WorkspaceMembers.AsNoTracking()
            .SingleOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.Subject == subject, cancellationToken);
        return row is null ? null : new WorkspaceMembership(row.WorkspaceId, row.Subject, row.Role);
    }
}

public sealed class WorkspaceDeliveryUsageRepository(StarportDbContext db) : IWorkspaceDeliveryUsage
{
    public async Task<IReadOnlyDictionary<string, long>> GetManyAsync(
        IReadOnlyCollection<string> workspaceIds, DateOnly month,
        CancellationToken cancellationToken = default)
    {
        var ids = workspaceIds.Distinct(StringComparer.Ordinal).ToArray();
        var rows = await db.WorkspaceDeliveryUsage.AsNoTracking()
            .Where(u => ids.Contains(u.WorkspaceId) && u.Month == month)
            .ToListAsync(cancellationToken);
        var result = ids.ToDictionary(id => id, _ => 0L, StringComparer.Ordinal);
        foreach (var row in rows) result[row.WorkspaceId] = row.Bytes;
        return result;
    }
}
