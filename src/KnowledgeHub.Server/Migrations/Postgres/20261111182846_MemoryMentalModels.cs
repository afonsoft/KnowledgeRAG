using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnowledgeHub.Server.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class MemoryMentalModels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MemoryMentalModels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BankId = table.Column<Guid>(type: "uuid", nullable: false),
                    Subtype = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    SourceQuery = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    TriggerJson = table.Column<string>(type: "text", nullable: true),
                    Content = table.Column<string>(type: "text", nullable: false),
                    StructuredContentJson = table.Column<string>(type: "text", nullable: true),
                    LastRefreshedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastMemorySeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastRefreshError = table.Column<string>(type: "text", nullable: true),
                    Tags = table.Column<List<string>>(type: "text[]", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryMentalModels", x => x.Id);
                    table.CheckConstraint("CK_MentalModel_Subtype", "\"Subtype\" IN ('mental_model','page')");
                    table.ForeignKey(
                        name: "FK_MemoryMentalModels_MemoryBanks_BankId",
                        column: x => x.BankId,
                        principalTable: "MemoryBanks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MentalModelHistories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ModelId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    ManualEdit = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MentalModelHistories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MentalModelHistories_MemoryMentalModels_ModelId",
                        column: x => x.ModelId,
                        principalTable: "MemoryMentalModels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MemoryMentalModels_BankId_Path_Name",
                table: "MemoryMentalModels",
                columns: new[] { "BankId", "Path", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MemoryMentalModels_BankId_Subtype",
                table: "MemoryMentalModels",
                columns: new[] { "BankId", "Subtype" });

            migrationBuilder.CreateIndex(
                name: "IX_MentalModelHistories_ModelId_Version",
                table: "MentalModelHistories",
                columns: new[] { "ModelId", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MentalModelHistories");

            migrationBuilder.DropTable(
                name: "MemoryMentalModels");
        }
    }
}
