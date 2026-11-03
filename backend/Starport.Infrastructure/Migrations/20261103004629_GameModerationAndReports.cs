using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Starport.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class GameModerationAndReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GameModerationActions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GameId = table.Column<Guid>(type: "uuid", nullable: false),
                    ModeratorSubject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Suspended = table.Column<bool>(type: "boolean", nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameModerationActions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GameModerationActions_Games_GameId",
                        column: x => x.GameId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GameReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GameId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReporterSubject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ResolvedBySubject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Resolution = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameReports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GameReports_Games_GameId",
                        column: x => x.GameId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GameModerationActions_GameId_CreatedAt",
                table: "GameModerationActions",
                columns: new[] { "GameId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GameReports_GameId",
                table: "GameReports",
                column: "GameId");

            migrationBuilder.CreateIndex(
                name: "IX_GameReports_Status_CreatedAt",
                table: "GameReports",
                columns: new[] { "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GameModerationActions");

            migrationBuilder.DropTable(
                name: "GameReports");
        }
    }
}
