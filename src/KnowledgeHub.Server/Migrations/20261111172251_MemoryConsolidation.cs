using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnowledgeHub.Server.Migrations
{
    /// <inheritdoc />
    public partial class MemoryConsolidation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ProofCount",
                table: "MemoryUnits",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SourceMemoryIdsJson",
                table: "MemoryUnits",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ObservationHistories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ObservationUnitId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BankId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    AddedSourceIdsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ProofCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ObservationHistories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ObservationHistories_MemoryUnits_ObservationUnitId",
                        column: x => x.ObservationUnitId,
                        principalTable: "MemoryUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ObservationHistories_BankId",
                table: "ObservationHistories",
                column: "BankId");

            migrationBuilder.CreateIndex(
                name: "IX_ObservationHistories_ObservationUnitId_Version",
                table: "ObservationHistories",
                columns: new[] { "ObservationUnitId", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ObservationHistories");

            migrationBuilder.DropColumn(
                name: "ProofCount",
                table: "MemoryUnits");

            migrationBuilder.DropColumn(
                name: "SourceMemoryIdsJson",
                table: "MemoryUnits");
        }
    }
}
