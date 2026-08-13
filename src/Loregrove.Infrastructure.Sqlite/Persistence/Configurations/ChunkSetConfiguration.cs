using Loregrove.Domain.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Loregrove.Infrastructure.Sqlite.Persistence.Configurations;

public sealed class ChunkSetConfiguration : IEntityTypeConfiguration<ChunkSet>
{
    public void Configure(EntityTypeBuilder<ChunkSet> builder)
    {
        builder.ToTable("ChunkSets");
        builder.HasKey(set => set.Id);
        builder.HasAlternateKey(set => new { set.Id, set.ParsedArtifactId, set.DocumentVersionId });
        builder.Property(set => set.Id)
            .HasConversion(id => id.Value, value => new ChunkSetId(value))
            .ValueGeneratedNever();
        builder.Property(set => set.DocumentVersionId)
            .HasConversion(id => id.Value, value => new SourceDocumentVersionId(value));
        builder.Property(set => set.ParsedArtifactId)
            .HasConversion(id => id.Value, value => new ParsedArtifactId(value));
        builder.Property(set => set.ChunkerId).HasMaxLength(128).IsRequired();
        builder.Property(set => set.ChunkerVersion).HasMaxLength(64).IsRequired();
        builder.Property(set => set.ChunkSchemaVersion).IsRequired();
        builder.Property(set => set.ConfigurationFingerprint).HasMaxLength(64).IsRequired();
        builder.Property(set => set.ChunkerFingerprint).HasMaxLength(64).IsRequired();
        builder.Property(set => set.CreatedAt).IsRequired();
        builder.Property(set => set.ChunkCount).IsRequired();
        builder.Property(set => set.IsCurrent).IsRequired();
        builder.HasIndex(set => new { set.ParsedArtifactId, set.ChunkerFingerprint }).IsUnique();
        builder.HasIndex(set => set.DocumentVersionId)
            .IsUnique()
            .HasFilter("IsCurrent = 1")
            .HasDatabaseName("IX_ChunkSets_CurrentDocumentVersionId");
        builder.HasOne<ParsedArtifact>()
            .WithMany()
            .HasForeignKey(set => new { set.ParsedArtifactId, set.DocumentVersionId })
            .HasPrincipalKey(artifact => new { artifact.Id, artifact.DocumentVersionId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
