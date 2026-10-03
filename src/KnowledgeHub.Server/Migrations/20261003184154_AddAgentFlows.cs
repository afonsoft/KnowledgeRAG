using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnowledgeHub.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentFlows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentFlows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Slug = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    DefinitionJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentFlows", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FlowRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FlowId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FlowVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    InputsJson = table.Column<string>(type: "TEXT", nullable: true),
                    StepResultsJson = table.Column<string>(type: "TEXT", nullable: true),
                    OutputJson = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: true),
                    ApiKeyId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlowRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FlowRuns_AgentFlows_FlowId",
                        column: x => x.FlowId,
                        principalTable: "AgentFlows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentFlows_Enabled",
                table: "AgentFlows",
                column: "Enabled");

            migrationBuilder.CreateIndex(
                name: "IX_AgentFlows_Slug",
                table: "AgentFlows",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FlowRuns_CreatedAt",
                table: "FlowRuns",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_FlowRuns_FlowId",
                table: "FlowRuns",
                column: "FlowId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FlowRuns");

            migrationBuilder.DropTable(
                name: "AgentFlows");
        }
    }
}
