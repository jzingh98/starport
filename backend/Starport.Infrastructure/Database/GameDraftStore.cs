using Microsoft.EntityFrameworkCore;
using System.Data;
using Starport.Application.Publishing;

namespace Starport.Infrastructure.Database;

public sealed class GameDraftStore(StarportDbContext db) : IGameDraftStore
{
    public Task<int> CountAsync(string workspaceId, CancellationToken cancellationToken = default) =>
        db.Games.CountAsync(g => g.WorkspaceId == workspaceId, cancellationToken);

    public async Task<GameDraft> CreateAsync(GameDraft draft, CancellationToken cancellationToken = default)
    {
        db.Games.Add(new GameRecord
        {
            Id = draft.Id,
            WorkspaceId = draft.WorkspaceId,
            Slug = draft.Slug,
            Title = draft.Title,
            Description = draft.Description,
            Audience = draft.Audience,
            IsPublished = false,
            CreatedAt = draft.CreatedAt,
            UpdatedAt = draft.CreatedAt
        });
        await db.SaveChangesAsync(cancellationToken);
        return draft;
    }

    public async Task<GameDraft?> CreateWithinLimitAsync(GameDraft draft, int? limit,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        // Serializes count-and-create for this workspace without PostgreSQL serialization
        // failures surfacing as HTTP 500 under concurrent draft requests.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT 1 FROM "Workspaces" WHERE "Id" = {draft.WorkspaceId} FOR NO KEY UPDATE
            """, cancellationToken);
        if (limit is { } maximum &&
            await CountAsync(draft.WorkspaceId, cancellationToken) >= maximum)
            return null;
        var created = await CreateAsync(draft, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return created;
    }

    public async Task<IReadOnlyList<GameDraft>> ListAsync(string workspaceId, CancellationToken cancellationToken = default) =>
        await db.Games.AsNoTracking()
            .Where(g => g.WorkspaceId == workspaceId)
            .OrderByDescending(g => g.UpdatedAt)
            .Select(g => new GameDraft(g.Id, g.WorkspaceId, g.Slug, g.Title, g.Description, g.Audience,
                g.CreatedAt, g.IsPublished, g.ActiveReleaseId != null && db.GameReleases.Any(r =>
                    r.Id == g.ActiveReleaseId && r.GameId == g.Id && r.IsValidated)))
            .ToListAsync(cancellationToken);
}
