using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Starport.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UploadsAndReleaseFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GameUploads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GameId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedBySubject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ArchiveBytes = table.Column<long>(type: "bigint", nullable: false),
                    ArchiveSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ArchivePath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ReleaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameUploads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GameUploads_Games_GameId",
                        column: x => x.GameId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReleaseFiles",
                columns: table => new
                {
                    ReleaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Bytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReleaseFiles", x => new { x.ReleaseId, x.Path });
                    table.ForeignKey(
                        name: "FK_ReleaseFiles_GameReleases_ReleaseId",
                        column: x => x.ReleaseId,
                        principalTable: "GameReleases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GameUploads_GameId_CreatedAt",
                table: "GameUploads",
                columns: new[] { "GameId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GameUploads_Status_CreatedAt",
                table: "GameUploads",
                columns: new[] { "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GameUploads");

            migrationBuilder.DropTable(
                name: "ReleaseFiles");
        }
    }
}
