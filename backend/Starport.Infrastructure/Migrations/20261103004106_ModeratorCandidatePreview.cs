using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Starport.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ModeratorCandidatePreview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ReviewRequestId",
                table: "GameLaunches",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_GameLaunches_ReviewRequestId",
                table: "GameLaunches",
                column: "ReviewRequestId");

            migrationBuilder.AddForeignKey(
                name: "FK_GameLaunches_GameReviewRequests_ReviewRequestId",
                table: "GameLaunches",
                column: "ReviewRequestId",
                principalTable: "GameReviewRequests",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_GameLaunches_GameReviewRequests_ReviewRequestId",
                table: "GameLaunches");

            migrationBuilder.DropIndex(
                name: "IX_GameLaunches_ReviewRequestId",
                table: "GameLaunches");

            migrationBuilder.DropColumn(
                name: "ReviewRequestId",
                table: "GameLaunches");
        }
    }
}
