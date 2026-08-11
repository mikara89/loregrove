using Loregrove.Domain.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Loregrove.Infrastructure.Sqlite.Persistence.Configurations;

public sealed class SourceDocumentVersionConfiguration : IEntityTypeConfiguration<SourceDocumentVersion>
{
    public void Configure(EntityTypeBuilder<SourceDocumentVersion> builder)
    {
        var optionalVersionIdConverter = new ValueConverter<SourceDocumentVersionId?, Guid?>(
            id => id.HasValue ? id.Value.Value : null,
            value => value.HasValue ? new SourceDocumentVersionId(value.Value) : null);

        builder.ToTable("SourceDocumentVersions");
        builder.HasKey(version => version.Id);
        builder.Property(version => version.Id)
            .HasConversion(id => id.Value, value => new SourceDocumentVersionId(value))
            .ValueGeneratedNever();
        builder.Property(version => version.DocumentId)
            .HasConversion(id => id.Value, value => new SourceDocumentId(value))
            .IsRequired();
        builder.Property(version => version.ContentHash).HasMaxLength(64).IsRequired();
        builder.Property(version => version.OriginalFileName).HasMaxLength(1024).IsRequired();
        builder.Property(version => version.MediaType).HasMaxLength(255);
        builder.Property(version => version.ByteLength).IsRequired();
        builder.Property(version => version.ImportedAt).IsRequired();
        builder.Property(version => version.ObjectKey).HasMaxLength(67).IsRequired();
        builder.Property(version => version.PreviousVersionId).HasConversion(optionalVersionIdConverter);
        builder.Property(version => version.ProcessingState).HasConversion<int>().IsRequired();

        builder.HasIndex(version => version.ContentHash).IsUnique();
        builder.HasIndex(version => version.DocumentId);

        builder.HasOne<SourceDocument>()
            .WithMany()
            .HasForeignKey(version => version.DocumentId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SourceDocumentVersion>()
            .WithMany()
            .HasForeignKey(version => version.PreviousVersionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
