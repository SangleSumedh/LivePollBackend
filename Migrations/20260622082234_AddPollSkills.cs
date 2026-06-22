using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace live_poll_backend.Migrations
{
    /// <inheritdoc />
    public partial class AddPollSkills : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PollSkills",
                columns: table => new
                {
                    PollsId = table.Column<string>(type: "character varying(10)", nullable: false),
                    SkillsId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PollSkills", x => new { x.PollsId, x.SkillsId });
                    table.ForeignKey(
                        name: "FK_PollSkills_Polls_PollsId",
                        column: x => x.PollsId,
                        principalTable: "Polls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PollSkills_Skills_SkillsId",
                        column: x => x.SkillsId,
                        principalTable: "Skills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PollSkills_SkillsId",
                table: "PollSkills",
                column: "SkillsId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PollSkills");
        }
    }
}
