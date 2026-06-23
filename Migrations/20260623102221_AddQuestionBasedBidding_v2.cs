using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace live_poll_backend.Migrations
{
    /// <inheritdoc />
    public partial class AddQuestionBasedBidding_v2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("TRUNCATE TABLE \"SkillBids\";");

            migrationBuilder.DropForeignKey(
                name: "FK_SkillBids_Skills_SkillId",
                table: "SkillBids");

            migrationBuilder.DropTable(
                name: "BiddingPollSkills");

            migrationBuilder.DropTable(
                name: "Skills");

            migrationBuilder.DropIndex(
                name: "IX_SkillBids_BiddingPollId_SkillId_SessionId",
                table: "SkillBids");

            migrationBuilder.DropIndex(
                name: "IX_SkillBids_SkillId",
                table: "SkillBids");

            migrationBuilder.DropColumn(
                name: "SkillCost",
                table: "BiddingPolls");

            migrationBuilder.RenameColumn(
                name: "SkillId",
                table: "SkillBids",
                newName: "QuestionIndex");

            migrationBuilder.AddColumn<int>(
                name: "BiddingSkillId",
                table: "SkillBids",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ActiveQuestionIndex",
                table: "BiddingPolls",
                type: "integer",
                nullable: false,
                defaultValue: -1);

            migrationBuilder.AddColumn<string>(
                name: "CurrentCohort",
                table: "BiddingPolls",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "BiddingQuestions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BiddingPollId = table.Column<string>(type: "character varying(10)", nullable: false),
                    Text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Index = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BiddingQuestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BiddingQuestions_BiddingPolls_BiddingPollId",
                        column: x => x.BiddingPollId,
                        principalTable: "BiddingPolls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BiddingSkills",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BiddingQuestionId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Index = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BiddingSkills", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BiddingSkills_BiddingQuestions_BiddingQuestionId",
                        column: x => x.BiddingQuestionId,
                        principalTable: "BiddingQuestions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SkillBids_BiddingPollId_BiddingSkillId_SessionId_Cohort",
                table: "SkillBids",
                columns: new[] { "BiddingPollId", "BiddingSkillId", "SessionId", "Cohort" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SkillBids_BiddingPollId_SessionId_Cohort",
                table: "SkillBids",
                columns: new[] { "BiddingPollId", "SessionId", "Cohort" });

            migrationBuilder.CreateIndex(
                name: "IX_SkillBids_BiddingSkillId",
                table: "SkillBids",
                column: "BiddingSkillId");

            migrationBuilder.CreateIndex(
                name: "IX_BiddingQuestions_BiddingPollId",
                table: "BiddingQuestions",
                column: "BiddingPollId");

            migrationBuilder.CreateIndex(
                name: "IX_BiddingSkills_BiddingQuestionId",
                table: "BiddingSkills",
                column: "BiddingQuestionId");

            migrationBuilder.AddForeignKey(
                name: "FK_SkillBids_BiddingSkills_BiddingSkillId",
                table: "SkillBids",
                column: "BiddingSkillId",
                principalTable: "BiddingSkills",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SkillBids_BiddingSkills_BiddingSkillId",
                table: "SkillBids");

            migrationBuilder.DropTable(
                name: "BiddingSkills");

            migrationBuilder.DropTable(
                name: "BiddingQuestions");

            migrationBuilder.DropIndex(
                name: "IX_SkillBids_BiddingPollId_BiddingSkillId_SessionId_Cohort",
                table: "SkillBids");

            migrationBuilder.DropIndex(
                name: "IX_SkillBids_BiddingPollId_SessionId_Cohort",
                table: "SkillBids");

            migrationBuilder.DropIndex(
                name: "IX_SkillBids_BiddingSkillId",
                table: "SkillBids");

            migrationBuilder.DropColumn(
                name: "BiddingSkillId",
                table: "SkillBids");

            migrationBuilder.DropColumn(
                name: "ActiveQuestionIndex",
                table: "BiddingPolls");

            migrationBuilder.DropColumn(
                name: "CurrentCohort",
                table: "BiddingPolls");

            migrationBuilder.RenameColumn(
                name: "QuestionIndex",
                table: "SkillBids",
                newName: "SkillId");

            migrationBuilder.AddColumn<int>(
                name: "SkillCost",
                table: "BiddingPolls",
                type: "integer",
                nullable: false,
                defaultValue: 20);

            migrationBuilder.CreateTable(
                name: "Skills",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Skills", x => x.Id);
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
                name: "IX_SkillBids_BiddingPollId_SkillId_SessionId",
                table: "SkillBids",
                columns: new[] { "BiddingPollId", "SkillId", "SessionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SkillBids_SkillId",
                table: "SkillBids",
                column: "SkillId");

            migrationBuilder.CreateIndex(
                name: "IX_BiddingPollSkills_SkillsId",
                table: "BiddingPollSkills",
                column: "SkillsId");

            migrationBuilder.AddForeignKey(
                name: "FK_SkillBids_Skills_SkillId",
                table: "SkillBids",
                column: "SkillId",
                principalTable: "Skills",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
