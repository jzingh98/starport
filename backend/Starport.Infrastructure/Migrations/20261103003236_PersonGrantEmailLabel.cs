using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Starport.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PersonGrantEmailLabel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "GamePlayGrants",
                type: "character varying(254)",
                maxLength: 254,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Email",
                table: "GamePlayGrants");
        }
    }
}
