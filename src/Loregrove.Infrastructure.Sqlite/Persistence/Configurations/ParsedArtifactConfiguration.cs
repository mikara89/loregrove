using Loregrove.Domain.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Loregrove.Infrastructure.Sqlite.Persistence.Configurations;

public sealed class ParsedArtifactConfiguration : IEntityTypeConfiguration<ParsedArtifact>
{
    public void Configure(EntityTypeBuilder<ParsedArtifact> builder)
    {
        builder.ToTable("ParsedArtifacts");
        builder.HasKey(artifact => artifact.Id);
        builder.HasAlternateKey(artifact => new { artifact.Id, artifact.DocumentVersionId });
        builder.Property(artifact => artifact.Id)
            .HasConversion(id => id.Value, value => new ParsedArtifactId(value))
            .ValueGeneratedNever();
        builder.Property(artifact => artifact.DocumentVersionId)
            .HasConversion(id => id.Value, value => new SourceDocumentVersionId(value))
            .IsRequired();
        builder.Property(artifact => artifact.SourceContentHash).HasMaxLength(64).IsRequired();
        builder.Property(artifact => artifact.ParserId).HasMaxLength(128).IsRequired();
        builder.Property(artifact => artifact.ParserVersion).HasMaxLength(64).IsRequired();
        builder.Property(artifact => artifact.ConfigurationFingerprint).HasMaxLength(64).IsRequired();
        builder.Property(artifact => artifact.ParserFingerprint).HasMaxLength(64).IsRequired();
        builder.Property(artifact => artifact.SchemaVersion).IsRequired();
        builder.Property(artifact => artifact.ArtifactContentHash).HasMaxLength(64).IsRequired();
        builder.Property(artifact => artifact.ArtifactObjectKey).HasMaxLength(256).IsRequired();
        builder.Property(artifact => artifact.CreatedAt).IsRequired();
        builder.Property(artifact => artifact.BlockCount).IsRequired();
        builder.Property(artifact => artifact.IsCurrent).IsRequired();

        builder.HasIndex(artifact => artifact.DocumentVersionId);
        builder.HasIndex(artifact => new { artifact.DocumentVersionId, artifact.ParserFingerprint }).IsUnique();
        builder.HasIndex(artifact => artifact.DocumentVersionId)
            .IsUnique()
            .HasFilter("IsCurrent = 1")
            .HasDatabaseName("IX_ParsedArtifacts_CurrentDocumentVersionId");
        builder.HasOne<SourceDocumentVersion>()
            .WithMany()
            .HasForeignKey(artifact => artifact.DocumentVersionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
