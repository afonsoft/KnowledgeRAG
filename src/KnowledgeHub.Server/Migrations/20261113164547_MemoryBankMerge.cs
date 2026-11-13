using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnowledgeHub.Server.Migrations
{
    /// <inheritdoc />
    public partial class MemoryBankMerge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "MergedIntoBankId",
                table: "MemoryBanks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MemoryBanks_MergedIntoBankId",
                table: "MemoryBanks",
                column: "MergedIntoBankId");

            migrationBuilder.AddForeignKey(
                name: "FK_MemoryBanks_MemoryBanks_MergedIntoBankId",
                table: "MemoryBanks",
                column: "MergedIntoBankId",
                principalTable: "MemoryBanks",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MemoryBanks_MemoryBanks_MergedIntoBankId",
                table: "MemoryBanks");

            migrationBuilder.DropIndex(
                name: "IX_MemoryBanks_MergedIntoBankId",
                table: "MemoryBanks");

            migrationBuilder.DropColumn(
                name: "MergedIntoBankId",
                table: "MemoryBanks");
        }
    }
}
