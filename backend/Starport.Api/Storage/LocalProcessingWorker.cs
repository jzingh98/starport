using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Starport.Application.Access;
using Starport.Application.Publishing;
using Starport.Domain.Access;
using Starport.Infrastructure.Database;
using Starport.Infrastructure.Accounts;

namespace Starport.Api.Storage;

/// <summary>
/// Development-only processor runner. The child receives no application environment. Hosted
/// processing uses a separate restricted Job with one-upload object capabilities.
/// </summary>
public sealed class LocalProcessingWorker(
    IServiceScopeFactory scopes, StoragePaths paths, IConfiguration config,
    IHostEnvironment environment, TimeProvider clock, ILogger<LocalProcessingWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await ProcessOneAsync(stoppingToken)) continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogError(error, "Local processor loop failed"); }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    private async Task<bool> ProcessOneAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StarportDbContext>();
        var stale = clock.GetUtcNow().AddMinutes(-10);
        var upload = await db.GameUploads
            .Where(u => u.Status == GameUploadStatus.Pending ||
                u.Status == GameUploadStatus.Processing && u.StartedAt < stale)
            .OrderBy(u => u.CreatedAt).FirstOrDefaultAsync(ct);
        if (upload is null) return false;
        upload.Status = GameUploadStatus.Processing;
        upload.StartedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);

        try
        {
            var releaseId = upload.Id;
            var releaseDirectory = paths.Release(releaseId);
            if (!Directory.Exists(releaseDirectory))
            {
                var staging = paths.Staging(upload.Id);
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
                Directory.CreateDirectory(paths.Root);
                await RunProcessorAsync(paths.Upload(upload.Id), staging, ct);
                await ProcessedOutputVerifier.VerifyAsync(staging, upload.ArchiveSha256, ct);
                Directory.CreateDirectory(Path.GetDirectoryName(releaseDirectory)!);
                Directory.Move(staging, releaseDirectory);
            }
            var package = await ProcessedOutputVerifier.VerifyAsync(releaseDirectory, upload.ArchiveSha256, ct);
            var game = await db.Games.SingleOrDefaultAsync(g =>
                g.Id == upload.GameId && g.WorkspaceId == upload.WorkspaceId, ct)
                ?? throw new InvalidDataException("The upload's game is unavailable.");
            var entitlement = await scope.ServiceProvider.GetRequiredService<IWorkspaceEntitlements>()
                .GetAsync(game.WorkspaceId, ct);
            await RetainedStorageQuota.EnsureFitsAsync(db, game.WorkspaceId,
                package.TotalBytes, entitlement?.Limit("retained_bytes_mb"), ct);
            var membership = await scope.ServiceProvider.GetRequiredService<IWorkspaceMemberships>()
                .GetAsync(game.WorkspaceId, upload.CreatedBySubject, ct);
            var canActivate = GameAccess.CanPublish(game.WorkspaceId, upload.CreatedBySubject,
                membership, entitlement, clock.GetUtcNow()) == AccessDecision.Allowed;

            db.GameReleases.Add(new GameReleaseRecord
            {
                Id = releaseId, GameId = game.Id, Sha256 = package.ArchiveSha256,
                StorageKey = Path.GetRelativePath(paths.Root, releaseDirectory),
                IsValidated = true, CreatedAt = upload.CreatedAt, ValidatedAt = clock.GetUtcNow()
            });
            foreach (var file in package.Files)
                db.ReleaseFiles.Add(new ReleaseFileRecord
                {
                    ReleaseId = releaseId, Path = file.Path, Bytes = file.Bytes, Sha256 = file.Sha256
                });
            // Keep an already published release active until the publisher explicitly activates
            // the new one. An expired or removed publisher cannot activate a queued upload.
            if (canActivate && !game.IsPublished)
            {
                game.ActiveReleaseId = releaseId;
                game.UpdatedAt = clock.GetUtcNow();
            }
            upload.Status = GameUploadStatus.Ready;
            upload.ReleaseId = releaseId;
            upload.FinishedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Validated upload {UploadId} for game {GameId}", upload.Id, game.Id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (AccountsAccessUnavailableException error)
        {
            // A validated package remains retryable when the authority is temporarily down.
            logger.LogWarning(error, "Upload {UploadId} awaits an Accounts access check", upload.Id);
            db.ChangeTracker.Clear();
            var retry = await db.GameUploads.SingleAsync(u => u.Id == upload.Id, ct);
            retry.Status = GameUploadStatus.Pending;
            retry.StartedAt = null;
            await db.SaveChangesAsync(ct);
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
        catch (RetainedQuotaExceededException)
        {
            var releaseDirectory = paths.Release(upload.Id);
            if (Directory.Exists(releaseDirectory)) Directory.Delete(releaseDirectory, recursive: true);
            db.ChangeTracker.Clear();
            var failed = await db.GameUploads.SingleAsync(u => u.Id == upload.Id, ct);
            failed.Status = GameUploadStatus.Failed;
            failed.ErrorCode = "retained-quota-exceeded";
            failed.FinishedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "Upload {UploadId} failed validation or processing", upload.Id);
            db.ChangeTracker.Clear();
            var failed = await db.GameUploads.SingleAsync(u => u.Id == upload.Id, ct);
            failed.Status = GameUploadStatus.Failed;
            failed.ErrorCode = error is InvalidDataException ? "invalid-package" : "processing-failed";
            failed.FinishedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
        return true;
    }

    private async Task RunProcessorAsync(string archive, string staging, CancellationToken ct)
    {
        var configured = config["Processing:ProcessorPath"];
        var packaged = Path.Combine(environment.ContentRootPath, "Starport.Processor.dll");
        var processor = Path.GetFullPath(configured ?? (File.Exists(packaged) ? packaged :
            Path.Combine(environment.ContentRootPath, "..", "Starport.Processor", "bin", "Debug",
                "net10.0", "Starport.Processor.dll")));
        if (!File.Exists(processor))
            throw new FileNotFoundException("The local processor is not built.", processor);
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = paths.Root
        };
        start.ArgumentList.Add(processor);
        start.ArgumentList.Add(archive);
        start.ArgumentList.Add(staging);
        start.Environment.Clear();
        start.Environment["PATH"] = "/usr/local/bin:/usr/bin:/bin";
        using var process = Process.Start(start) ?? throw new IOException("The processor could not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new InvalidDataException("The processor timed out.");
        }
        if (process.ExitCode != 0)
            throw new InvalidDataException("The processor rejected the package.");
    }
}
