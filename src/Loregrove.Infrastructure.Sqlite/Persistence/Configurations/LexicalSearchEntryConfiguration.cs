using Loregrove.Domain.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Loregrove.Infrastructure.Sqlite.Persistence.Configurations;

public sealed class LexicalSearchEntryConfiguration : IEntityTypeConfiguration<LexicalSearchEntry>
{
    public void Configure(EntityTypeBuilder<LexicalSearchEntry> builder)
    {
        var optionalChunkId = new ValueConverter<ChunkId?, Guid?>(
            id => id.HasValue ? id.Value.Value : null,
            value => value.HasValue ? new ChunkId(value.Value) : null);
        builder.ToTable("LexicalSearchEntries");
        builder.HasKey(entry => entry.RowId);
        builder.Property(entry => entry.RowId).ValueGeneratedOnAdd();
        builder.Property(entry => entry.Kind).HasConversion<int>().IsRequired();
        builder.Property(entry => entry.SourceDocumentId)
            .HasConversion(id => id.Value, value => new SourceDocumentId(value));
        builder.Property(entry => entry.DocumentVersionId)
            .HasConversion(id => id.Value, value => new SourceDocumentVersionId(value));
        builder.Property(entry => entry.ChunkId).HasConversion(optionalChunkId);
        builder.Property(entry => entry.SourceName).HasMaxLength(1024).IsRequired();
        builder.Property(entry => entry.Title).IsRequired();
        builder.Property(entry => entry.Heading).IsRequired();
        builder.Property(entry => entry.Body).IsRequired();
        builder.HasIndex(entry => entry.DocumentVersionId);
        builder.HasIndex(entry => entry.ChunkId).IsUnique().HasFilter("ChunkId IS NOT NULL");
        builder.HasIndex(entry => new { entry.DocumentVersionId, entry.Kind })
            .IsUnique()
            .HasFilter("Kind = 0")
            .HasDatabaseName("IX_LexicalSearchEntries_SourceVersion");
        builder.HasOne<SourceDocument>()
            .WithMany()
            .HasForeignKey(entry => entry.SourceDocumentId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SourceDocumentVersion>()
            .WithMany()
            .HasForeignKey(entry => entry.DocumentVersionId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Chunk>()
            .WithMany()
            .HasForeignKey(entry => entry.ChunkId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
