using Loregrove.Domain.Sources;
using Microsoft.EntityFrameworkCore;

namespace Loregrove.Application.Persistence;

public interface ILoregroveDbContext
{
    DbSet<SourceDocument> SourceDocuments { get; }

    DbSet<SourceDocumentVersion> SourceDocumentVersions { get; }

    DbSet<ProcessingJob> ProcessingJobs { get; }

    DbSet<ParsedArtifact> ParsedArtifacts { get; }

    DbSet<SourceAnchor> SourceAnchors { get; }

    DbSet<ChunkSet> ChunkSets { get; }

    DbSet<Chunk> Chunks { get; }

    DbSet<ChunkEvidenceSpan> ChunkEvidenceSpans { get; }

    DbSet<LexicalSearchEntry> LexicalSearchEntries { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    Task<ILoregroveDbTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

    void ClearTrackedChanges();
}

public interface ILoregroveDbTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);

    Task RollbackAsync(CancellationToken cancellationToken);
}
