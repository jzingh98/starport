using Starport.Domain.Access;

namespace Starport.Application.Access;

public enum AccessDecision
{
    Allowed,
    SignInRequired,
    NotFound,
    PublisherOffline,
    ReviewRequired,
    DeliveryQuotaExceeded,
    Forbidden
}

/// <summary>
/// Pure policy shared by catalog, launch, delivery and publication handlers. Those handlers must
/// fetch current entitlement and membership and apply this policy at every protected boundary.
/// </summary>
public static class GameAccess
{
    public static AccessDecision CanPublish(
        string workspaceId, string? subject, WorkspaceMembership? membership, PublishingEntitlement? entitlement,
        DateTimeOffset now)
    {
        if (subject is null || membership?.WorkspaceId != workspaceId || membership.Subject != subject)
            return AccessDecision.NotFound;
        if (membership.Role == WorkspaceRole.Viewer)
            return AccessDecision.Forbidden;
        return entitlement?.WorkspaceId == workspaceId && entitlement.IsActiveAt(now)
            ? AccessDecision.Allowed
            : AccessDecision.PublisherOffline;
    }

    public static AccessDecision CanActivatePublicRelease(
        GameSnapshot game, string? subject, WorkspaceMembership? membership, PublishingEntitlement? entitlement,
        DateTimeOffset now)
    {
        var publishing = CanPublish(game.WorkspaceId, subject, membership, entitlement, now);
        if (publishing != AccessDecision.Allowed)
            return publishing;
        if (game.IsModerationSuspended || !game.HasValidatedRelease)
            return AccessDecision.Forbidden;
        return game.PublicReviewApproved ? AccessDecision.Allowed : AccessDecision.ReviewRequired;
    }

    public static AccessDecision CanPlay(
        GameSnapshot game, PublishingEntitlement? ownerEntitlement, PlayerContext player,
        DateTimeOffset now, bool preview = false)
    {
        if (game.IsModerationSuspended || !game.HasValidatedRelease)
            return AccessDecision.NotFound;
        if (ownerEntitlement?.WorkspaceId != game.WorkspaceId || !ownerEntitlement.IsActiveAt(now))
            return AccessDecision.PublisherOffline;

        var member = player.Subject is not null &&
            player.OwningWorkspaceMembership is { } membership &&
            membership.WorkspaceId == game.WorkspaceId &&
            membership.Subject == player.Subject
                ? membership
                : null;

        // A validated draft may be previewed by its publisher collaborators only.
        if (preview)
            return member is { Role: not WorkspaceRole.Viewer }
                ? AccessDecision.Allowed
                : AccessDecision.NotFound;
        if (!game.IsPublished)
            return AccessDecision.NotFound;

        // Public listings require review of the exact active release and metadata revision.
        if (game.Audience == GameAudience.Public)
            return game.PublicReviewApproved ? AccessDecision.Allowed : AccessDecision.NotFound;

        if (member is { Role: not WorkspaceRole.Viewer })
            return AccessDecision.Allowed;
        if (game.Audience == GameAudience.Team)
            return member is not null ? AccessDecision.Allowed : AccessDecision.NotFound;
        if (game.Audience == GameAudience.People)
        {
            var grant = player.PersonGrant;
            return player.Subject is not null && grant?.GameId == game.Id &&
                   grant.Subject == player.Subject && grant.IsActiveAt(now)
                ? AccessDecision.Allowed
                : AccessDecision.NotFound;
        }
        return AccessDecision.NotFound;
    }
}
