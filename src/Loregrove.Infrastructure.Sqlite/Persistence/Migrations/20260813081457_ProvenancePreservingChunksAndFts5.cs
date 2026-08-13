using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861, IDE0161

namespace Loregrove.Infrastructure.Sqlite.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProvenancePreservingChunksAndFts5 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_SourceAnchors_Id_ParsedArtifactId_DocumentVersionId",
                table: "SourceAnchors",
                columns: new[] { "Id", "ParsedArtifactId", "DocumentVersionId" });

            migrationBuilder.CreateTable(
                name: "ChunkSets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DocumentVersionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParsedArtifactId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChunkerId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ChunkerVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ChunkSchemaVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    ConfigurationFingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ChunkerFingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ChunkCount = table.Column<int>(type: "INTEGER", nullable: false),
                    IsCurrent = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChunkSets", x => x.Id);
                    table.UniqueConstraint("AK_ChunkSets_Id_ParsedArtifactId_DocumentVersionId", x => new { x.Id, x.ParsedArtifactId, x.DocumentVersionId });
                    table.ForeignKey(
                        name: "FK_ChunkSets_ParsedArtifacts_ParsedArtifactId_DocumentVersionId",
                        columns: x => new { x.ParsedArtifactId, x.DocumentVersionId },
                        principalTable: "ParsedArtifacts",
                        principalColumns: new[] { "Id", "DocumentVersionId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Chunks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChunkSetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DocumentVersionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParsedArtifactId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    ChunkKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    ContextText = table.Column<string>(type: "TEXT", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CharacterLength = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Chunks", x => x.Id);
                    table.UniqueConstraint("AK_Chunks_Id_ParsedArtifactId_DocumentVersionId", x => new { x.Id, x.ParsedArtifactId, x.DocumentVersionId });
                    table.ForeignKey(
                        name: "FK_Chunks_ChunkSets_ChunkSetId_ParsedArtifactId_DocumentVersionId",
                        columns: x => new { x.ChunkSetId, x.ParsedArtifactId, x.DocumentVersionId },
                        principalTable: "ChunkSets",
                        principalColumns: new[] { "Id", "ParsedArtifactId", "DocumentVersionId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ChunkEvidenceSpans",
                columns: table => new
                {
                    ChunkId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceAnchorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParsedArtifactId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DocumentVersionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AnchorStart = table.Column<int>(type: "INTEGER", nullable: false),
                    AnchorEnd = table.Column<int>(type: "INTEGER", nullable: false),
                    ChunkStart = table.Column<int>(type: "INTEGER", nullable: false),
                    ChunkEnd = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChunkEvidenceSpans", x => new { x.ChunkId, x.Ordinal });
                    table.CheckConstraint("CK_ChunkEvidenceSpans_Anchor", "AnchorStart >= 0 AND AnchorEnd > AnchorStart");
                    table.CheckConstraint("CK_ChunkEvidenceSpans_Chunk", "ChunkStart >= 0 AND ChunkEnd > ChunkStart");
                    table.ForeignKey(
                        name: "FK_ChunkEvidenceSpans_Chunks_ChunkId_ParsedArtifactId_DocumentVersionId",
                        columns: x => new { x.ChunkId, x.ParsedArtifactId, x.DocumentVersionId },
                        principalTable: "Chunks",
                        principalColumns: new[] { "Id", "ParsedArtifactId", "DocumentVersionId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChunkEvidenceSpans_SourceAnchors_SourceAnchorId_ParsedArtifactId_DocumentVersionId",
                        columns: x => new { x.SourceAnchorId, x.ParsedArtifactId, x.DocumentVersionId },
                        principalTable: "SourceAnchors",
                        principalColumns: new[] { "Id", "ParsedArtifactId", "DocumentVersionId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LexicalSearchEntries",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceDocumentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DocumentVersionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChunkId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SourceName = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Heading = table.Column<string>(type: "TEXT", nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LexicalSearchEntries", x => x.RowId);
                    table.ForeignKey(
                        name: "FK_LexicalSearchEntries_Chunks_ChunkId",
                        column: x => x.ChunkId,
                        principalTable: "Chunks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LexicalSearchEntries_SourceDocumentVersions_DocumentVersionId",
                        column: x => x.DocumentVersionId,
                        principalTable: "SourceDocumentVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LexicalSearchEntries_SourceDocuments_SourceDocumentId",
                        column: x => x.SourceDocumentId,
                        principalTable: "SourceDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChunkEvidenceSpans_ChunkId_ParsedArtifactId_DocumentVersionId",
                table: "ChunkEvidenceSpans",
                columns: new[] { "ChunkId", "ParsedArtifactId", "DocumentVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_ChunkEvidenceSpans_SourceAnchorId_ParsedArtifactId_DocumentVersionId",
                table: "ChunkEvidenceSpans",
                columns: new[] { "SourceAnchorId", "ParsedArtifactId", "DocumentVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_Chunks_ChunkKey",
                table: "Chunks",
                column: "ChunkKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Chunks_ChunkSetId_Ordinal",
                table: "Chunks",
                columns: new[] { "ChunkSetId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Chunks_ChunkSetId_ParsedArtifactId_DocumentVersionId",
                table: "Chunks",
                columns: new[] { "ChunkSetId", "ParsedArtifactId", "DocumentVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_Chunks_ParsedArtifactId_DocumentVersionId",
                table: "Chunks",
                columns: new[] { "ParsedArtifactId", "DocumentVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_ChunkSets_CurrentDocumentVersionId",
                table: "ChunkSets",
                column: "DocumentVersionId",
                unique: true,
                filter: "IsCurrent = 1");

            migrationBuilder.CreateIndex(
                name: "IX_ChunkSets_ParsedArtifactId_ChunkerFingerprint",
                table: "ChunkSets",
                columns: new[] { "ParsedArtifactId", "ChunkerFingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChunkSets_ParsedArtifactId_DocumentVersionId",
                table: "ChunkSets",
                columns: new[] { "ParsedArtifactId", "DocumentVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_LexicalSearchEntries_ChunkId",
                table: "LexicalSearchEntries",
                column: "ChunkId",
                unique: true,
                filter: "ChunkId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LexicalSearchEntries_DocumentVersionId",
                table: "LexicalSearchEntries",
                column: "DocumentVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_LexicalSearchEntries_SourceDocumentId",
                table: "LexicalSearchEntries",
                column: "SourceDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_LexicalSearchEntries_SourceVersion",
                table: "LexicalSearchEntries",
                columns: new[] { "DocumentVersionId", "Kind" },
                unique: true,
                filter: "Kind = 0");

            migrationBuilder.Sql(
                """
                CREATE VIRTUAL TABLE LexicalSearchFts USING fts5(
                    Title,
                    Heading,
                    Body,
                    content='LexicalSearchEntries',
                    content_rowid='RowId',
                    tokenize='unicode61 remove_diacritics 2'
                );

                CREATE TRIGGER LexicalSearchEntries_AfterInsert AFTER INSERT ON LexicalSearchEntries BEGIN
                    INSERT INTO LexicalSearchFts(rowid, Title, Heading, Body)
                    VALUES (new.RowId, new.Title, new.Heading, new.Body);
                END;

                CREATE TRIGGER LexicalSearchEntries_AfterDelete AFTER DELETE ON LexicalSearchEntries BEGIN
                    INSERT INTO LexicalSearchFts(LexicalSearchFts, rowid, Title, Heading, Body)
                    VALUES ('delete', old.RowId, old.Title, old.Heading, old.Body);
                END;

                CREATE TRIGGER LexicalSearchEntries_AfterUpdate AFTER UPDATE ON LexicalSearchEntries BEGIN
                    INSERT INTO LexicalSearchFts(LexicalSearchFts, rowid, Title, Heading, Body)
                    VALUES ('delete', old.RowId, old.Title, old.Heading, old.Body);
                    INSERT INTO LexicalSearchFts(rowid, Title, Heading, Body)
                    VALUES (new.RowId, new.Title, new.Heading, new.Body);
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS LexicalSearchEntries_AfterUpdate;
                DROP TRIGGER IF EXISTS LexicalSearchEntries_AfterDelete;
                DROP TRIGGER IF EXISTS LexicalSearchEntries_AfterInsert;
                DROP TABLE IF EXISTS LexicalSearchFts;
                """);

            migrationBuilder.DropTable(
                name: "ChunkEvidenceSpans");

            migrationBuilder.DropTable(
                name: "LexicalSearchEntries");

            migrationBuilder.DropTable(
                name: "Chunks");

            migrationBuilder.DropTable(
                name: "ChunkSets");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_SourceAnchors_Id_ParsedArtifactId_DocumentVersionId",
                table: "SourceAnchors");
        }
    }
}
#pragma warning restore CA1861, IDE0161
