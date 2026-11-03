using Microsoft.EntityFrameworkCore;
using Starport.Infrastructure.Database;

namespace Starport.Api.Storage;

public sealed class RetainedQuotaExceededException : Exception;

public static class RetainedStorageQuota
{
    public static async Task EnsureFitsAsync(StarportDbContext db, string workspaceId,
        long additionalBytes, int? limitMb, CancellationToken ct)
    {
        if (limitMb is null) return;
        var limit = (long)limitMb.Value * 1024 * 1024;
        var retained = await (from file in db.ReleaseFiles.AsNoTracking()
                              join release in db.GameReleases.AsNoTracking()
                                  on file.ReleaseId equals release.Id
                              join game in db.Games.AsNoTracking()
                                  on release.GameId equals game.Id
                              where game.WorkspaceId == workspaceId
                              select (long?)file.Bytes).SumAsync(ct) ?? 0;
        if (additionalBytes < 0 || additionalBytes > limit - retained)
            throw new RetainedQuotaExceededException();
    }
}
