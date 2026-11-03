using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Starport.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class GameSessionsAndSaves : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GameLaunches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GameId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Preview = table.Column<bool>(type: "boolean", nullable: false),
                    TicketHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SessionHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TicketExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SessionExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RedeemedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameLaunches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GameLaunches_Games_GameId",
                        column: x => x.GameId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GameSaves",
                columns: table => new
                {
                    GameId = table.Column<Guid>(type: "uuid", nullable: false),
                    Subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Slot = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DataJson = table.Column<string>(type: "jsonb", nullable: false),
                    Revision = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameSaves", x => new { x.GameId, x.Subject, x.Slot });
                    table.ForeignKey(
                        name: "FK_GameSaves_Games_GameId",
                        column: x => x.GameId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GameLaunches_GameId",
                table: "GameLaunches",
                column: "GameId");

            migrationBuilder.CreateIndex(
                name: "IX_GameLaunches_SessionExpiresAt",
                table: "GameLaunches",
                column: "SessionExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_GameLaunches_SessionHash",
                table: "GameLaunches",
                column: "SessionHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GameLaunches_TicketHash",
                table: "GameLaunches",
                column: "TicketHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GameLaunches");

            migrationBuilder.DropTable(
                name: "GameSaves");
        }
    }
}
