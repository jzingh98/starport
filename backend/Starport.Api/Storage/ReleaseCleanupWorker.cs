using Microsoft.EntityFrameworkCore;
using Starport.Infrastructure.Database;

namespace Starport.Api.Storage;

/// <summary>Finishes storage deletion after a release is invalidated in the database.</summary>
public sealed class ReleaseCleanupWorker(IServiceScopeFactory scopes, IConfiguration config,
    ILogger<ReleaseCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SweepAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogError(error, "Release cleanup failed"); }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StarportDbContext>();
        var releases = await db.GameReleases.AsNoTracking()
            .Where(r => !r.IsValidated && db.ReleaseFiles.Any(f => f.ReleaseId == r.Id))
            .OrderBy(r => r.CreatedAt).Take(10).Select(r => r.Id).ToListAsync(ct);
        foreach (var releaseId in releases)
        {
            try
            {
                var files = await db.ReleaseFiles.AsNoTracking()
                    .Where(f => f.ReleaseId == releaseId).Select(f => f.Path).ToListAsync(ct);
                if (config["Storage:Mode"] == "gcs")
                {
                    var storage = scope.ServiceProvider.GetRequiredService<GcsGameStorage>();
                    foreach (var file in files)
                        await storage.DeleteAsync(storage.ReleaseBucket,
                            GcsGameStorage.ReleaseKey(releaseId, file), ct);
                }
                else if (config["Storage:Mode"] == "local")
                {
                    var path = scope.ServiceProvider.GetRequiredService<StoragePaths>()
                        .Release(releaseId);
                    if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                }
                else return;
                await db.ReleaseFiles.Where(f => f.ReleaseId == releaseId).ExecuteDeleteAsync(ct);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogWarning(error, "Release {ReleaseId} awaits cleanup retry", releaseId);
            }
        }
    }
}
