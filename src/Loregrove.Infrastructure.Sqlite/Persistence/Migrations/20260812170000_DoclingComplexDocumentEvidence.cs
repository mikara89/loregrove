using Loregrove.Infrastructure.Sqlite.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Loregrove.Infrastructure.Sqlite.Persistence.Migrations;

[DbContext(typeof(LoregroveDbContext))]
[Migration("20260812170000_DoclingComplexDocumentEvidence")]
public sealed class DoclingComplexDocumentEvidence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "Completeness",
            table: "ParsedArtifacts",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<string>(
            name: "SafeDiagnosticCode",
            table: "ParsedArtifacts",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "WarningCount",
            table: "ParsedArtifacts",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "Completeness", table: "ParsedArtifacts");
        migrationBuilder.DropColumn(name: "SafeDiagnosticCode", table: "ParsedArtifacts");
        migrationBuilder.DropColumn(name: "WarningCount", table: "ParsedArtifacts");
    }
}
