using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace live_poll_backend.Migrations
{
    /// <inheritdoc />
    public partial class DecouplePollAndBidding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SkillBids_Polls_PollId",
                table: "SkillBids");

            migrationBuilder.DropTable(
                name: "PollSkills");

            migrationBuilder.DropColumn(
                name: "BiddingClosed",
                table: "Polls");

            migrationBuilder.DropColumn(
                name: "IsBiddingActive",
                table: "Polls");

            migrationBuilder.DropColumn(
                name: "SkillCost",
                table: "Polls");

            migrationBuilder.RenameColumn(
                name: "PollId",
                table: "SkillBids",
                newName: "BiddingPollId");

            migrationBuilder.RenameIndex(
                name: "IX_SkillBids_PollId_SkillId_SessionId",
                table: "SkillBids",
                newName: "IX_SkillBids_BiddingPollId_SkillId_SessionId");

            migrationBuilder.CreateTable(
                name: "BiddingPolls",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedByEmail = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    CreatedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsBiddingActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    BiddingClosed = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    SkillCost = table.Column<int>(type: "integer", nullable: false, defaultValue: 20),
                    Theme = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false, defaultValue: "synergy_sphere"),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BiddingPolls", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BiddingPollSkills",
                columns: table => new
                {
                    BiddingPollsId = table.Column<string>(type: "character varying(10)", nullable: false),
                    SkillsId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BiddingPollSkills", x => new { x.BiddingPollsId, x.SkillsId });
                    table.ForeignKey(
                        name: "FK_BiddingPollSkills_BiddingPolls_BiddingPollsId",
                        column: x => x.BiddingPollsId,
                        principalTable: "BiddingPolls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BiddingPollSkills_Skills_SkillsId",
                        column: x => x.SkillsId,
                        principalTable: "Skills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BiddingPollSkills_SkillsId",
                table: "BiddingPollSkills",
                column: "SkillsId");

            migrationBuilder.AddForeignKey(
                name: "FK_SkillBids_BiddingPolls_BiddingPollId",
                table: "SkillBids",
                column: "BiddingPollId",
                principalTable: "BiddingPolls",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SkillBids_BiddingPolls_BiddingPollId",
                table: "SkillBids");

            migrationBuilder.DropTable(
                name: "BiddingPollSkills");

            migrationBuilder.DropTable(
                name: "BiddingPolls");

            migrationBuilder.RenameColumn(
                name: "BiddingPollId",
                table: "SkillBids",
                newName: "PollId");

            migrationBuilder.RenameIndex(
                name: "IX_SkillBids_BiddingPollId_SkillId_SessionId",
                table: "SkillBids",
                newName: "IX_SkillBids_PollId_SkillId_SessionId");

            migrationBuilder.AddColumn<bool>(
                name: "BiddingClosed",
                table: "Polls",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsBiddingActive",
                table: "Polls",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "SkillCost",
                table: "Polls",
                type: "integer",
                nullable: false,
                defaultValue: 20);

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

            migrationBuilder.AddForeignKey(
                name: "FK_SkillBids_Polls_PollId",
                table: "SkillBids",
                column: "PollId",
                principalTable: "Polls",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
