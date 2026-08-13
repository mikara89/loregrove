using Loregrove.Application.Persistence;
using Loregrove.Domain.Sources;
using Microsoft.EntityFrameworkCore;

namespace Loregrove.Application.Processing;

public sealed class ProcessingJobRecovery(
    ILoregroveDbContext dbContext,
    TimeProvider? timeProvider = null) : IProcessingJobRecovery
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<int> RecoverInterruptedJobsAsync(CancellationToken cancellationToken)
    {
        var recoveredAt = _timeProvider.GetUtcNow();
        await using var transaction = await dbContext.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var parsingVersionIds = await dbContext.ProcessingJobs
            .Where(job =>
                job.State == ProcessingJobState.Processing &&
                job.Stage == ProcessingStage.Parsing)
            .Select(job => job.DocumentVersionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var chunkingVersionIds = await dbContext.ProcessingJobs
            .Where(job =>
                job.State == ProcessingJobState.Processing &&
                job.Stage == ProcessingStage.Chunking)
            .Select(job => job.DocumentVersionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var chunkingVersionsWithCurrentSet = await dbContext.ChunkSets
            .Where(set => set.IsCurrent && chunkingVersionIds.Contains(set.DocumentVersionId))
            .Select(set => set.DocumentVersionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var recovered = await dbContext.ProcessingJobs
            .Where(job => job.State == ProcessingJobState.Processing)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.State, ProcessingJobState.Pending)
                    .SetProperty(job => job.LastError, (string?)null)
                    .SetProperty(job => job.UpdatedAt, recoveredAt),
                cancellationToken).ConfigureAwait(false);
        if (chunkingVersionsWithCurrentSet.Count > 0)
        {
            await dbContext.ProcessingJobs
                .Where(job => chunkingVersionsWithCurrentSet.Contains(job.DocumentVersionId))
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(job => job.Stage, ProcessingStage.Embedding),
                    cancellationToken).ConfigureAwait(false);
        }
        if (parsingVersionIds.Count > 0)
        {
            await dbContext.SourceDocumentVersions
                .Where(version => parsingVersionIds.Contains(version.Id))
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        version => version.ProcessingState,
                        SourceProcessingState.PendingProcessing),
                    cancellationToken).ConfigureAwait(false);
        }

        if (chunkingVersionIds.Count > 0)
        {
            await dbContext.SourceDocumentVersions
                .Where(version =>
                    chunkingVersionIds.Contains(version.Id) &&
                    !chunkingVersionsWithCurrentSet.Contains(version.Id))
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        version => version.ProcessingState,
                        SourceProcessingState.Parsed),
                    cancellationToken).ConfigureAwait(false);
        }

        if (chunkingVersionsWithCurrentSet.Count > 0)
        {
            await dbContext.SourceDocumentVersions
                .Where(version => chunkingVersionsWithCurrentSet.Contains(version.Id))
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        version => version.ProcessingState,
                        SourceProcessingState.Chunked),
                    cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        dbContext.ClearTrackedChanges();
        return recovered;
    }
}
