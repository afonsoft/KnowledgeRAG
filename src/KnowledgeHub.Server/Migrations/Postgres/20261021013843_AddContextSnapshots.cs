using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnowledgeHub.Server.Migrations.Postgres
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RepoKey = table.Column<string>(type: "character varying(201)", maxLength: 201, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    PayloadBytes = table.Column<int>(type: "integer", nullable: false),
                    TokenEstimate = table.Column<int>(type: "integer", nullable: false),
                    Compacted = table.Column<bool>(type: "boolean", nullable: false),
                    Strategy = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByKeyId = table.Column<Guid>(type: "uuid", nullable: true)
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
