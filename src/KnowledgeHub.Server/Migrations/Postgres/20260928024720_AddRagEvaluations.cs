using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnowledgeHub.Server.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddRagEvaluations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RagEvaluations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    QueryId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Question = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ContextRelevance = table.Column<double>(type: "double precision", nullable: false),
                    Groundedness = table.Column<double>(type: "double precision", nullable: false),
                    AnswerRelevance = table.Column<double>(type: "double precision", nullable: false),
                    OverallScore = table.Column<double>(type: "double precision", nullable: false),
                    TimestampUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FlaggedAsHallucination = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RagEvaluations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RagEvaluations_FlaggedAsHallucination",
                table: "RagEvaluations",
                column: "FlaggedAsHallucination");

            migrationBuilder.CreateIndex(
                name: "IX_RagEvaluations_TimestampUtc",
                table: "RagEvaluations",
                column: "TimestampUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RagEvaluations");
        }
    }
}
