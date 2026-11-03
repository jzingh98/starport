using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Starport.Api.Auth;
using Starport.Api.Storage;
using Starport.Application.Access;
using Starport.Application.Publishing;
using Starport.Domain.Access;
using Starport.Infrastructure.Database;

namespace Starport.Api.Endpoints;

public sealed record UploadDto(Guid Id, Guid GameId, GameUploadStatus Status, long ArchiveBytes,
    Guid? ReleaseId, string? ErrorCode);

public static class UploadEndpoints
{
    public static void MapUploadEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/workspaces/{workspaceId}/games/{gameId:guid}/uploads")
            .RequireAuthorization();
        group.MapPost("", async (string workspaceId, Guid gameId, HttpContext http,
            StarportDbContext db, IWorkspaceMemberships memberships, IWorkspaceEntitlements entitlements,
            TimeProvider clock, IConfiguration config, IServiceProvider services, CancellationToken ct) =>
        {
            var local = config["Storage:Mode"] == "local" &&
                config["Processing:Mode"] == "local" && app.Environment.IsDevelopment();
            var hosted = config["Storage:Mode"] == "gcs" &&
                config["Processing:Mode"] == "job";
            if (!local && !hosted)
                return ApiProblems.Problem(503, "uploads-unavailable", "Uploads are unavailable");
            var paths = local ? services.GetRequiredService<StoragePaths>() : null;
            var game = await db.Games.AsNoTracking().SingleOrDefaultAsync(g => g.Id == gameId, ct);
            if (game is null || game.WorkspaceId != workspaceId)
                return ApiProblems.Problem(404, "not-found", "Not found");
            var subject = http.User.Subject()!;
            var membership = await memberships.GetAsync(game.WorkspaceId, subject, ct);
            var entitlement = await entitlements.GetAsync(game.WorkspaceId, ct);
            var decision = GameAccess.CanPublish(game.WorkspaceId, subject, membership, entitlement,
                clock.GetUtcNow());
            if (decision != AccessDecision.Allowed)
                return decision == AccessDecision.NotFound
                    ? ApiProblems.Problem(404, "not-found", "Not found")
                    : decision == AccessDecision.Forbidden
                        ? ApiProblems.Problem(403, "publisher-role-required", "Publishing role required")
                        : ApiProblems.Problem(402, "publishing-plan-required", "Publishing plan required");

            var max = Math.Min(GamePackageInspector.MaxArchiveBytes,
                (long)(entitlement?.Limit("upload_bytes_mb") ?? 256) * 1024 * 1024);
            if (http.Request.ContentLength is > 0 && http.Request.ContentLength > max)
                return ApiProblems.Problem(413, "upload-too-large", "Game package is too large");
            if (!string.Equals(http.Request.ContentType, "application/zip", StringComparison.OrdinalIgnoreCase))
                return ApiProblems.Problem(415, "zip-required", "Upload a ZIP package");

            var id = Guid.NewGuid();
            var path = local ? paths!.Upload(id) : Path.GetTempFileName();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            long bytes = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            try
            {
                await using (var output = new FileStream(path, local ? FileMode.CreateNew : FileMode.Create,
                    FileAccess.Write,
                    FileShare.None, 64 * 1024, FileOptions.Asynchronous))
                {
                    var buffer = new byte[64 * 1024];
                    while (true)
                    {
                        var count = await http.Request.Body.ReadAsync(buffer, ct);
                        if (count == 0) break;
                        bytes += count;
                        if (bytes > max)
                            throw new UploadTooLargeException();
                        hash.AppendData(buffer, 0, count);
                        await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    }
                }
                if (bytes == 0)
                {
                    File.Delete(path);
                    return ApiProblems.Problem(400, "empty-upload", "The package was empty");
                }
                if (hosted)
                {
                    await using var source = File.OpenRead(path);
                    var storage = services.GetRequiredService<GcsGameStorage>();
                    await storage.UploadAsync(storage.UploadBucket, GcsGameStorage.SourceKey(id),
                        source, "application/zip", ct);
                    File.Delete(path);
                }
                db.GameUploads.Add(new GameUploadRecord
                {
                    Id = id, GameId = gameId, WorkspaceId = game.WorkspaceId,
                    CreatedBySubject = subject, Status = GameUploadStatus.Pending,
                    ArchiveBytes = bytes, ArchiveSha256 = Convert.ToHexStringLower(hash.GetHashAndReset()),
                    ArchivePath = local ? Path.GetRelativePath(paths!.Root, path) :
                        GcsGameStorage.SourceKey(id), CreatedAt = clock.GetUtcNow()
                });
                await db.SaveChangesAsync(ct);
            }
            catch (UploadTooLargeException)
            {
                File.Delete(path);
                return ApiProblems.Problem(413, "upload-too-large", "Game package is too large");
            }
            catch
            {
                File.Delete(path);
                throw;
            }
            return Results.Accepted($"/api/workspaces/{workspaceId}/games/{gameId}/uploads/{id}",
                new UploadDto(id, gameId, GameUploadStatus.Pending, bytes, null, null));
        }).WithMetadata(new RequestSizeLimitAttribute(GamePackageInspector.MaxArchiveBytes + 1))
            .RequireRateLimiting("upload");

        group.MapGet("/{uploadId:guid}", async (string workspaceId, Guid gameId, Guid uploadId,
            HttpContext http, StarportDbContext db, IWorkspaceMemberships memberships,
            CancellationToken ct) =>
        {
            var game = await db.Games.AsNoTracking().SingleOrDefaultAsync(g => g.Id == gameId, ct);
            if (game is null || game.WorkspaceId != workspaceId ||
                await memberships.GetAsync(game.WorkspaceId, http.User.Subject()!, ct) is not
                    { Role: not WorkspaceRole.Viewer })
                return ApiProblems.Problem(404, "not-found", "Not found");
            var upload = await db.GameUploads.AsNoTracking().SingleOrDefaultAsync(u =>
                u.Id == uploadId && u.GameId == gameId && u.WorkspaceId == workspaceId, ct);
            return upload is null ? ApiProblems.Problem(404, "not-found", "Not found") :
                Results.Ok(new UploadDto(upload.Id, gameId, upload.Status, upload.ArchiveBytes,
                    upload.ReleaseId, upload.ErrorCode));
        });
    }

    private sealed class UploadTooLargeException : Exception;
}
