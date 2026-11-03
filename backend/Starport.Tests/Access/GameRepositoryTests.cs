using Microsoft.EntityFrameworkCore;
using Starport.Domain.Access;
using Starport.Infrastructure.Database;

namespace Starport.Tests.Access;

public sealed class GameRepositoryTests
{
    [Fact]
    public async Task Public_approval_is_bound_to_the_active_release_digest_and_metadata_revision()
    {
        const string workspaceId = "ws_creator";
        var gameId = Guid.NewGuid();
        var releaseId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<StarportDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new StarportDbContext(options);
        var now = DateTimeOffset.UtcNow;
        db.Workspaces.Add(new WorkspaceRecord
        {
            Id = workspaceId, Slug = "creator", Name = "Creator", Kind = WorkspaceKind.Personal, CreatedAt = now
        });
        var game = new GameRecord
        {
            Id = gameId, WorkspaceId = workspaceId, Slug = "adventure", Title = "Adventure",
            Description = "Explore", Audience = GameAudience.Public, IsPublished = true,
            ActiveReleaseId = releaseId, MetadataRevision = 1, CreatedAt = now, UpdatedAt = now
        };
        var release = new GameReleaseRecord
        {
            Id = releaseId, GameId = gameId, Sha256 = new string('a', 64), StorageKey = "release/1",
            IsValidated = true, CreatedAt = now, ValidatedAt = now
        };
        db.Games.Add(game);
        db.GameReleases.Add(release);
        db.GameReviewApprovals.Add(new GameReviewApprovalRecord
        {
            Id = Guid.NewGuid(), GameId = gameId, ReleaseId = releaseId,
            ReleaseSha256 = release.Sha256, MetadataRevision = 1,
            ReviewerSubject = "moderator", ApprovedAt = now
        });
        await db.SaveChangesAsync();

        var repository = new GameRepository(db);
        Assert.True((await repository.GetAsync(gameId))?.Access.PublicReviewApproved);

        release.Sha256 = new string('b', 64);
        await db.SaveChangesAsync();
        Assert.False((await repository.GetAsync(gameId))?.Access.PublicReviewApproved);

        release.Sha256 = new string('a', 64);
        game.MetadataRevision = 2;
        await db.SaveChangesAsync();
        Assert.False((await repository.GetAsync(gameId))?.Access.PublicReviewApproved);
    }
}
