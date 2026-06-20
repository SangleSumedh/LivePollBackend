using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace live_poll_backend.Migrations
{
    /// <inheritdoc />
    public partial class AddWordCloudSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Add Type column as nullable first to avoid failure on existing rows
            migrationBuilder.AddColumn<string>(
                name: "Type",
                table: "Questions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true,
                defaultValue: "MultipleChoice");

            // 2. Backfill existing question rows
            migrationBuilder.Sql("UPDATE \"Questions\" SET \"Type\" = 'MultipleChoice' WHERE \"Type\" IS NULL;");

            // 3. Make the column non-nullable now that it is backfilled
            migrationBuilder.AlterColumn<string>(
                name: "Type",
                table: "Questions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "MultipleChoice",
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20,
                oldNullable: true);

            // 4. Make OptionIndex nullable in Votes table
            migrationBuilder.AlterColumn<int>(
                name: "OptionIndex",
                table: "Votes",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            // 5. Add SubmittedText column to Votes
            migrationBuilder.AddColumn<string>(
                name: "SubmittedText",
                table: "Votes",
                type: "text",
                nullable: true);

            // 6. Create WordCloudCounts table
            migrationBuilder.CreateTable(
                name: "WordCloudCounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PollId = table.Column<string>(type: "character varying(10)", nullable: false),
                    QuestionIndex = table.Column<int>(type: "integer", nullable: false),
                    Word = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WordCloudCounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WordCloudCounts_Polls_PollId",
                        column: x => x.PollId,
                        principalTable: "Polls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // 7. Create Unique Index for WordCloudCounts
            migrationBuilder.CreateIndex(
                name: "IX_WordCloudCounts_PollId_QuestionIndex_Word",
                table: "WordCloudCounts",
                columns: new[] { "PollId", "QuestionIndex", "Word" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WordCloudCounts");

            migrationBuilder.DropColumn(
                name: "SubmittedText",
                table: "Votes");

            migrationBuilder.AlterColumn<int>(
                name: "OptionIndex",
                table: "Votes",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.DropColumn(
                name: "Type",
                table: "Questions");
        }
    }
}
