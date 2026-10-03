using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnowledgeHub.Server.Migrations.Postgres
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Slug = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    DefinitionJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentFlows", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FlowRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FlowId = table.Column<Guid>(type: "uuid", nullable: false),
                    FlowVersion = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    InputsJson = table.Column<string>(type: "text", nullable: true),
                    StepResultsJson = table.Column<string>(type: "text", nullable: true),
                    OutputJson = table.Column<string>(type: "text", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: true),
                    ApiKeyId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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
