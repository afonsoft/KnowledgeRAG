using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnowledgeHub.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddContextSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ContextSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RepoKey = table.Column<string>(type: "TEXT", maxLength: 201, nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadBytes = table.Column<int>(type: "INTEGER", nullable: false),
                    TokenEstimate = table.Column<int>(type: "INTEGER", nullable: false),
                    Compacted = table.Column<bool>(type: "INTEGER", nullable: false),
                    Strategy = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedByKeyId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContextSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContextSnapshots_RepoKey_CreatedAt",
                table: "ContextSnapshots",
                columns: new[] { "RepoKey", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ContextSnapshots_RepoKey_IdempotencyKey",
                table: "ContextSnapshots",
                columns: new[] { "RepoKey", "IdempotencyKey" },
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ContextSnapshots_RepoKey_Label",
                table: "ContextSnapshots",
                columns: new[] { "RepoKey", "Label" });

            migrationBuilder.CreateIndex(
                name: "IX_ContextSnapshots_RepoKey_Version",
                table: "ContextSnapshots",
                columns: new[] { "RepoKey", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContextSnapshots");
        }
    }
}
