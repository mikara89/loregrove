using Loregrove.Domain.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Loregrove.Infrastructure.Sqlite.Persistence.Configurations;

public sealed class SourceAnchorConfiguration : IEntityTypeConfiguration<SourceAnchor>
{
    public void Configure(EntityTypeBuilder<SourceAnchor> builder)
    {
        builder.ToTable("SourceAnchors");
        builder.HasKey(anchor => anchor.Id);
        builder.HasAlternateKey(anchor => new { anchor.Id, anchor.ParsedArtifactId, anchor.DocumentVersionId });
        builder.Property(anchor => anchor.Id)
            .HasConversion(id => id.Value, value => new SourceAnchorId(value))
            .ValueGeneratedNever();
        builder.Property(anchor => anchor.ParsedArtifactId)
            .HasConversion(id => id.Value, value => new ParsedArtifactId(value))
            .IsRequired();
        builder.Property(anchor => anchor.DocumentVersionId)
            .HasConversion(id => id.Value, value => new SourceDocumentVersionId(value))
            .IsRequired();
        builder.Property(anchor => anchor.Ordinal).IsRequired();
        builder.Property(anchor => anchor.Kind).HasConversion<int>().IsRequired();
        builder.Property(anchor => anchor.LocatorKind).HasConversion<int>().IsRequired();
        builder.Property(anchor => anchor.LocatorSchemaVersion).IsRequired();
        builder.Property(anchor => anchor.LocatorJson).IsRequired();
        builder.Property(anchor => anchor.NormalizedText).IsRequired();
        builder.Property(anchor => anchor.NormalizedTextHash).HasMaxLength(64).IsRequired();

        builder.HasIndex(anchor => new { anchor.ParsedArtifactId, anchor.Ordinal }).IsUnique();
        builder.HasIndex(anchor => anchor.ParsedArtifactId);
        builder.HasIndex(anchor => anchor.DocumentVersionId);
        builder.HasOne<ParsedArtifact>()
            .WithMany()
            .HasForeignKey(anchor => new { anchor.ParsedArtifactId, anchor.DocumentVersionId })
            .HasPrincipalKey(artifact => new { artifact.Id, artifact.DocumentVersionId })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SourceDocumentVersion>()
            .WithMany()
            .HasForeignKey(anchor => anchor.DocumentVersionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
