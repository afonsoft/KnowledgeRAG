using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnowledgeHub.Server.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddA2aTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "A2aTasks",
                columns: table => new
                {
                    TaskId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ContextId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    State = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    TaskJson = table.Column<string>(type: "text", nullable: false),
                    PushConfigsJson = table.Column<string>(type: "text", nullable: true),
                    PushDispatched = table.Column<bool>(type: "boolean", nullable: false),
                    CallerKeyId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastUpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_A2aTasks", x => x.TaskId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_A2aTasks_ContextId",
                table: "A2aTasks",
                column: "ContextId");

            migrationBuilder.CreateIndex(
                name: "IX_A2aTasks_LastUpdatedAt",
                table: "A2aTasks",
                column: "LastUpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_A2aTasks_State",
                table: "A2aTasks",
                column: "State");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "A2aTasks");
        }
    }
}
