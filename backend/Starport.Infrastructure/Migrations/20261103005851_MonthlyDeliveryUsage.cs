using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Starport.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MonthlyDeliveryUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkspaceDeliveryUsage",
                columns: table => new
                {
                    WorkspaceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Month = table.Column<DateOnly>(type: "date", nullable: false),
                    Bytes = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspaceDeliveryUsage", x => new { x.WorkspaceId, x.Month });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkspaceDeliveryUsage");
        }
    }
}
