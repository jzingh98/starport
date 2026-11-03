using Starport.Domain.Access;

namespace Starport.Application.Access;

/// <summary>Filters a bounded candidate page against current publisher availability in batches.</summary>
public sealed class GameCatalogService(
    IGameRepository games, IWorkspaceEntitlements entitlements, TimeProvider clock,
    IWorkspaceDeliveryUsage? deliveryUsage = null)
{
    public async Task<IReadOnlyList<GameDetails>> ListAsync(int page = 0,
        CancellationToken ct = default)
    {
        if (page is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(page));
        const int pageSize = 100;
        const int resultSize = 24;
        var result = new List<GameDetails>(resultSize);
        var eligibleSeen = 0;
        for (var skip = 0; result.Count < resultSize; skip += pageSize)
        {
            var candidates = await games.ListPublicCandidatesAsync(skip, pageSize, ct);
            if (candidates.Count == 0) break;
            var states = await entitlements.GetManyAsync(
                candidates.Select(g => g.Access.WorkspaceId).Distinct(StringComparer.Ordinal).ToArray(), ct);
            var now = clock.GetUtcNow().UtcDateTime;
            var usage = deliveryUsage is null ? null : await deliveryUsage.GetManyAsync(
                states.Keys.ToArray(), new DateOnly(now.Year, now.Month, 1), ct);
            foreach (var game in candidates)
            {
                states.TryGetValue(game.Access.WorkspaceId, out var state);
                if (GameAccess.CanPlay(game.Access, state, new PlayerContext(null, null, null),
                    clock.GetUtcNow()) == AccessDecision.Allowed &&
                    (state?.Limit("monthly_delivery_mb") is not { } limit ||
                     usage is null ||
                     usage.GetValueOrDefault(game.Access.WorkspaceId) < (long)limit * 1024 * 1024))
                {
                    if (eligibleSeen++ >= page * resultSize)
                        result.Add(game);
                }
                if (result.Count == resultSize) break;
            }
            if (candidates.Count < pageSize) break;
        }
        return result;
    }
}
