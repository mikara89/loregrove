using Loregrove.Application.Sources;
using Loregrove.Application.Storage;
using Loregrove.Domain.Sources;

namespace Loregrove.UnitTests;

public sealed class ImportSourceServiceTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task CaptureStoresOriginalBeforeMetadataAndCreatesPendingJob()
    {
        var objectStore = new RecordingObjectStore();
        var repository = new RecordingRepository(() => objectStore.Stored);
        var service = new ImportSourceService(objectStore, repository);
        await using var content = new MemoryStream([1, 2, 3]);

        var result = await service.ImportAsync(
            new ImportSourceCommand("Evidence", "../../evidence.pdf", "application/pdf", content),
            CancellationToken.None);

        Assert.Equal(ImportDisposition.Created, result.Disposition);
        Assert.Equal(Hash, result.ContentHash);
        Assert.True(repository.CalledAfterStorage);
        Assert.NotNull(repository.Document);
        Assert.NotNull(repository.Version);
        Assert.NotNull(repository.Job);
        Assert.Equal(repository.Document.Id, result.DocumentId);
        Assert.Equal(repository.Version.Id, result.VersionId);
        Assert.Equal("../../evidence.pdf", repository.Version.OriginalFileName);
        Assert.Equal("01/012345", repository.Version.ObjectKey);
        Assert.Equal(SourceProcessingState.PendingProcessing, repository.Version.ProcessingState);
        Assert.Equal(ProcessingJobState.Pending, repository.Job.State);
        Assert.Equal(0, repository.Job.AttemptCount);
        Assert.Equal(repository.Version.Id, repository.Job.DocumentVersionId);
    }

    [Fact]
    public async Task ExactDuplicateReturnsExistingIdentity()
    {
        var existingDocumentId = SourceDocumentId.New();
        var existingVersionId = SourceDocumentVersionId.New();
        var objectStore = new RecordingObjectStore();
        var repository = new RecordingRepository(
            () => objectStore.Stored,
            new SourceCaptureCommitResult(existingDocumentId, existingVersionId, Created: false));
        var service = new ImportSourceService(objectStore, repository);

        var result = await service.ImportAsync(
            new ImportSourceCommand("Renamed", "different-name.bin", null, new MemoryStream([1])),
            CancellationToken.None);

        Assert.Equal(ImportDisposition.AlreadyExists, result.Disposition);
        Assert.Equal(existingDocumentId, result.DocumentId);
        Assert.Equal(existingVersionId, result.VersionId);
    }

    [Fact]
    public async Task CancellationDuringStorageCreatesNoMetadataOrJob()
    {
        var objectStore = new RecordingObjectStore(cancel: true);
        var repository = new RecordingRepository(() => objectStore.Stored);
        var service = new ImportSourceService(objectStore, repository);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ImportAsync(
            new ImportSourceCommand("Source", "source.bin", null, new MemoryStream([1])),
            new CancellationToken(canceled: true)));

        Assert.Null(repository.Document);
        Assert.Null(repository.Version);
        Assert.Null(repository.Job);
    }

    private sealed class RecordingObjectStore(bool cancel = false) : IObjectStore
    {
        public bool Stored { get; private set; }

        public Task<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StoredObject> StoreAsync(Stream content, CancellationToken cancellationToken)
        {
            if (cancel)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            Stored = true;
            return Task.FromResult(new StoredObject(Hash, "01/012345", 3));
        }
    }

    private sealed class RecordingRepository(
        Func<bool> storageCompleted,
        SourceCaptureCommitResult? result = null) : ISourceDocumentRepository
    {
        public bool CalledAfterStorage { get; private set; }

        public SourceDocument? Document { get; private set; }

        public SourceDocumentVersion? Version { get; private set; }

        public ProcessingJob? Job { get; private set; }

        public Task<SourceCaptureCommitResult> TryAddCaptureAsync(
            SourceDocument document,
            SourceDocumentVersion version,
            ProcessingJob processingJob,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CalledAfterStorage = storageCompleted();
            Document = document;
            Version = version;
            Job = processingJob;
            return Task.FromResult(result ?? new SourceCaptureCommitResult(document.Id, version.Id, Created: true));
        }
    }
}
