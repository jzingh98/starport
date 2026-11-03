using Microsoft.EntityFrameworkCore;

namespace Starport.Infrastructure.Database;

public sealed class StarportDbContext(DbContextOptions<StarportDbContext> options) : DbContext(options)
{
    public DbSet<WorkspaceRecord> Workspaces => Set<WorkspaceRecord>();
    public DbSet<WorkspaceMemberRecord> WorkspaceMembers => Set<WorkspaceMemberRecord>();
    public DbSet<WorkspacePublishingRecord> WorkspacePublishing => Set<WorkspacePublishingRecord>();
    public DbSet<GameRecord> Games => Set<GameRecord>();
    public DbSet<GameReleaseRecord> GameReleases => Set<GameReleaseRecord>();
    public DbSet<ReleaseFileRecord> ReleaseFiles => Set<ReleaseFileRecord>();
    public DbSet<GameUploadRecord> GameUploads => Set<GameUploadRecord>();
    public DbSet<GameReviewApprovalRecord> GameReviewApprovals => Set<GameReviewApprovalRecord>();
    public DbSet<GameReviewRequestRecord> GameReviewRequests => Set<GameReviewRequestRecord>();
    public DbSet<GameModerationActionRecord> GameModerationActions => Set<GameModerationActionRecord>();
    public DbSet<GameReportRecord> GameReports => Set<GameReportRecord>();
    public DbSet<GamePlayGrantRecord> GamePlayGrants => Set<GamePlayGrantRecord>();
    public DbSet<GameSaveRecord> GameSaves => Set<GameSaveRecord>();
    public DbSet<GameSaveMutationRecord> GameSaveMutations => Set<GameSaveMutationRecord>();
    public DbSet<WorkspaceDeliveryUsageRecord> WorkspaceDeliveryUsage => Set<WorkspaceDeliveryUsageRecord>();
    public DbSet<GameLaunchRecord> GameLaunches => Set<GameLaunchRecord>();
    public DbSet<AccountsEventCursorRecord> AccountsEventCursors => Set<AccountsEventCursorRecord>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<AccountsEventCursorRecord>(b =>
        {
            b.ToTable("AccountsEventCursors");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasMaxLength(64).ValueGeneratedNever();
        });
        model.Entity<WorkspaceRecord>(b =>
        {
            b.ToTable("Workspaces");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Id).HasMaxLength(64).IsRequired();
            b.Property(x => x.Slug).HasMaxLength(80).IsRequired();
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16).IsRequired();
            b.HasIndex(x => x.Slug).IsUnique();
        });
        model.Entity<WorkspaceMemberRecord>(b =>
        {
            b.ToTable("WorkspaceMembers");
            b.HasKey(x => new { x.WorkspaceId, x.Subject });
            b.Property(x => x.WorkspaceId).HasMaxLength(64).IsRequired();
            b.Property(x => x.Subject).HasMaxLength(256);
            b.Property(x => x.Role).HasConversion<string>().HasMaxLength(16).IsRequired();
            b.HasOne<WorkspaceRecord>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<WorkspacePublishingRecord>(b =>
        {
            b.ToTable("WorkspacePublishing");
            b.HasKey(x => x.WorkspaceId);
            b.Property(x => x.WorkspaceId).HasMaxLength(64).ValueGeneratedNever();
            b.Property(x => x.LimitsJson).HasColumnType("jsonb").IsRequired();
            b.HasOne<WorkspaceRecord>().WithOne().HasForeignKey<WorkspacePublishingRecord>(x => x.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<GameRecord>(b =>
        {
            b.ToTable("Games");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.WorkspaceId).HasMaxLength(64).IsRequired();
            b.Property(x => x.Slug).HasMaxLength(120).IsRequired();
            b.Property(x => x.Title).HasMaxLength(200).IsRequired();
            b.Property(x => x.Description).HasMaxLength(2000).IsRequired();
            b.Property(x => x.Audience).HasConversion<string>().HasMaxLength(16).IsRequired();
            b.Property(x => x.MetadataRevision).HasDefaultValue(1).IsRequired();
            b.Property<uint>("xmin").IsRowVersion();
            b.HasIndex(x => x.Slug).IsUnique();
            b.HasIndex(x => new { x.WorkspaceId, x.UpdatedAt });
            b.HasIndex(x => new { x.Audience, x.IsPublished });
        });
        model.Entity<GameReleaseRecord>(b =>
        {
            b.ToTable("GameReleases");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            b.Property(x => x.StorageKey).HasMaxLength(512).IsRequired();
            b.HasOne<GameRecord>().WithMany().HasForeignKey(x => x.GameId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(x => new { x.GameId, x.CreatedAt });
        });
        model.Entity<ReleaseFileRecord>(b =>
        {
            b.ToTable("ReleaseFiles");
            b.HasKey(x => new { x.ReleaseId, x.Path });
            b.Property(x => x.Path).HasMaxLength(512).IsRequired();
            b.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            b.HasOne<GameReleaseRecord>().WithMany().HasForeignKey(x => x.ReleaseId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<GameUploadRecord>(b =>
        {
            b.ToTable("GameUploads");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.WorkspaceId).HasMaxLength(64).IsRequired();
            b.Property(x => x.CreatedBySubject).HasMaxLength(256).IsRequired();
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
            b.Property(x => x.ArchiveSha256).HasMaxLength(64).IsRequired();
            b.Property(x => x.ArchivePath).HasMaxLength(512).IsRequired();
            b.Property(x => x.ErrorCode).HasMaxLength(64);
            b.HasOne<GameRecord>().WithMany().HasForeignKey(x => x.GameId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.Status, x.CreatedAt });
            b.HasIndex(x => new { x.GameId, x.CreatedAt });
        });
        model.Entity<GameReviewApprovalRecord>(b =>
        {
            b.ToTable("GameReviewApprovals");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.ReviewerSubject).HasMaxLength(256).IsRequired();
            b.Property(x => x.ReleaseSha256).HasMaxLength(64).IsRequired();
            b.HasOne<GameRecord>().WithMany().HasForeignKey(x => x.GameId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<GameReleaseRecord>().WithMany().HasForeignKey(x => x.ReleaseId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.GameId, x.ReleaseId, x.MetadataRevision }).IsUnique();
        });
        model.Entity<GameReviewRequestRecord>(b =>
        {
            b.ToTable("GameReviewRequests");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.ReleaseSha256).HasMaxLength(64).IsRequired();
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
            b.Property(x => x.RequestedBySubject).HasMaxLength(256).IsRequired();
            b.Property(x => x.ReviewerSubject).HasMaxLength(256);
            b.Property(x => x.DecisionReason).HasMaxLength(500);
            b.HasOne<GameRecord>().WithMany().HasForeignKey(x => x.GameId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<GameReleaseRecord>().WithMany().HasForeignKey(x => x.ReleaseId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.GameId, x.ReleaseId, x.MetadataRevision }).IsUnique();
            b.HasIndex(x => new { x.Status, x.RequestedAt });
        });
        model.Entity<GameModerationActionRecord>(b =>
        {
            b.ToTable("GameModerationActions");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.ModeratorSubject).HasMaxLength(256).IsRequired();
            b.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
            b.HasOne<GameRecord>().WithMany().HasForeignKey(x => x.GameId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(x => new { x.GameId, x.CreatedAt });
        });
        model.Entity<GameReportRecord>(b =>
        {
            b.ToTable("GameReports");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.ReporterSubject).HasMaxLength(256).IsRequired();
            b.Property(x => x.Category).HasMaxLength(50).IsRequired();
            b.Property(x => x.Description).HasMaxLength(2000).IsRequired();
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
            b.Property(x => x.ResolvedBySubject).HasMaxLength(256);
            b.Property(x => x.Resolution).HasMaxLength(1000);
            b.HasOne<GameRecord>().WithMany().HasForeignKey(x => x.GameId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(x => new { x.Status, x.CreatedAt });
        });
        model.Entity<GamePlayGrantRecord>(b =>
        {
            b.ToTable("GamePlayGrants");
            b.HasKey(x => new { x.GameId, x.Subject });
            b.Property(x => x.Subject).HasMaxLength(256);
            b.Property(x => x.Email).HasMaxLength(254);
            b.HasOne<GameRecord>().WithMany().HasForeignKey(x => x.GameId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<GameSaveRecord>(b =>
        {
            b.ToTable("GameSaves");
            b.HasKey(x => new { x.GameId, x.Subject, x.Slot });
            b.Property(x => x.Subject).HasMaxLength(256);
            b.Property(x => x.Slot).HasMaxLength(64);
            b.Property(x => x.DataJson).HasColumnType("jsonb").IsRequired();
            b.HasOne<GameRecord>().WithMany().HasForeignKey(x => x.GameId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<GameSaveMutationRecord>(b =>
        {
            b.ToTable("GameSaveMutations");
            b.HasKey(x => new { x.GameId, x.Subject, x.RequestKey });
            b.Property(x => x.Subject).HasMaxLength(256);
            b.Property(x => x.RequestKey).HasMaxLength(128);
            b.Property(x => x.Slot).HasMaxLength(64);
            b.Property(x => x.DataSha256).HasMaxLength(64);
            b.Property(x => x.Precondition).HasMaxLength(64);
            b.HasOne<GameRecord>().WithMany().HasForeignKey(x => x.GameId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(x => x.CreatedAt);
        });
        model.Entity<WorkspaceDeliveryUsageRecord>(b =>
        {
            b.ToTable("WorkspaceDeliveryUsage");
            b.HasKey(x => new { x.WorkspaceId, x.Month });
            b.Property(x => x.WorkspaceId).HasMaxLength(64);
            b.Property(x => x.Month).HasColumnType("date");
        });
        model.Entity<GameLaunchRecord>(b =>
        {
            b.ToTable("GameLaunches");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.TicketHash).HasMaxLength(64).IsRequired();
            b.Property(x => x.SessionHash).HasMaxLength(64);
            b.Property(x => x.Subject).HasMaxLength(256);
            b.HasOne<GameRecord>().WithMany().HasForeignKey(x => x.GameId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<GameReviewRequestRecord>().WithMany().HasForeignKey(x => x.ReviewRequestId)
                .OnDelete(DeleteBehavior.SetNull);
            b.HasIndex(x => x.TicketHash).IsUnique();
            b.HasIndex(x => x.SessionHash).IsUnique();
            b.HasIndex(x => x.SessionExpiresAt);
        });
    }
}
