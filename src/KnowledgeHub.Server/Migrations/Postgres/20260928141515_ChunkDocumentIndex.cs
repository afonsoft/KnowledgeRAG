using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnowledgeHub.Server.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class ChunkDocumentIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Chunks_KnowledgeDocumentId",
                table: "Chunks");

            migrationBuilder.CreateIndex(
                name: "IX_Chunks_KnowledgeDocumentId_ChunkIndex",
                table: "Chunks",
                columns: new[] { "KnowledgeDocumentId", "ChunkIndex" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Chunks_KnowledgeDocumentId_ChunkIndex",
                table: "Chunks");

            migrationBuilder.CreateIndex(
                name: "IX_Chunks_KnowledgeDocumentId",
                table: "Chunks",
                column: "KnowledgeDocumentId");
        }
    }
}
