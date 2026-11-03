using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Starport.Application.Access;
using Starport.Application.Publishing;
using Starport.Domain.Access;
using Starport.Infrastructure.Accounts;
using Starport.Infrastructure.Database;

namespace Starport.Api.Storage;

/// <summary>Dispatches credentialless processor Jobs, verifies their output and commits releases.</summary>
public sealed class HostedProcessingWorker(
    IServiceScopeFactory scopes, ProcessorJobs jobs, GcsGameStorage storage,
    TimeProvider clock, ILogger<HostedProcessingWorker> logger) : BackgroundService
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();
    private int processingOffset;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<StarportDbContext>();
                var pending = await db.GameUploads.AsNoTracking()
                    .Where(u => u.Status == GameUploadStatus.Pending)
                    .OrderBy(u => u.CreatedAt).Take(25).Select(u => u.Id)
                    .ToListAsync(stoppingToken);
                var processing = await db.GameUploads.AsNoTracking()
                    .Where(u => u.Status == GameUploadStatus.Processing)
                    .OrderBy(u => u.CreatedAt).Skip(processingOffset).Take(25)
                    .Select(u => u.Id).ToListAsync(stoppingToken);
                if (processing.Count == 0 && processingOffset > 0)
                {
                    processingOffset = 0;
                    processing = await db.GameUploads.AsNoTracking()
                        .Where(u => u.Status == GameUploadStatus.Processing)
                        .OrderBy(u => u.CreatedAt).Take(25).Select(u => u.Id)
                        .ToListAsync(stoppingToken);
                }
                processingOffset += processing.Count;
                foreach (var id in pending.Concat(processing).Distinct())
                {
                    try { await ProcessOneAsync(id, stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                    catch (AccountsAccessUnavailableException error)
                    { logger.LogWarning(error, "Processing awaits Accounts access checks"); }
                    catch (Exception error)
                    { logger.LogError(error, "Hosted processing failed for upload {UploadId}", id); }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogError(error, "Hosted processing loop failed"); }
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
    }

    private async Task ProcessOneAsync(Guid uploadId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StarportDbContext>();
        var upload = await db.GameUploads.SingleOrDefaultAsync(u => u.Id == uploadId &&
            (u.Status == GameUploadStatus.Pending || u.Status == GameUploadStatus.Processing), ct);
        if (upload is null) return;
        if (upload.Status == GameUploadStatus.Pending)
        {
            upload.Status = GameUploadStatus.Processing;
            upload.StartedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }

        var state = await jobs.GetAsync(upload.Id, ct);
        if (state == ProcessorJobState.Missing)
        {
            var source = await storage.SignReadAsync(storage.UploadBucket,
                GcsGameStorage.SourceKey(upload.Id), TimeSpan.FromMinutes(30), ct);
            var output = await storage.SignWriteAsync(storage.UploadBucket,
                GcsGameStorage.OutputKey(upload.Id), TimeSpan.FromMinutes(30), ct);
            await jobs.CreateAsync(upload.Id, source, output, ct);
            return;
        }
        if (state == ProcessorJobState.Running) return;
        if (state == ProcessorJobState.Failed)
        {
            upload.Status = GameUploadStatus.Failed;
            upload.ErrorCode = "processing-failed";
            upload.FinishedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return;
        }

        var scratch = Path.Combine(Path.GetTempPath(), "starport-" + upload.Id.ToString("N"));
        var tarPath = scratch + ".tar";
        try
        {
            Directory.CreateDirectory(scratch);
            await using (var output = new BoundedFileWriteStream(
                new FileStream(tarPath, FileMode.Create, FileAccess.Write,
                    FileShare.None, 64 * 1024, FileOptions.Asynchronous),
                600L * 1024 * 1024))
                await storage.DownloadAsync(storage.UploadBucket,
                    GcsGameStorage.OutputKey(upload.Id), output, ct);
            await HostedArtifactExtractor.ExtractAsync(tarPath, scratch, ct);
            var package = await ProcessedOutputVerifier.VerifyAsync(scratch,
                upload.ArchiveSha256, ct);
            var game = await db.Games.SingleOrDefaultAsync(g => g.Id == upload.GameId &&
                g.WorkspaceId == upload.WorkspaceId, ct)
                ?? throw new InvalidDataException("The upload's game is unavailable.");
            var entitlements = scope.ServiceProvider.GetRequiredService<IWorkspaceEntitlements>();
            var memberships = scope.ServiceProvider.GetRequiredService<IWorkspaceMemberships>();
            var entitlement = await entitlements.GetAsync(game.WorkspaceId, ct);
            await RetainedStorageQuota.EnsureFitsAsync(db, game.WorkspaceId,
                package.TotalBytes, entitlement?.Limit("retained_bytes_mb"), ct);
            var membership = await memberships.GetAsync(game.WorkspaceId, upload.CreatedBySubject, ct);
            var canActivate = GameAccess.CanPublish(game.WorkspaceId, upload.CreatedBySubject,
                membership, entitlement, clock.GetUtcNow()) == AccessDecision.Allowed;
            await Parallel.ForEachAsync(package.Files,
                new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct },
                async (file, token) =>
                {
                    var path = Path.Combine(scratch, file.Path.Replace('/', Path.DirectorySeparatorChar));
                    await using var input = File.OpenRead(path);
                    if (!ContentTypes.TryGetContentType(file.Path, out var contentType))
                        contentType = "application/octet-stream";
                    await storage.UploadAsync(storage.ReleaseBucket,
                        GcsGameStorage.ReleaseKey(upload.Id, file.Path), input, contentType, token);
                });
            db.GameReleases.Add(new GameReleaseRecord
            {
                Id = upload.Id, GameId = game.Id, Sha256 = package.ArchiveSha256,
                StorageKey = $"releases/{upload.Id:N}", IsValidated = true,
                CreatedAt = upload.CreatedAt, ValidatedAt = clock.GetUtcNow()
            });
            foreach (var file in package.Files)
                db.ReleaseFiles.Add(new ReleaseFileRecord
                {
                    ReleaseId = upload.Id, Path = file.Path,
                    Bytes = file.Bytes, Sha256 = file.Sha256
                });
            if (canActivate && !game.IsPublished)
            {
                game.ActiveReleaseId = upload.Id;
                game.UpdatedAt = clock.GetUtcNow();
            }
            upload.Status = GameUploadStatus.Ready;
            upload.ReleaseId = upload.Id;
            upload.FinishedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Validated hosted upload {UploadId} for game {GameId}", upload.Id, game.Id);
        }
        catch (RetainedQuotaExceededException)
        {
            db.ChangeTracker.Clear();
            var failed = await db.GameUploads.SingleAsync(u => u.Id == upload.Id, ct);
            failed.Status = GameUploadStatus.Failed;
            failed.ErrorCode = "retained-quota-exceeded";
            failed.FinishedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
        catch (InvalidDataException error)
        {
            logger.LogWarning(error, "Hosted upload {UploadId} failed validation", upload.Id);
            db.ChangeTracker.Clear();
            var failed = await db.GameUploads.SingleAsync(u => u.Id == upload.Id, ct);
            failed.Status = GameUploadStatus.Failed;
            failed.ErrorCode = "invalid-package";
            failed.FinishedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            if (File.Exists(tarPath)) File.Delete(tarPath);
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }
    }

}
