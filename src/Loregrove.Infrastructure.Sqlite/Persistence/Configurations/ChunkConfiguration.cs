using Loregrove.Domain.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Loregrove.Infrastructure.Sqlite.Persistence.Configurations;

public sealed class ChunkConfiguration : IEntityTypeConfiguration<Chunk>
{
    public void Configure(EntityTypeBuilder<Chunk> builder)
    {
        builder.ToTable("Chunks");
        builder.HasKey(chunk => chunk.Id);
        builder.HasAlternateKey(chunk => new { chunk.Id, chunk.ParsedArtifactId, chunk.DocumentVersionId });
        builder.Property(chunk => chunk.Id)
            .HasConversion(id => id.Value, value => new ChunkId(value))
            .ValueGeneratedNever();
        builder.Property(chunk => chunk.ChunkSetId)
            .HasConversion(id => id.Value, value => new ChunkSetId(value));
        builder.Property(chunk => chunk.DocumentVersionId)
            .HasConversion(id => id.Value, value => new SourceDocumentVersionId(value));
        builder.Property(chunk => chunk.ParsedArtifactId)
            .HasConversion(id => id.Value, value => new ParsedArtifactId(value));
        builder.Property(chunk => chunk.Ordinal).IsRequired();
        builder.Property(chunk => chunk.ChunkKey).HasMaxLength(64).IsRequired();
        builder.Property(chunk => chunk.Text).IsRequired();
        builder.Property(chunk => chunk.ContextText).IsRequired();
        builder.Property(chunk => chunk.ContentHash).HasMaxLength(64).IsRequired();
        builder.Property(chunk => chunk.CharacterLength).IsRequired();
        builder.HasIndex(chunk => new { chunk.ChunkSetId, chunk.Ordinal }).IsUnique();
        builder.HasIndex(chunk => chunk.ChunkKey).IsUnique();
        builder.HasIndex(chunk => new { chunk.ParsedArtifactId, chunk.DocumentVersionId });
        builder.HasOne<ChunkSet>()
            .WithMany()
            .HasForeignKey(chunk => new { chunk.ChunkSetId, chunk.ParsedArtifactId, chunk.DocumentVersionId })
            .HasPrincipalKey(set => new { set.Id, set.ParsedArtifactId, set.DocumentVersionId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
