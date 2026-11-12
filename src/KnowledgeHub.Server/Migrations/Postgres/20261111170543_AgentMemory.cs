using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnowledgeHub.Server.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AgentMemory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MemoryBanks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BankKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ApiKeyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Mission = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    DispositionJson = table.Column<string>(type: "text", nullable: true),
                    ConfigJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryBanks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MemoryDirectives",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BankId = table.Column<Guid>(type: "uuid", nullable: false),
                    Content = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryDirectives", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MemoryDirectives_MemoryBanks_BankId",
                        column: x => x.BankId,
                        principalTable: "MemoryBanks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MemoryDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BankId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MetadataJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MemoryDocuments_MemoryBanks_BankId",
                        column: x => x.BankId,
                        principalTable: "MemoryBanks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MemoryOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BankId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApiKeyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    ResultJson = table.Column<string>(type: "text", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true),
                    UnitsCreated = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryOperations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MemoryOperations_MemoryBanks_BankId",
                        column: x => x.BankId,
                        principalTable: "MemoryBanks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MemoryUnits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BankId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    ChunkIndex = table.Column<int>(type: "integer", nullable: true),
                    Text = table.Column<string>(type: "text", nullable: false),
                    FactType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Context = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    EventDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OccurredStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OccurredEnd = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MentionedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Tags = table.Column<List<string>>(type: "text[]", nullable: false),
                    MetadataJson = table.Column<string>(type: "jsonb", nullable: true),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ConsolidatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConsolidationFailedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Embedding = table.Column<byte[]>(type: "bytea", nullable: true),
                    EmbeddingModel = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryUnits", x => x.Id);
                    table.CheckConstraint("CK_MemoryUnits_FactType", "\"FactType\" IN ('world','experience','observation')");
                    table.ForeignKey(
                        name: "FK_MemoryUnits_MemoryBanks_BankId",
                        column: x => x.BankId,
                        principalTable: "MemoryBanks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MemoryUnits_MemoryDocuments_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "MemoryDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "MemoryLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FromUnitId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToUnitId = table.Column<Guid>(type: "uuid", nullable: false),
                    LinkType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: true),
                    Weight = table.Column<double>(type: "double precision", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryLinks", x => x.Id);
                    table.CheckConstraint("CK_MemoryLinks_LinkType", "\"LinkType\" IN ('temporal','semantic','causal')");
                    table.ForeignKey(
                        name: "FK_MemoryLinks_MemoryUnits_FromUnitId",
                        column: x => x.FromUnitId,
                        principalTable: "MemoryUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MemoryLinks_MemoryUnits_ToUnitId",
                        column: x => x.ToUnitId,
                        principalTable: "MemoryUnits",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "MemoryUnitEntities",
                columns: table => new
                {
                    UnitId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    BankId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryUnitEntities", x => new { x.UnitId, x.EntityId });
                    table.ForeignKey(
                        name: "FK_MemoryUnitEntities_KgNodes_EntityId",
                        column: x => x.EntityId,
                        principalTable: "KgNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MemoryUnitEntities_MemoryUnits_UnitId",
                        column: x => x.UnitId,
                        principalTable: "MemoryUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MemoryBanks_BankKey_ApiKeyId",
                table: "MemoryBanks",
                columns: new[] { "BankKey", "ApiKeyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MemoryDirectives_BankId_IsActive",
                table: "MemoryDirectives",
                columns: new[] { "BankId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_MemoryDocuments_BankId_ExternalId",
                table: "MemoryDocuments",
                columns: new[] { "BankId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MemoryLinks_FromUnitId_ToUnitId_LinkType_EntityId",
                table: "MemoryLinks",
                columns: new[] { "FromUnitId", "ToUnitId", "LinkType", "EntityId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MemoryLinks_ToUnitId",
                table: "MemoryLinks",
                column: "ToUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_MemoryOperations_ApiKeyId_Status",
                table: "MemoryOperations",
                columns: new[] { "ApiKeyId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MemoryOperations_BankId_Status",
                table: "MemoryOperations",
                columns: new[] { "BankId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MemoryUnitEntities_BankId_EntityId",
                table: "MemoryUnitEntities",
                columns: new[] { "BankId", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_MemoryUnitEntities_EntityId",
                table: "MemoryUnitEntities",
                column: "EntityId");

            migrationBuilder.CreateIndex(
                name: "IX_MemoryUnits_BankId_ConsolidatedAt",
                table: "MemoryUnits",
                columns: new[] { "BankId", "ConsolidatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MemoryUnits_BankId_EventDate",
                table: "MemoryUnits",
                columns: new[] { "BankId", "EventDate" });

            migrationBuilder.CreateIndex(
                name: "IX_MemoryUnits_BankId_IsDeleted_FactType",
                table: "MemoryUnits",
                columns: new[] { "BankId", "IsDeleted", "FactType" });

            migrationBuilder.CreateIndex(
                name: "IX_MemoryUnits_DocumentId",
                table: "MemoryUnits",
                column: "DocumentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MemoryDirectives");

            migrationBuilder.DropTable(
                name: "MemoryLinks");

            migrationBuilder.DropTable(
                name: "MemoryOperations");

            migrationBuilder.DropTable(
                name: "MemoryUnitEntities");

            migrationBuilder.DropTable(
                name: "MemoryUnits");

            migrationBuilder.DropTable(
                name: "MemoryDocuments");

            migrationBuilder.DropTable(
                name: "MemoryBanks");
        }
    }
}
