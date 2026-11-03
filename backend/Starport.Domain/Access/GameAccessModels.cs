namespace Starport.Domain.Access;

public enum WorkspaceRole { Owner, Admin, Member, Viewer }

public enum GameAudience { Private, People, Team, Public }

public sealed record WorkspaceMembership(string WorkspaceId, string Subject, WorkspaceRole Role);

/// <summary>The serving entitlement of the workspace that owns a game.</summary>
public sealed record PublishingEntitlement(
    string WorkspaceId, bool CanPublish, DateTimeOffset? PaidThrough, bool Suspended = false,
    IReadOnlyDictionary<string, int?>? Limits = null)
{
    // PaidThrough is exclusive. An expired or unknown boundary never serves a game.
    public bool IsActiveAt(DateTimeOffset now) => CanPublish && !Suspended && PaidThrough > now;

    public int? Limit(string key) => Limits is not null && Limits.TryGetValue(key, out var value) ? value : null;
}

public sealed record GameSnapshot(
    Guid Id,
    string WorkspaceId,
    GameAudience Audience,
    bool IsPublished,
    bool HasValidatedRelease,
    bool PublicReviewApproved,
    bool IsModerationSuspended);

/// <summary>A play grant is bound to the player identity, never to an email or a guessed URL.</summary>
public sealed record PersonPlayGrant(Guid GameId, string Subject, DateTimeOffset? ExpiresAt = null)
{
    public bool IsActiveAt(DateTimeOffset now) => ExpiresAt is null || ExpiresAt > now;
}

public sealed record PlayerContext(
    string? Subject,
    WorkspaceMembership? OwningWorkspaceMembership,
    PersonPlayGrant? PersonGrant);
