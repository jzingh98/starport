using Starport.Domain.Access;

namespace Starport.Infrastructure.Database;

public enum WorkspaceKind { Personal, Team }

public sealed class WorkspaceRecord
{
    public required string Id { get; set; }
    public required string Slug { get; set; }
    public required string Name { get; set; }
    public WorkspaceKind Kind { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class WorkspaceMemberRecord
{
    public required string WorkspaceId { get; set; }
    public required string Subject { get; set; }
    public WorkspaceRole Role { get; set; }
}

/// <summary>Local self-hosted grant. Hosted mode will use the authoritative Accounts adapter.</summary>
public sealed class WorkspacePublishingRecord
{
    public required string WorkspaceId { get; set; }
    public bool CanPublish { get; set; }
    public DateTimeOffset? PaidThrough { get; set; }
    public bool Suspended { get; set; }
    public required string LimitsJson { get; set; } = "{}";
}

public sealed class GameRecord
{
    public Guid Id { get; set; }
    public required string WorkspaceId { get; set; }
    public required string Slug { get; set; }
    public required string Title { get; set; }
    public required string Description { get; set; }
    public GameAudience Audience { get; set; } = GameAudience.Private;
    public bool IsPublished { get; set; }
    public bool IsModerationSuspended { get; set; }
    public Guid? ActiveReleaseId { get; set; }
    public int MetadataRevision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class GameReleaseRecord
{
    public Guid Id { get; set; }
    public Guid GameId { get; set; }
    public required string Sha256 { get; set; }
    public required string StorageKey { get; set; }
    public bool IsValidated { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ValidatedAt { get; set; }
}

public sealed class ReleaseFileRecord
{
    public Guid ReleaseId { get; set; }
    public required string Path { get; set; }
    public long Bytes { get; set; }
    public required string Sha256 { get; set; }
}

public enum GameUploadStatus { Pending, Processing, Ready, Failed }

public sealed class GameUploadRecord
{
    public Guid Id { get; set; }
    public Guid GameId { get; set; }
    public required string WorkspaceId { get; set; }
    public required string CreatedBySubject { get; set; }
    public GameUploadStatus Status { get; set; }
    public long ArchiveBytes { get; set; }
    public required string ArchiveSha256 { get; set; }
    public required string ArchivePath { get; set; }
    public Guid? ReleaseId { get; set; }
    public string? ErrorCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>Approval applies to exactly one validated release and metadata revision.</summary>
public sealed class GameReviewApprovalRecord
{
    public Guid Id { get; set; }
    public Guid GameId { get; set; }
    public Guid ReleaseId { get; set; }
    public required string ReleaseSha256 { get; set; }
    public int MetadataRevision { get; set; }
    public required string ReviewerSubject { get; set; }
    public DateTimeOffset ApprovedAt { get; set; }
}

public enum GameReviewStatus { Pending, Approved, Rejected }

public sealed class GameReviewRequestRecord
{
    public Guid Id { get; set; }
    public Guid GameId { get; set; }
    public Guid ReleaseId { get; set; }
    public int MetadataRevision { get; set; }
    public required string ReleaseSha256 { get; set; }
    public GameReviewStatus Status { get; set; } = GameReviewStatus.Pending;
    public required string RequestedBySubject { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public string? ReviewerSubject { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecisionReason { get; set; }
}

public sealed class GameModerationActionRecord
{
    public Guid Id { get; set; }
    public Guid GameId { get; set; }
    public required string ModeratorSubject { get; set; }
    public bool Suspended { get; set; }
    public required string Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum GameReportStatus { New, Resolved }

public sealed class GameReportRecord
{
    public Guid Id { get; set; }
    public Guid GameId { get; set; }
    public required string ReporterSubject { get; set; }
    public required string Category { get; set; }
    public required string Description { get; set; }
    public GameReportStatus Status { get; set; } = GameReportStatus.New;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? ResolvedBySubject { get; set; }
    public string? Resolution { get; set; }
}

public sealed class GamePlayGrantRecord
{
    public Guid GameId { get; set; }
    public required string Subject { get; set; }
    /// <summary>Email verified when the grant was created; a display label, never an access key.</summary>
    public string? Email { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}

public sealed class GameSaveRecord
{
    public Guid GameId { get; set; }
    public required string Subject { get; set; }
    public required string Slot { get; set; }
    public required string DataJson { get; set; }
    public Guid Revision { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class GameSaveMutationRecord
{
    public Guid GameId { get; set; }
    public required string Subject { get; set; }
    public required string RequestKey { get; set; }
    public required string Slot { get; set; }
    public required string DataSha256 { get; set; }
    public required string Precondition { get; set; }
    public Guid Revision { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class WorkspaceDeliveryUsageRecord
{
    public required string WorkspaceId { get; set; }
    public DateOnly Month { get; set; }
    public long Bytes { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class GameLaunchRecord
{
    public Guid Id { get; set; }
    public Guid GameId { get; set; }
    public Guid ReleaseId { get; set; }
    public string? Subject { get; set; }
    public bool Preview { get; set; }
    public Guid? ReviewRequestId { get; set; }
    public required string TicketHash { get; set; }
    public string? SessionHash { get; set; }
    public DateTimeOffset TicketExpiresAt { get; set; }
    public DateTimeOffset? SessionExpiresAt { get; set; }
    public DateTimeOffset? RedeemedAt { get; set; }
}
