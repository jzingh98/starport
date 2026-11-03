using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Starport.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UploadStartedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StartedAt",
                table: "GameUploads",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StartedAt",
                table: "GameUploads");
        }
    }
}
