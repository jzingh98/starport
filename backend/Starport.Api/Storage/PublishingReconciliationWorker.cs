using Microsoft.EntityFrameworkCore;
using Starport.Application.Access;
using Starport.Infrastructure.Database;

namespace Starport.Api.Storage;

/// <summary>
/// Persists an unpublish after paid access is lost. Request-time checks remain the immediate
/// serving gate; this worker prevents an expired publication from reappearing on resubscription.
/// </summary>
public sealed class PublishingReconciliationWorker(
    IServiceScopeFactory scopes, TimeProvider clock, ILogger<PublishingReconciliationWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ReconcileAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogError(error, "Publishing reconciliation failed"); }
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StarportDbContext>();
        var entitlements = scope.ServiceProvider.GetRequiredService<IWorkspaceEntitlements>();
        var workspaces = await db.Games.AsNoTracking().Where(g => g.IsPublished)
            .Select(g => g.WorkspaceId).Distinct().ToListAsync(ct);
        foreach (var workspaceId in workspaces)
        {
            try
            {
                var entitlement = await entitlements.GetAsync(workspaceId, ct);
                if (entitlement?.IsActiveAt(clock.GetUtcNow()) == true) continue;
                var now = clock.GetUtcNow();
                var count = await db.Games.Where(g => g.WorkspaceId == workspaceId && g.IsPublished)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(g => g.IsPublished, false)
                        .SetProperty(g => g.UpdatedAt, now), ct);
                if (count > 0)
                    logger.LogInformation("Unpublished {Count} games after workspace {WorkspaceId} lost access",
                        count, workspaceId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                logger.LogWarning(error, "Could not reconcile workspace {WorkspaceId}", workspaceId);
            }
        }
    }
}
