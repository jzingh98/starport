using Microsoft.EntityFrameworkCore;
using Starport.Infrastructure.Database;

namespace Starport.Api.Storage;

public sealed class LaunchCleanupWorker(IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<LaunchCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<StarportDbContext>();
                var now = clock.GetUtcNow();
                await db.GameLaunches.Where(l => l.SessionExpiresAt != null &&
                    l.SessionExpiresAt < now || l.SessionExpiresAt == null &&
                    l.TicketExpiresAt < now).ExecuteDeleteAsync(stoppingToken);
                await db.GameSaveMutations.Where(m => m.CreatedAt < now.AddDays(-7))
                    .ExecuteDeleteAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogError(error, "Launch cleanup failed"); }
            await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
        }
    }
}
