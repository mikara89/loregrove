using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Loregrove.Infrastructure.Sqlite.Persistence.Migrations;

/// <inheritdoc />
public partial class DurableParsedArtifactsAndSourceAnchors : Migration
{
    private static readonly string[] DocumentVersionFingerprintColumns =
        ["DocumentVersionId", "ParserFingerprint"];

    private static readonly string[] ArtifactOrdinalColumns = ["ParsedArtifactId", "Ordinal"];

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "Stage",
            table: "ProcessingJobs",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.CreateTable(
            name: "ParsedArtifacts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                DocumentVersionId = table.Column<Guid>(type: "TEXT", nullable: false),
                SourceContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                ParserId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                ParserVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                ConfigurationFingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                ParserFingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                SchemaVersion = table.Column<int>(type: "INTEGER", nullable: false),
                ArtifactContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                ArtifactObjectKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                BlockCount = table.Column<int>(type: "INTEGER", nullable: false),
                IsCurrent = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ParsedArtifacts", x => x.Id);
                table.ForeignKey(
                    name: "FK_ParsedArtifacts_SourceDocumentVersions_DocumentVersionId",
                    column: x => x.DocumentVersionId,
                    principalTable: "SourceDocumentVersions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "SourceAnchors",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                ParsedArtifactId = table.Column<Guid>(type: "TEXT", nullable: false),
                DocumentVersionId = table.Column<Guid>(type: "TEXT", nullable: false),
                Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                Kind = table.Column<int>(type: "INTEGER", nullable: false),
                LocatorKind = table.Column<int>(type: "INTEGER", nullable: false),
                LocatorSchemaVersion = table.Column<int>(type: "INTEGER", nullable: false),
                LocatorJson = table.Column<string>(type: "TEXT", nullable: false),
                NormalizedText = table.Column<string>(type: "TEXT", nullable: false),
                NormalizedTextHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SourceAnchors", x => x.Id);
                table.ForeignKey(
                    name: "FK_SourceAnchors_ParsedArtifacts_ParsedArtifactId",
                    column: x => x.ParsedArtifactId,
                    principalTable: "ParsedArtifacts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_SourceAnchors_SourceDocumentVersions_DocumentVersionId",
                    column: x => x.DocumentVersionId,
                    principalTable: "SourceDocumentVersions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ParsedArtifacts_CurrentDocumentVersionId",
            table: "ParsedArtifacts",
            column: "DocumentVersionId",
            unique: true,
            filter: "IsCurrent = 1");

        migrationBuilder.CreateIndex(
            name: "IX_ParsedArtifacts_DocumentVersionId_ParserFingerprint",
            table: "ParsedArtifacts",
            columns: DocumentVersionFingerprintColumns,
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_SourceAnchors_DocumentVersionId",
            table: "SourceAnchors",
            column: "DocumentVersionId");

        migrationBuilder.CreateIndex(
            name: "IX_SourceAnchors_ParsedArtifactId",
            table: "SourceAnchors",
            column: "ParsedArtifactId");

        migrationBuilder.CreateIndex(
            name: "IX_SourceAnchors_ParsedArtifactId_Ordinal",
            table: "SourceAnchors",
            columns: ArtifactOrdinalColumns,
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "SourceAnchors");

        migrationBuilder.DropTable(
            name: "ParsedArtifacts");

        migrationBuilder.DropColumn(
            name: "Stage",
            table: "ProcessingJobs");
    }
}
