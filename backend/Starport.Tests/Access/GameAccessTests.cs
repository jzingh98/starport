using Starport.Application.Access;
using Starport.Domain.Access;

namespace Starport.Tests.Access;

public sealed class GameAccessTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private const string OwnerId = "ws_owner";
    private const string OtherId = "ws_other";
    private static readonly Guid GameId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static PublishingEntitlement Active => new(OwnerId, true, Now.AddHours(1));
    private static GameSnapshot Game(GameAudience audience, bool approved = false, bool published = true) =>
        new(GameId, OwnerId, audience, published, true, approved, false);
    private static WorkspaceMembership Member(string subject, WorkspaceRole role = WorkspaceRole.Member) =>
        new(OwnerId, subject, role);
    private static PlayerContext Player(string? subject = null, WorkspaceMembership? membership = null,
        PersonPlayGrant? grant = null) => new(subject, membership, grant);

    [Fact]
    public void Pro_member_cannot_publish_into_an_unrelated_free_team()
    {
        var unrelatedPro = new PublishingEntitlement(OtherId, true, Now.AddHours(1));
        Assert.Equal(AccessDecision.PublisherOffline,
            GameAccess.CanPublish(OwnerId, "alice", Member("alice"), unrelatedPro, Now));
        Assert.Equal(AccessDecision.NotFound,
            GameAccess.CanPublish(OwnerId, "alice", new(OtherId, "alice", WorkspaceRole.Owner), Active, Now));
        Assert.Equal(AccessDecision.NotFound,
            GameAccess.CanPublish(OwnerId, "mallory", Member("alice"), Active, Now));
    }

    [Fact]
    public void Team_viewer_can_play_but_cannot_publish_or_preview_draft()
    {
        var viewer = Member("viewer", WorkspaceRole.Viewer);
        Assert.Equal(AccessDecision.Forbidden, GameAccess.CanPublish(OwnerId, "viewer", viewer, Active, Now));
        Assert.Equal(AccessDecision.Allowed,
            GameAccess.CanPlay(Game(GameAudience.Team), Active, Player("viewer", viewer), Now));
        Assert.Equal(AccessDecision.NotFound,
            GameAccess.CanPlay(Game(GameAudience.Team, published: false), Active,
                Player("viewer", viewer), Now, preview: true));
    }

    [Fact]
    public void Free_player_with_a_person_grant_can_play_but_cannot_edit()
    {
        var recipient = Player("free-player", grant: new(GameId, "free-player"));
        Assert.Equal(AccessDecision.Allowed,
            GameAccess.CanPlay(Game(GameAudience.People), Active, recipient, Now));
        Assert.Equal(AccessDecision.NotFound,
            GameAccess.CanPlay(Game(GameAudience.People), Active,
                Player("other", grant: new(GameId, "free-player")), Now));
        Assert.Equal(AccessDecision.NotFound, GameAccess.CanPublish(OwnerId, "free-player", null, Active, Now));
    }

    [Fact]
    public void Public_release_needs_review_and_an_active_publisher()
    {
        Assert.Equal(AccessDecision.ReviewRequired,
            GameAccess.CanActivatePublicRelease(Game(GameAudience.Public), "creator", Member("creator"), Active, Now));
        Assert.Equal(AccessDecision.NotFound,
            GameAccess.CanPlay(Game(GameAudience.Public), Active, Player(), Now));
        Assert.Equal(AccessDecision.Allowed,
            GameAccess.CanPlay(Game(GameAudience.Public, approved: true), Active, Player(), Now));
    }

    [Fact]
    public void Paid_period_boundary_stops_every_audience_immediately()
    {
        var until = Now.AddSeconds(1);
        var entitlement = new PublishingEntitlement(OwnerId, true, until);
        var shared = Player("friend", grant: new(GameId, "friend"));
        Assert.Equal(AccessDecision.Allowed,
            GameAccess.CanPlay(Game(GameAudience.People), entitlement, shared, Now));
        Assert.Equal(AccessDecision.PublisherOffline,
            GameAccess.CanPlay(Game(GameAudience.People), entitlement, shared, until));
        Assert.Equal(AccessDecision.PublisherOffline,
            GameAccess.CanPlay(Game(GameAudience.Public, approved: true), entitlement, Player(), until));
        Assert.Equal(AccessDecision.PublisherOffline,
            GameAccess.CanPublish(OwnerId, "creator", Member("creator"), entitlement, until));
    }

    [Fact]
    public void Revocation_and_moderation_override_player_access()
    {
        var expiredGrant = new PersonPlayGrant(GameId, "friend", Now);
        Assert.Equal(AccessDecision.NotFound,
            GameAccess.CanPlay(Game(GameAudience.People), Active,
                Player("friend", grant: expiredGrant), Now));
        Assert.Equal(AccessDecision.PublisherOffline,
            GameAccess.CanPlay(Game(GameAudience.Public, approved: true), Active with { Suspended = true },
                Player(), Now));
        Assert.Equal(AccessDecision.NotFound,
            GameAccess.CanPlay(Game(GameAudience.Public, approved: true) with { IsModerationSuspended = true },
                Active, Player(), Now));
    }
}
