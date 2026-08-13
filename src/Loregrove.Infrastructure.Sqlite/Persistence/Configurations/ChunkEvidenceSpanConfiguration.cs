using Loregrove.Domain.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Loregrove.Infrastructure.Sqlite.Persistence.Configurations;

public sealed class ChunkEvidenceSpanConfiguration : IEntityTypeConfiguration<ChunkEvidenceSpan>
{
    public void Configure(EntityTypeBuilder<ChunkEvidenceSpan> builder)
    {
        builder.ToTable(
            "ChunkEvidenceSpans",
            table =>
            {
                table.HasCheckConstraint("CK_ChunkEvidenceSpans_Anchor", "AnchorStart >= 0 AND AnchorEnd > AnchorStart");
                table.HasCheckConstraint("CK_ChunkEvidenceSpans_Chunk", "ChunkStart >= 0 AND ChunkEnd > ChunkStart");
            });
        builder.HasKey(span => new { span.ChunkId, span.Ordinal });
        builder.Property(span => span.ChunkId)
            .HasConversion(id => id.Value, value => new ChunkId(value));
        builder.Property(span => span.SourceAnchorId)
            .HasConversion(id => id.Value, value => new SourceAnchorId(value));
        builder.Property(span => span.ParsedArtifactId)
            .HasConversion(id => id.Value, value => new ParsedArtifactId(value));
        builder.Property(span => span.DocumentVersionId)
            .HasConversion(id => id.Value, value => new SourceDocumentVersionId(value));
        builder.Property(span => span.Ordinal).IsRequired();
        builder.Property(span => span.AnchorStart).IsRequired();
        builder.Property(span => span.AnchorEnd).IsRequired();
        builder.Property(span => span.ChunkStart).IsRequired();
        builder.Property(span => span.ChunkEnd).IsRequired();
        builder.HasIndex(span => new { span.SourceAnchorId, span.ParsedArtifactId, span.DocumentVersionId });
        builder.HasOne<Chunk>()
            .WithMany()
            .HasForeignKey(span => new { span.ChunkId, span.ParsedArtifactId, span.DocumentVersionId })
            .HasPrincipalKey(chunk => new { chunk.Id, chunk.ParsedArtifactId, chunk.DocumentVersionId })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<SourceAnchor>()
            .WithMany()
            .HasForeignKey(span => new { span.SourceAnchorId, span.ParsedArtifactId, span.DocumentVersionId })
            .HasPrincipalKey(anchor => new { anchor.Id, anchor.ParsedArtifactId, anchor.DocumentVersionId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
