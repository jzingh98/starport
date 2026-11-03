using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Starport.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PublicReviewRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GameReviewRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GameId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    MetadataRevision = table.Column<int>(type: "integer", nullable: false),
                    ReleaseSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RequestedBySubject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReviewerSubject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DecisionReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameReviewRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GameReviewRequests_GameReleases_ReleaseId",
                        column: x => x.ReleaseId,
                        principalTable: "GameReleases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GameReviewRequests_Games_GameId",
                        column: x => x.GameId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GameReviewRequests_GameId_ReleaseId_MetadataRevision",
                table: "GameReviewRequests",
                columns: new[] { "GameId", "ReleaseId", "MetadataRevision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GameReviewRequests_ReleaseId",
                table: "GameReviewRequests",
                column: "ReleaseId");

            migrationBuilder.CreateIndex(
                name: "IX_GameReviewRequests_Status_RequestedAt",
                table: "GameReviewRequests",
                columns: new[] { "Status", "RequestedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GameReviewRequests");
        }
    }
}
