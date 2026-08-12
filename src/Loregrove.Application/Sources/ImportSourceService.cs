using Loregrove.Application.Persistence;
using Loregrove.Application.Storage;
using Loregrove.Domain.Sources;
using Microsoft.EntityFrameworkCore;

namespace Loregrove.Application.Sources;

/// <summary>
/// Captures an original source before recording metadata or requesting processing.
/// </summary>
public sealed class ImportSourceService(
    IObjectStore objectStore,
    ILoregroveDbContext dbContext,
    IDatabaseExceptionClassifier databaseExceptionClassifier,
    TimeProvider? timeProvider = null,
    IImportTransactionHook? transactionHook = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IImportTransactionHook _transactionHook = transactionHook ?? NoOpImportTransactionHook.Instance;

    public async Task<ImportSourceResult> ImportAsync(
        ImportSourceCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Content);

        if (string.IsNullOrWhiteSpace(command.DisplayName))
        {
            throw new ArgumentException("A display name is required.", nameof(command));
        }

        if (string.IsNullOrEmpty(command.OriginalFileName))
        {
            throw new ArgumentException("An original file name is required.", nameof(command));
        }

        if (!command.Content.CanRead)
        {
            throw new ArgumentException("The source content stream must be readable.", nameof(command));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var storedObject = await objectStore.StoreAsync(command.Content, cancellationToken).ConfigureAwait(false);

        // A cancelled or failed metadata commit deliberately leaves the immutable object in place.
        cancellationToken.ThrowIfCancellationRequested();
        var existing = await FindExistingAsync(storedObject.ContentHash, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return ExistingResult(existing, storedObject.ContentHash);
        }

        var capturedAt = _timeProvider.GetUtcNow();
        var documentId = SourceDocumentId.New();
        var versionId = SourceDocumentVersionId.New();
        var document = new SourceDocument(
            documentId,
            command.DisplayName,
            SourceKind.File,
            capturedAt,
            versionId);
        var version = new SourceDocumentVersion(
            versionId,
            documentId,
            storedObject.ContentHash,
            command.OriginalFileName,
            command.MediaType,
            storedObject.ByteLength,
            capturedAt,
            storedObject.ObjectKey,
            previousVersionId: null,
            SourceProcessingState.PendingProcessing);
        var processingJob = new ProcessingJob(
            ProcessingJobId.New(),
            versionId,
            ProcessingJobState.Pending,
            capturedAt,
            attemptCount: 0);

        await using var transaction = await dbContext.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            dbContext.SourceDocuments.Add(document);
            await _transactionHook.OnStageAsync(
                ImportTransactionStage.AfterDocumentAdded,
                cancellationToken).ConfigureAwait(false);
            dbContext.SourceDocumentVersions.Add(version);
            await _transactionHook.OnStageAsync(
                ImportTransactionStage.AfterVersionAdded,
                cancellationToken).ConfigureAwait(false);
            await _transactionHook.OnStageAsync(
                ImportTransactionStage.BeforeProcessingJobAdded,
                cancellationToken).ConfigureAwait(false);
            dbContext.ProcessingJobs.Add(processingJob);

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await _transactionHook.OnStageAsync(
                ImportTransactionStage.BeforeCommit,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            dbContext.ClearTrackedChanges();

            return new ImportSourceResult(
                document.Id,
                version.Id,
                storedObject.ContentHash,
                ImportDisposition.Created);
        }
        catch (DbUpdateException exception)
            when (databaseExceptionClassifier.IsUniqueConstraintViolation(exception))
        {
            await RollbackAndClearAsync(transaction).ConfigureAwait(false);
            existing = await FindExistingAsync(storedObject.ContentHash, CancellationToken.None).ConfigureAwait(false);
            if (existing is null)
            {
                throw;
            }

            return ExistingResult(existing, storedObject.ContentHash);
        }
        catch
        {
            await RollbackAndClearAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    private async Task RollbackAndClearAsync(ILoregroveDbTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            dbContext.ClearTrackedChanges();
        }
    }

    private async Task<SourceDocumentVersion?> FindExistingAsync(
        string contentHash,
        CancellationToken cancellationToken) =>
        await dbContext.SourceDocumentVersions
            .AsNoTracking()
            .SingleOrDefaultAsync(version => version.ContentHash == contentHash, cancellationToken)
            .ConfigureAwait(false);

    private static ImportSourceResult ExistingResult(SourceDocumentVersion existing, string contentHash) =>
        new(existing.DocumentId, existing.Id, contentHash, ImportDisposition.AlreadyExists);
}
