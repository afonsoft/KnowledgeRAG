using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnowledgeHub.Server.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class MemoryPromptProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Origin",
                table: "MemoryDocuments",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "explicit");

            migrationBuilder.AddColumn<string>(
                name: "PromptHash",
                table: "MemoryDocuments",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourcePrompt",
                table: "MemoryDocuments",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceSessionId",
                table: "MemoryDocuments",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MemoryDocuments_BankId_SourceSessionId",
                table: "MemoryDocuments",
                columns: new[] { "BankId", "SourceSessionId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_MemoryDocuments_Origin",
                table: "MemoryDocuments",
                sql: "\"Origin\" IN ('explicit','passive','import','system')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MemoryDocuments_BankId_SourceSessionId",
                table: "MemoryDocuments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_MemoryDocuments_Origin",
                table: "MemoryDocuments");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "MemoryDocuments");

            migrationBuilder.DropColumn(
                name: "PromptHash",
                table: "MemoryDocuments");

            migrationBuilder.DropColumn(
                name: "SourcePrompt",
                table: "MemoryDocuments");

            migrationBuilder.DropColumn(
                name: "SourceSessionId",
                table: "MemoryDocuments");
        }
    }
}
