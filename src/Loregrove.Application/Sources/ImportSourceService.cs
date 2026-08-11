using Loregrove.Application.Storage;
using Loregrove.Domain.Sources;

namespace Loregrove.Application.Sources;

/// <summary>
/// Captures an original source before recording metadata or requesting processing.
/// </summary>
public sealed class ImportSourceService(
    IObjectStore objectStore,
    ISourceDocumentRepository repository,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

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

        var commit = await repository.TryAddCaptureAsync(
            document,
            version,
            processingJob,
            cancellationToken).ConfigureAwait(false);

        return new ImportSourceResult(
            commit.DocumentId,
            commit.VersionId,
            storedObject.ContentHash,
            commit.Created ? ImportDisposition.Created : ImportDisposition.AlreadyExists);
    }
}
