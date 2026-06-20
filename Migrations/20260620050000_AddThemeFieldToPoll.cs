using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace live_poll_backend.Migrations
{
    /// <inheritdoc />
    public partial class AddThemeFieldToPoll : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Theme",
                table: "Polls",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "default");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Theme",
                table: "Polls");
        }
    }
}
