using Microsoft.EntityFrameworkCore;
using Starport.Application.Access;
using Starport.Domain.Access;
using Starport.Infrastructure.Database;

namespace Starport.Tests.Access;

public sealed class PostgresAccessTests
{
    [Fact]
    public async Task Exact_review_and_paid_expiry_are_enforced_by_the_Postgres_projection()
    {
        var connection = Environment.GetEnvironmentVariable("STARPORT_TEST_DATABASE");
        if (string.IsNullOrWhiteSpace(connection))
            return;

        var now = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        var workspaceId = "ws_" + Guid.NewGuid().ToString("N");
        var gameId = Guid.NewGuid();
        var releaseId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<StarportDbContext>().UseNpgsql(connection).Options;
        await using var db = new StarportDbContext(options);
        await db.Database.MigrateAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Workspaces.Add(new WorkspaceRecord
        {
            Id = workspaceId, Slug = workspaceId, Name = "Test creator", Kind = WorkspaceKind.Personal,
            CreatedAt = now
        });
        var publishing = new WorkspacePublishingRecord
        {
            WorkspaceId = workspaceId, CanPublish = true, PaidThrough = now.AddHours(1), LimitsJson = "{}"
        };
        db.WorkspacePublishing.Add(publishing);
        var game = new GameRecord
        {
            Id = gameId, WorkspaceId = workspaceId, Slug = "test-" + gameId.ToString("N"),
            Title = "Postgres test", Description = "Test", Audience = GameAudience.Public,
            IsPublished = true, ActiveReleaseId = releaseId, MetadataRevision = 1,
            CreatedAt = now, UpdatedAt = now
        };
        db.Games.Add(game);
        var release = new GameReleaseRecord
        {
            Id = releaseId, GameId = gameId, Sha256 = new string('a', 64), StorageKey = "test/release",
            IsValidated = true, CreatedAt = now, ValidatedAt = now
        };
        db.GameReleases.Add(release);
        db.GameReviewApprovals.Add(new GameReviewApprovalRecord
        {
            Id = Guid.NewGuid(), GameId = gameId, ReleaseId = releaseId,
            ReleaseSha256 = release.Sha256, MetadataRevision = 1,
            ReviewerSubject = "moderator", ApprovedAt = now
        });
        await db.SaveChangesAsync();

        var service = new GameReadService(new GameRepository(db), new PlayGrantRepository(db),
            new LocalWorkspaceEntitlements(db), new LocalWorkspaceMemberships(db), new FixedClock(now));
        Assert.Equal(AccessDecision.Allowed, (await service.ReadAsync(gameId, null)).Decision);

        release.Sha256 = new string('b', 64);
        await db.SaveChangesAsync();
        Assert.Equal(AccessDecision.NotFound, (await service.ReadAsync(gameId, null)).Decision);

        release.Sha256 = new string('a', 64);
        game.MetadataRevision = 2;
        await db.SaveChangesAsync();
        Assert.Equal(AccessDecision.NotFound, (await service.ReadAsync(gameId, null)).Decision);

        game.MetadataRevision = 1;
        publishing.PaidThrough = now;
        await db.SaveChangesAsync();
        Assert.Equal(AccessDecision.PublisherOffline, (await service.ReadAsync(gameId, null)).Decision);
        await transaction.RollbackAsync();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
