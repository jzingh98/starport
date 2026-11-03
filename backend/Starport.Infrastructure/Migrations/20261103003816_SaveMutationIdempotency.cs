using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Starport.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SaveMutationIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GameSaveMutations",
                columns: table => new
                {
                    GameId = table.Column<Guid>(type: "uuid", nullable: false),
                    Subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RequestKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Slot = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DataSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Precondition = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Revision = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameSaveMutations", x => new { x.GameId, x.Subject, x.RequestKey });
                    table.ForeignKey(
                        name: "FK_GameSaveMutations_Games_GameId",
                        column: x => x.GameId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GameSaveMutations_CreatedAt",
                table: "GameSaveMutations",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GameSaveMutations");
        }
    }
}
