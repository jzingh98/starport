using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Starport.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Workspaces",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Slug = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workspaces", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Games",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Slug = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Audience = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IsPublished = table.Column<bool>(type: "boolean", nullable: false),
                    IsModerationSuspended = table.Column<bool>(type: "boolean", nullable: false),
                    ActiveReleaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    MetadataRevision = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Games", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Games_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkspaceMembers",
                columns: table => new
                {
                    WorkspaceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspaceMembers", x => new { x.WorkspaceId, x.Subject });
                    table.ForeignKey(
                        name: "FK_WorkspaceMembers_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkspacePublishing",
                columns: table => new
                {
                    WorkspaceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CanPublish = table.Column<bool>(type: "boolean", nullable: false),
                    PaidThrough = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Suspended = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspacePublishing", x => x.WorkspaceId);
                    table.ForeignKey(
                        name: "FK_WorkspacePublishing_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GamePlayGrants",
                columns: table => new
                {
                    GameId = table.Column<Guid>(type: "uuid", nullable: false),
                    Subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GamePlayGrants", x => new { x.GameId, x.Subject });
                    table.ForeignKey(
                        name: "FK_GamePlayGrants_Games_GameId",
                        column: x => x.GameId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GameReleases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GameId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    IsValidated = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ValidatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameReleases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GameReleases_Games_GameId",
                        column: x => x.GameId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GameReviewApprovals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GameId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleaseSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MetadataRevision = table.Column<int>(type: "integer", nullable: false),
                    ReviewerSubject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameReviewApprovals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GameReviewApprovals_GameReleases_ReleaseId",
                        column: x => x.ReleaseId,
                        principalTable: "GameReleases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GameReviewApprovals_Games_GameId",
                        column: x => x.GameId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GameReleases_GameId_CreatedAt",
                table: "GameReleases",
                columns: new[] { "GameId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GameReviewApprovals_GameId_ReleaseId_MetadataRevision",
                table: "GameReviewApprovals",
                columns: new[] { "GameId", "ReleaseId", "MetadataRevision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GameReviewApprovals_ReleaseId",
                table: "GameReviewApprovals",
                column: "ReleaseId");

            migrationBuilder.CreateIndex(
                name: "IX_Games_Audience_IsPublished",
                table: "Games",
                columns: new[] { "Audience", "IsPublished" });

            migrationBuilder.CreateIndex(
                name: "IX_Games_Slug",
                table: "Games",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Games_WorkspaceId_UpdatedAt",
                table: "Games",
                columns: new[] { "WorkspaceId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Workspaces_Slug",
                table: "Workspaces",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GamePlayGrants");

            migrationBuilder.DropTable(
                name: "GameReviewApprovals");

            migrationBuilder.DropTable(
                name: "WorkspaceMembers");

            migrationBuilder.DropTable(
                name: "WorkspacePublishing");

            migrationBuilder.DropTable(
                name: "GameReleases");

            migrationBuilder.DropTable(
                name: "Games");

            migrationBuilder.DropTable(
                name: "Workspaces");
        }
    }
}
