using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnowledgeHub.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddFlowTriggersAndApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PendingApprovalId",
                table: "FlowRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResumeStateJson",
                table: "FlowRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TriggerId",
                table: "FlowRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FlowTriggers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FlowId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Secret = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    IntervalSeconds = table.Column<int>(type: "INTEGER", nullable: true),
                    ConfigJson = table.Column<string>(type: "TEXT", nullable: true),
                    LastFiredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlowTriggers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FlowTriggers_AgentFlows_FlowId",
                        column: x => x.FlowId,
                        principalTable: "AgentFlows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FlowRuns_PendingApprovalId",
                table: "FlowRuns",
                column: "PendingApprovalId");

            migrationBuilder.CreateIndex(
                name: "IX_FlowTriggers_FlowId",
                table: "FlowTriggers",
                column: "FlowId");

            migrationBuilder.CreateIndex(
                name: "IX_FlowTriggers_Secret",
                table: "FlowTriggers",
                column: "Secret",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FlowTriggers");

            migrationBuilder.DropIndex(
                name: "IX_FlowRuns_PendingApprovalId",
                table: "FlowRuns");

            migrationBuilder.DropColumn(
                name: "PendingApprovalId",
                table: "FlowRuns");

            migrationBuilder.DropColumn(
                name: "ResumeStateJson",
                table: "FlowRuns");

            migrationBuilder.DropColumn(
                name: "TriggerId",
                table: "FlowRuns");
        }
    }
}
