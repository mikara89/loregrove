using Loregrove.Domain.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Loregrove.Infrastructure.Sqlite.Persistence.Configurations;

public sealed class SourceDocumentConfiguration : IEntityTypeConfiguration<SourceDocument>
{
    public void Configure(EntityTypeBuilder<SourceDocument> builder)
    {
        builder.ToTable("SourceDocuments");
        builder.HasKey(document => document.Id);
        builder.Property(document => document.Id)
            .HasConversion(id => id.Value, value => new SourceDocumentId(value))
            .ValueGeneratedNever();
        builder.Property(document => document.DisplayName).HasMaxLength(512).IsRequired();
        builder.Property(document => document.SourceKind).HasConversion<int>().IsRequired();
        builder.Property(document => document.CreatedAt).IsRequired();
        builder.Property(document => document.CurrentVersionId)
            .HasConversion(id => id.Value, value => new SourceDocumentVersionId(value))
            .IsRequired();
    }
}
