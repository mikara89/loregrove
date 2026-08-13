using Loregrove.Application.Persistence;
using Loregrove.Domain.Sources;
using Microsoft.EntityFrameworkCore;

namespace Loregrove.Application.Chunking;

public sealed class ChunkSourceService(
    ILoregroveDbContext dbContext,
    ChunkingDocumentReader documentReader,
    IChunker chunker,
    IDatabaseExceptionClassifier exceptionClassifier,
    IChunkTransactionHook? transactionHook = null,
    TimeProvider? timeProvider = null)
{
    private readonly IChunkTransactionHook _transactionHook = transactionHook ?? NoOpChunkTransactionHook.Instance;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public Task<ChunkSourceResult> ChunkAsync(
        SourceDocumentVersionId versionId,
        CancellationToken cancellationToken) =>
        ChunkCoreAsync(versionId, ChunkOperation.Chunk, cancellationToken);

    public Task<ChunkSourceResult> RetryAsync(
        SourceDocumentVersionId versionId,
        CancellationToken cancellationToken) =>
        ChunkCoreAsync(versionId, ChunkOperation.Retry, cancellationToken);

    public Task<ChunkSourceResult> RechunkAsync(
        SourceDocumentVersionId versionId,
        CancellationToken cancellationToken) =>
        ChunkCoreAsync(versionId, ChunkOperation.Rechunk, cancellationToken);

    private async Task<ChunkSourceResult> ChunkCoreAsync(
        SourceDocumentVersionId versionId,
        ChunkOperation operation,
        CancellationToken cancellationToken)
    {
        if (versionId.Value == Guid.Empty)
        {
            throw new ArgumentException("A source document version id is required.", nameof(versionId));
        }

        var artifactId = await dbContext.ParsedArtifacts.AsNoTracking()
            .Where(item => item.DocumentVersionId == versionId && item.IsCurrent)
            .Select(item => (ParsedArtifactId?)item.Id)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (artifactId is null)
        {
            var sourceExists = await dbContext.SourceDocumentVersions.AsNoTracking()
                .AnyAsync(item => item.Id == versionId, cancellationToken)
                .ConfigureAwait(false);
            return new ChunkSourceResult(sourceExists ? ChunkSourceDisposition.NotReady : ChunkSourceDisposition.NotFound);
        }

        var existing = await FindExistingAsync(artifactId.Value, cancellationToken).ConfigureAwait(false);
        if (existing is { } existingSet)
        {
            return new ChunkSourceResult(ChunkSourceDisposition.AlreadyChunked, existingSet.Id, existingSet.ChunkCount);
        }

        if (!await TryClaimAsync(versionId, artifactId.Value, operation, cancellationToken).ConfigureAwait(false))
        {
            return await ResolveUnclaimedAsync(versionId, artifactId.Value, operation, cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            var document = await documentReader.ReadAsync(versionId, cancellationToken).ConfigureAwait(false);
            var candidates = chunker.Chunk(document, cancellationToken);
            ChunkCandidateValidator.Validate(document, candidates, chunker.Descriptor);
            await _transactionHook.OnStageAsync(ChunkTransactionStage.AfterChunkGeneration, cancellationToken)
                .ConfigureAwait(false);
            return await CommitAsync(document, candidates, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReturnToPendingAsync(versionId).ConfigureAwait(false);
            return new ChunkSourceResult(ChunkSourceDisposition.Cancelled);
        }
        catch (Exception exception)
        {
            if (exceptionClassifier.IsUniqueConstraintViolation(exception))
            {
                dbContext.ClearTrackedChanges();
                var raced = await FindExistingAsync(artifactId.Value, CancellationToken.None).ConfigureAwait(false);
                if (raced is { } racedSet)
                {
                    return new ChunkSourceResult(ChunkSourceDisposition.AlreadyChunked, racedSet.Id, racedSet.ChunkCount);
                }
            }

            const string message = "The parsed evidence could not be chunked safely.";
            await MarkFailedAsync(versionId, message).ConfigureAwait(false);
            return new ChunkSourceResult(ChunkSourceDisposition.Failed, Message: message);
        }
    }

    private async Task<bool> TryClaimAsync(
        SourceDocumentVersionId versionId,
        ParsedArtifactId artifactId,
        ChunkOperation operation,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        await using var transaction = await dbContext.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var candidates = dbContext.ProcessingJobs.Where(job => job.DocumentVersionId == versionId);
            candidates = operation switch
            {
                ChunkOperation.Chunk => candidates.Where(job =>
                    job.Stage == ProcessingStage.Chunking && job.State == ProcessingJobState.Pending),
                ChunkOperation.Retry => candidates.Where(job =>
                    job.Stage == ProcessingStage.Chunking && job.State == ProcessingJobState.Failed),
                ChunkOperation.Rechunk => candidates.Where(job =>
                    job.Stage == ProcessingStage.Embedding &&
                    job.State == ProcessingJobState.Pending &&
                    dbContext.ChunkSets.Any(set =>
                        set.DocumentVersionId == versionId &&
                        set.IsCurrent &&
                        set.ChunkerFingerprint != chunker.Descriptor.Fingerprint)),
                _ => throw new ArgumentOutOfRangeException(nameof(operation)),
            };
            candidates = candidates.Where(_ => !dbContext.ChunkSets.Any(set =>
                set.ParsedArtifactId == artifactId && set.ChunkerFingerprint == chunker.Descriptor.Fingerprint));
            var changed = await candidates.ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.State, ProcessingJobState.Processing)
                    .SetProperty(job => job.Stage, ProcessingStage.Chunking)
                    .SetProperty(job => job.AttemptCount, job => job.AttemptCount + 1)
                    .SetProperty(job => job.UpdatedAt, now)
                    .SetProperty(job => job.LastError, (string?)null),
                cancellationToken).ConfigureAwait(false);
            if (changed == 0)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            var sourceChanged = await dbContext.SourceDocumentVersions
                .Where(version =>
                    version.Id == versionId &&
                    version.ProcessingState == (operation == ChunkOperation.Rechunk
                        ? SourceProcessingState.Chunked
                        : SourceProcessingState.Parsed))
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(version => version.ProcessingState, SourceProcessingState.Chunking),
                    cancellationToken).ConfigureAwait(false);
            if (sourceChanged == 0)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            dbContext.ClearTrackedChanges();
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            dbContext.ClearTrackedChanges();
            throw;
        }
    }

    private async Task<ChunkSourceResult> CommitAsync(
        ChunkingDocument document,
        IReadOnlyList<ChunkCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var setId = ChunkSetId.New();
        var set = new ChunkSet(
            setId,
            document.DocumentVersionId,
            document.ParsedArtifactId,
            chunker.Descriptor.Id,
            chunker.Descriptor.Version,
            chunker.Descriptor.SchemaVersion,
            chunker.Descriptor.ConfigurationFingerprint,
            chunker.Descriptor.Fingerprint,
            now,
            candidates.Count,
            isCurrent: true);
        var chunks = new List<Chunk>(candidates.Count);
        var spans = new List<ChunkEvidenceSpan>();
        foreach (var candidate in candidates)
        {
            var chunkId = ChunkId.New();
            chunks.Add(new Chunk(
                chunkId,
                setId,
                document.DocumentVersionId,
                document.ParsedArtifactId,
                candidate.Ordinal,
                candidate.ChunkKey,
                candidate.Text,
                candidate.ContextText,
                candidate.ContentHash,
                candidate.Text.Length));
            spans.AddRange(candidate.EvidenceSpans.Select((span, ordinal) => new ChunkEvidenceSpan(
                chunkId,
                ordinal,
                span.SourceAnchorId,
                document.ParsedArtifactId,
                document.DocumentVersionId,
                span.AnchorStart,
                span.AnchorEnd,
                span.ChunkStart,
                span.ChunkEnd)));
        }

        await using var transaction = await dbContext.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await dbContext.ChunkSets
                .Where(item => item.DocumentVersionId == document.DocumentVersionId && item.IsCurrent)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(item => item.IsCurrent, false),
                    cancellationToken).ConfigureAwait(false);
            await dbContext.LexicalSearchEntries
                .Where(entry => entry.DocumentVersionId == document.DocumentVersionId)
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

            var source = await (
                from version in dbContext.SourceDocumentVersions
                join sourceDocument in dbContext.SourceDocuments on version.DocumentId equals sourceDocument.Id
                where version.Id == document.DocumentVersionId
                select new { Version = version, Document = sourceDocument })
                .SingleAsync(cancellationToken).ConfigureAwait(false);
            var job = await dbContext.ProcessingJobs
                .SingleAsync(item => item.DocumentVersionId == document.DocumentVersionId, cancellationToken)
                .ConfigureAwait(false);

            dbContext.ChunkSets.Add(set);
            dbContext.Chunks.AddRange(chunks);
            dbContext.ChunkEvidenceSpans.AddRange(spans);
            dbContext.LexicalSearchEntries.Add(new LexicalSearchEntry(
                LexicalSearchEntryKind.Source,
                source.Document.Id,
                document.DocumentVersionId,
                null,
                source.Version.OriginalFileName,
                source.Version.OriginalFileName,
                string.Empty,
                string.Empty));
            dbContext.LexicalSearchEntries.AddRange(chunks.Select(chunk => new LexicalSearchEntry(
                LexicalSearchEntryKind.Chunk,
                source.Document.Id,
                document.DocumentVersionId,
                chunk.Id,
                source.Version.OriginalFileName,
                string.Empty,
                chunk.ContextText,
                chunk.Text)));
            source.Version.MarkChunked();
            job.CompleteChunking(now);

            await _transactionHook.OnStageAsync(ChunkTransactionStage.AfterRelationalEntitiesAdded, cancellationToken)
                .ConfigureAwait(false);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await _transactionHook.OnStageAsync(ChunkTransactionStage.BeforeCommit, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            dbContext.ClearTrackedChanges();
            return new ChunkSourceResult(ChunkSourceDisposition.Chunked, setId, chunks.Count);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            dbContext.ClearTrackedChanges();
            throw;
        }
    }

    private async Task<(ChunkSetId Id, int ChunkCount)?> FindExistingAsync(
        ParsedArtifactId artifactId,
        CancellationToken cancellationToken) =>
        await dbContext.ChunkSets.AsNoTracking()
            .Where(set => set.ParsedArtifactId == artifactId && set.ChunkerFingerprint == chunker.Descriptor.Fingerprint)
            .Select(set => new ValueTuple<ChunkSetId, int>(set.Id, set.ChunkCount))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false) is var result && result.Item1.Value != Guid.Empty ? result : null;

    private async Task<ChunkSourceResult> ResolveUnclaimedAsync(
        SourceDocumentVersionId versionId,
        ParsedArtifactId artifactId,
        ChunkOperation operation,
        CancellationToken cancellationToken)
    {
        var existing = await FindExistingAsync(artifactId, cancellationToken).ConfigureAwait(false);
        if (existing is { } set)
        {
            return new ChunkSourceResult(ChunkSourceDisposition.AlreadyChunked, set.Id, set.ChunkCount);
        }

        var job = await dbContext.ProcessingJobs.AsNoTracking()
            .SingleOrDefaultAsync(item => item.DocumentVersionId == versionId, cancellationToken)
            .ConfigureAwait(false);
        if (job is null)
        {
            return new ChunkSourceResult(ChunkSourceDisposition.NotReady);
        }

        if (job.State == ProcessingJobState.Processing)
        {
            return new ChunkSourceResult(ChunkSourceDisposition.Busy);
        }

        return new ChunkSourceResult(
            ChunkSourceDisposition.NotReady,
            Message: operation switch
            {
                ChunkOperation.Retry => "The source does not have a failed chunking attempt to retry.",
                ChunkOperation.Rechunk => "The source does not have a current chunk set that requires re-chunking.",
                _ => null,
            });
    }

    private async Task MarkFailedAsync(SourceDocumentVersionId versionId, string message)
    {
        var now = _timeProvider.GetUtcNow();
        await using var transaction = await dbContext.BeginTransactionAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var version = await dbContext.SourceDocumentVersions.SingleAsync(item => item.Id == versionId)
                .ConfigureAwait(false);
            var job = await dbContext.ProcessingJobs.SingleAsync(item => item.DocumentVersionId == versionId)
                .ConfigureAwait(false);
            version.ReturnToParsed();
            job.FailChunking(now, message);
            await dbContext.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            dbContext.ClearTrackedChanges();
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            dbContext.ClearTrackedChanges();
            throw;
        }
    }

    private async Task ReturnToPendingAsync(SourceDocumentVersionId versionId)
    {
        var now = _timeProvider.GetUtcNow();
        await using var transaction = await dbContext.BeginTransactionAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await dbContext.ProcessingJobs
                .Where(job => job.DocumentVersionId == versionId &&
                              job.State == ProcessingJobState.Processing &&
                              job.Stage == ProcessingStage.Chunking)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(job => job.State, ProcessingJobState.Pending)
                        .SetProperty(job => job.UpdatedAt, now)
                        .SetProperty(job => job.LastError, (string?)null),
                    CancellationToken.None).ConfigureAwait(false);
            await dbContext.SourceDocumentVersions
                .Where(version => version.Id == versionId && version.ProcessingState == SourceProcessingState.Chunking)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(version => version.ProcessingState, SourceProcessingState.Parsed),
                    CancellationToken.None).ConfigureAwait(false);
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            dbContext.ClearTrackedChanges();
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            dbContext.ClearTrackedChanges();
            throw;
        }
    }

    private enum ChunkOperation
    {
        Chunk = 0,
        Retry = 1,
        Rechunk = 2,
    }
}
