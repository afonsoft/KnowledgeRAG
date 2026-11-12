using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnowledgeHub.Server.Migrations.Postgres
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
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SourceMemoryIdsJson",
                table: "MemoryUnits",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ObservationHistories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ObservationUnitId = table.Column<Guid>(type: "uuid", nullable: false),
                    BankId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    AddedSourceIdsJson = table.Column<string>(type: "text", nullable: false),
                    ProofCount = table.Column<int>(type: "integer", nullable: false),
                    Action = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
