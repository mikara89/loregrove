using Loregrove.Domain.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Loregrove.Infrastructure.Sqlite.Persistence.Configurations;

public sealed class ProcessingJobConfiguration : IEntityTypeConfiguration<ProcessingJob>
{
    public void Configure(EntityTypeBuilder<ProcessingJob> builder)
    {
        builder.ToTable("ProcessingJobs");
        builder.HasKey(job => job.Id);
        builder.Property(job => job.Id)
            .HasConversion(id => id.Value, value => new ProcessingJobId(value))
            .ValueGeneratedNever();
        builder.Property(job => job.DocumentVersionId)
            .HasConversion(id => id.Value, value => new SourceDocumentVersionId(value))
            .IsRequired();
        builder.Property(job => job.State).HasConversion<int>().IsRequired();
        builder.Property(job => job.CreatedAt).IsRequired();
        builder.Property(job => job.UpdatedAt);
        builder.Property(job => job.AttemptCount).IsRequired();
        builder.Property(job => job.LastError).HasMaxLength(2000);

        builder.HasIndex(job => job.DocumentVersionId).IsUnique();
        builder.HasIndex(job => job.State);
        builder.HasOne<SourceDocumentVersion>()
            .WithMany()
            .HasForeignKey(job => job.DocumentVersionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
