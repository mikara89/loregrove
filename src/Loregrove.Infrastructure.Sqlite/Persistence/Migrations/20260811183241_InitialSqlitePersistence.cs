using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Loregrove.Infrastructure.Sqlite.Persistence.Migrations;

/// <inheritdoc />
public partial class InitialSqlitePersistence : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "SourceDocuments",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                DisplayName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                SourceKind = table.Column<int>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                CurrentVersionId = table.Column<Guid>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SourceDocuments", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "SourceDocumentVersions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                DocumentId = table.Column<Guid>(type: "TEXT", nullable: false),
                ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                OriginalFileName = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                MediaType = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                ByteLength = table.Column<long>(type: "INTEGER", nullable: false),
                ImportedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                ObjectKey = table.Column<string>(type: "TEXT", maxLength: 67, nullable: false),
                PreviousVersionId = table.Column<Guid>(type: "TEXT", nullable: true),
                ProcessingState = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SourceDocumentVersions", x => x.Id);
                table.ForeignKey(
                    name: "FK_SourceDocumentVersions_SourceDocumentVersions_PreviousVersionId",
                    column: x => x.PreviousVersionId,
                    principalTable: "SourceDocumentVersions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_SourceDocumentVersions_SourceDocuments_DocumentId",
                    column: x => x.DocumentId,
                    principalTable: "SourceDocuments",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "ProcessingJobs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                DocumentVersionId = table.Column<Guid>(type: "TEXT", nullable: false),
                State = table.Column<int>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                LastError = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProcessingJobs", x => x.Id);
                table.ForeignKey(
                    name: "FK_ProcessingJobs_SourceDocumentVersions_DocumentVersionId",
                    column: x => x.DocumentVersionId,
                    principalTable: "SourceDocumentVersions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ProcessingJobs_DocumentVersionId",
            table: "ProcessingJobs",
            column: "DocumentVersionId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ProcessingJobs_State",
            table: "ProcessingJobs",
            column: "State");

        migrationBuilder.CreateIndex(
            name: "IX_SourceDocumentVersions_ContentHash",
            table: "SourceDocumentVersions",
            column: "ContentHash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_SourceDocumentVersions_DocumentId",
            table: "SourceDocumentVersions",
            column: "DocumentId");

        migrationBuilder.CreateIndex(
            name: "IX_SourceDocumentVersions_PreviousVersionId",
            table: "SourceDocumentVersions",
            column: "PreviousVersionId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ProcessingJobs");

        migrationBuilder.DropTable(
            name: "SourceDocumentVersions");

        migrationBuilder.DropTable(
            name: "SourceDocuments");
    }
}
