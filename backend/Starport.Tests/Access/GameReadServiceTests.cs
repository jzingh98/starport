using Starport.Application.Access;
using Starport.Domain.Access;

namespace Starport.Tests.Access;

public sealed class GameReadServiceTests
{
    private const string WorkspaceId = "ws_creator";
    private static readonly Guid GameId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Free_recipient_can_read_a_person_shared_game_but_unknown_player_cannot()
    {
        var fixture = new Fixture(GameAudience.People);
        fixture.Grants.Grant = new(GameId, "free-player");
        var allowed = await fixture.Service.ReadAsync(GameId, "free-player");
        var denied = await fixture.Service.ReadAsync(GameId, "stranger");

        Assert.Equal(AccessDecision.Allowed, allowed.Decision);
        Assert.Equal("An adventure", allowed.Game?.Title);
        Assert.Equal(AccessDecision.NotFound, denied.Decision);
        Assert.Null(denied.Game);
        Assert.Equal(2, fixture.Grants.Reads);
    }

    [Fact]
    public async Task Public_game_rechecks_publisher_expiry_without_a_player_identity()
    {
        var fixture = new Fixture(GameAudience.Public, approved: true);
        Assert.Equal(AccessDecision.Allowed, (await fixture.Service.ReadAsync(GameId, null)).Decision);
        fixture.Entitlements.Value = new(WorkspaceId, true, Now);
        var offline = await fixture.Service.ReadAsync(GameId, null);
        Assert.Equal(AccessDecision.PublisherOffline, offline.Decision);
        Assert.Null(offline.Game);
        Assert.Equal(2, fixture.Entitlements.Reads);
        Assert.Equal(0, fixture.Grants.Reads);
        Assert.Equal(0, fixture.Memberships.Reads);
    }

    [Fact]
    public async Task Provider_outage_does_not_fall_back_to_a_previous_positive_decision()
    {
        var fixture = new Fixture(GameAudience.Public, approved: true);
        Assert.Equal(AccessDecision.Allowed, (await fixture.Service.ReadAsync(GameId, null)).Decision);
        fixture.Entitlements.Error = new IOException("provider unavailable");
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.ReadAsync(GameId, null));
    }

    [Fact]
    public async Task Draft_preview_requires_current_editor_membership()
    {
        var fixture = new Fixture(GameAudience.Private, published: false);
        fixture.Memberships.Value = new(WorkspaceId, "editor", WorkspaceRole.Member);
        Assert.Equal(AccessDecision.Allowed, (await fixture.Service.ReadAsync(GameId, "editor", preview: true)).Decision);
        fixture.Memberships.Value = new(WorkspaceId, "editor", WorkspaceRole.Viewer);
        Assert.Equal(AccessDecision.NotFound, (await fixture.Service.ReadAsync(GameId, "editor", preview: true)).Decision);
        Assert.Equal(0, fixture.Grants.Reads);
    }

    private sealed class Fixture
    {
        public readonly FakeEntitlements Entitlements = new();
        public readonly FakeMemberships Memberships = new();
        public readonly FakeGrants Grants = new();
        public readonly GameReadService Service;

        public Fixture(GameAudience audience, bool approved = false, bool published = true)
        {
            var game = new GameDetails(
                new GameSnapshot(GameId, WorkspaceId, audience, published, true, approved, false),
                "adventure", "An adventure", "Explore a world.");
            Service = new GameReadService(new FakeGames(game), Grants, Entitlements, Memberships,
                new FixedClock(Now));
        }
    }

    private sealed class FakeGames(GameDetails game) : IGameRepository
    {
        public Task<GameDetails?> GetAsync(Guid gameId, CancellationToken cancellationToken = default) =>
            Task.FromResult<GameDetails?>(gameId == game.Access.Id ? game : null);

        public Task<IReadOnlyList<GameDetails>> ListPublicCandidatesAsync(
            int skip, int take, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GameDetails>>([]);
    }

    private sealed class FakeGrants : IPlayGrantRepository
    {
        public PersonPlayGrant? Grant { get; set; }
        public int Reads { get; private set; }
        public Task<PersonPlayGrant?> GetAsync(Guid gameId, string subject, CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult(Grant?.GameId == gameId && Grant.Subject == subject ? Grant : null);
        }
    }

    private sealed class FakeMemberships : IWorkspaceMemberships
    {
        public WorkspaceMembership? Value { get; set; }
        public int Reads { get; private set; }
        public Task<WorkspaceMembership?> GetAsync(
            string workspaceId, string subject, CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult(Value?.WorkspaceId == workspaceId && Value.Subject == subject ? Value : null);
        }
    }

    private sealed class FakeEntitlements : IWorkspaceEntitlements
    {
        public PublishingEntitlement? Value { get; set; } = new(WorkspaceId, true, Now.AddHours(1));
        public Exception? Error { get; set; }
        public int Reads { get; private set; }
        public Task<PublishingEntitlement?> GetAsync(string workspaceId, CancellationToken cancellationToken = default)
        {
            Reads++;
            if (Error is not null)
                throw Error;
            return Task.FromResult(Value?.WorkspaceId == workspaceId ? Value : null);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
