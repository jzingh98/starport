using Starport.Domain.Access;

namespace Starport.Application.Access;

/// <summary>Current workspace serving capability. An outage must throw, not return a cached grant.</summary>
public interface IWorkspaceEntitlements
{
    Task<PublishingEntitlement?> GetAsync(string workspaceId, CancellationToken cancellationToken = default);

    async Task<IReadOnlyDictionary<string, PublishingEntitlement?>> GetManyAsync(
        IReadOnlyCollection<string> workspaceIds, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, PublishingEntitlement?>(StringComparer.Ordinal);
        foreach (var id in workspaceIds.Distinct(StringComparer.Ordinal))
            result[id] = await GetAsync(id, cancellationToken);
        return result;
    }
}

/// <summary>Current membership in the game's owning workspace.</summary>
public interface IWorkspaceMemberships
{
    Task<WorkspaceMembership?> GetAsync(string workspaceId, string subject, CancellationToken cancellationToken = default);
}

public sealed record GameDetails(GameSnapshot Access, string Slug, string Title, string Description);

public interface IGameRepository
{
    Task<GameDetails?> GetAsync(Guid gameId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GameDetails>> ListPublicCandidatesAsync(
        int skip, int take, CancellationToken cancellationToken = default);
}

public interface IPlayGrantRepository
{
    Task<PersonPlayGrant?> GetAsync(Guid gameId, string subject, CancellationToken cancellationToken = default);
}

public interface IWorkspaceDeliveryUsage
{
    Task<IReadOnlyDictionary<string, long>> GetManyAsync(IReadOnlyCollection<string> workspaceIds,
        DateOnly month, CancellationToken cancellationToken = default);
}

public sealed record GameReadResult(GameDetails? Game, AccessDecision Decision,
    PublishingEntitlement? Entitlement = null);

/// <summary>Builds a play decision using current state at each request boundary.</summary>
public sealed class GameReadService(
    IGameRepository games,
    IPlayGrantRepository grants,
    IWorkspaceEntitlements entitlements,
    IWorkspaceMemberships memberships,
    TimeProvider clock,
    IWorkspaceDeliveryUsage? deliveryUsage = null)
{
    public async Task<GameReadResult> ReadAsync(
        Guid gameId, string? subject, bool preview = false, CancellationToken cancellationToken = default)
    {
        var game = await games.GetAsync(gameId, cancellationToken);
        if (game is null)
            return new(null, AccessDecision.NotFound);

        var entitlement = await entitlements.GetAsync(game.Access.WorkspaceId, cancellationToken);
        WorkspaceMembership? membership = null;
        PersonPlayGrant? grant = null;
        if (subject is not null)
        {
            membership = await memberships.GetAsync(game.Access.WorkspaceId, subject, cancellationToken);
            if (!preview && game.Access.Audience == GameAudience.People)
                grant = await grants.GetAsync(gameId, subject, cancellationToken);
        }

        var player = new PlayerContext(subject, membership, grant);
        var decision = GameAccess.CanPlay(game.Access, entitlement, player, clock.GetUtcNow(), preview);
        if (decision == AccessDecision.Allowed && deliveryUsage is not null &&
            entitlement?.Limit("monthly_delivery_mb") is { } limit)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var month = new DateOnly(now.Year, now.Month, 1);
            var usage = await deliveryUsage.GetManyAsync([game.Access.WorkspaceId], month,
                cancellationToken);
            if (usage.GetValueOrDefault(game.Access.WorkspaceId) >= (long)limit * 1024 * 1024)
                decision = AccessDecision.DeliveryQuotaExceeded;
        }
        return new(decision == AccessDecision.Allowed ? game : null, decision, entitlement);
    }
}
