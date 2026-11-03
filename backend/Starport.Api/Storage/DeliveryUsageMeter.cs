using Microsoft.EntityFrameworkCore;
using Starport.Infrastructure.Database;

namespace Starport.Api.Storage;

/// <summary>Atomically reserves bytes in the owning workspace's UTC calendar month.</summary>
public sealed class DeliveryUsageMeter(TimeProvider clock)
{
    public async Task<bool> HasRemainingAsync(StarportDbContext db, string workspaceId,
        int? limitMb, CancellationToken ct)
    {
        if (limitMb is null) return true;
        if (limitMb <= 0) return false;
        var now = clock.GetUtcNow().UtcDateTime;
        var month = new DateOnly(now.Year, now.Month, 1);
        var used = await db.WorkspaceDeliveryUsage.AsNoTracking().SingleOrDefaultAsync(u =>
            u.WorkspaceId == workspaceId && u.Month == month, ct);
        return used is null || used.Bytes < (long)limitMb.Value * 1024 * 1024;
    }

    public async Task<bool> TryReserveAsync(StarportDbContext db, string workspaceId,
        long bytes, int? limitMb, CancellationToken ct)
    {
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        var limit = limitMb is null ? long.MaxValue : (long)limitMb.Value * 1024 * 1024;
        if (bytes > limit) return false;
        if (bytes == 0) return true;
        var now = clock.GetUtcNow();
        var utc = now.UtcDateTime;
        var month = new DateOnly(utc.Year, utc.Month, 1);
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "WorkspaceDeliveryUsage" ("WorkspaceId", "Month", "Bytes", "UpdatedAt")
            VALUES ({workspaceId}, {month}, {bytes}, {now})
            ON CONFLICT ("WorkspaceId", "Month") DO UPDATE
            SET "Bytes" = "WorkspaceDeliveryUsage"."Bytes" + EXCLUDED."Bytes",
                "UpdatedAt" = EXCLUDED."UpdatedAt"
            WHERE "WorkspaceDeliveryUsage"."Bytes" <= {limit} - EXCLUDED."Bytes"
            """, ct);
        return changed == 1;
    }
}
