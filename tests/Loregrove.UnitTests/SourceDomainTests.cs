using Loregrove.Application.Sources;
using Loregrove.Domain.Sources;

namespace Loregrove.UnitTests;

public sealed class SourceDomainTests
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void SourceIdentifiersAreStronglyTypedValues()
    {
        var value = Guid.NewGuid();

        var documentId = new SourceDocumentId(value);
        var versionId = new SourceDocumentVersionId(value);
        var jobId = new ProcessingJobId(value);

        Assert.Equal(value, documentId.Value);
        Assert.Equal(value, versionId.Value);
        Assert.Equal(value, jobId.Value);
        Assert.IsType<SourceDocumentId>(documentId);
        Assert.IsType<SourceDocumentVersionId>(versionId);
        Assert.IsType<ProcessingJobId>(jobId);
    }

    [Fact]
    public void SourceRequiresIdentityAndDisplayName()
    {
        var versionId = SourceDocumentVersionId.New();

        Assert.Throws<ArgumentException>(() => new SourceDocument(
            default,
            "source",
            SourceKind.File,
            DateTimeOffset.UtcNow,
            versionId));
        Assert.Throws<ArgumentException>(() => new SourceDocument(
            SourceDocumentId.New(),
            " ",
            SourceKind.File,
            DateTimeOffset.UtcNow,
            versionId));
    }

    [Fact]
    public void VersionPreservesImmutableSourceIdentityAndAllowsFuturePredecessor()
    {
        var documentId = SourceDocumentId.New();
        var priorVersionId = SourceDocumentVersionId.New();
        var version = new SourceDocumentVersion(
            SourceDocumentVersionId.New(),
            documentId,
            Hash,
            "../untrusted.pdf",
            "application/pdf",
            42,
            DateTimeOffset.UtcNow,
            $"aa/{Hash}",
            priorVersionId,
            SourceProcessingState.Captured);

        Assert.Equal(documentId, version.DocumentId);
        Assert.Equal("../untrusted.pdf", version.OriginalFileName);
        Assert.Equal(priorVersionId, version.PreviousVersionId);
        Assert.Equal(SourceProcessingState.Captured, version.ProcessingState);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void VersionRejectsInvalidSha256Identity(string contentHash)
    {
        Assert.Throws<ArgumentException>(() => CreateVersion(contentHash, 1));
    }

    [Fact]
    public void VersionRejectsNegativeByteLength()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateVersion(Hash, -1));
    }

    [Fact]
    public void PendingJobStartsWithoutAttempts()
    {
        var versionId = SourceDocumentVersionId.New();
        var job = new ProcessingJob(
            ProcessingJobId.New(),
            versionId,
            ProcessingJobState.Pending,
            DateTimeOffset.UtcNow,
            0);

        Assert.Equal(versionId, job.DocumentVersionId);
        Assert.Equal(ProcessingJobState.Pending, job.State);
        Assert.Equal(0, job.AttemptCount);
        Assert.Contains(SourceProcessingState.PendingProcessing, Enum.GetValues<SourceProcessingState>());
    }

    [Fact]
    public void InterruptedJobRecoveryReturnsProcessingToPendingWithoutAddingAttempt()
    {
        var recoveredAt = DateTimeOffset.UtcNow;
        var job = new ProcessingJob(
            ProcessingJobId.New(),
            SourceDocumentVersionId.New(),
            ProcessingJobState.Processing,
            recoveredAt.AddMinutes(-1),
            attemptCount: 2);

        Assert.True(job.RecoverInterrupted(recoveredAt));
        Assert.Equal(ProcessingJobState.Pending, job.State);
        Assert.Equal(2, job.AttemptCount);
        Assert.Equal(recoveredAt, job.UpdatedAt);
        Assert.Null(job.LastError);
    }

    [Fact]
    public void RecoveryDoesNotChangeFinishedJob()
    {
        var job = new ProcessingJob(
            ProcessingJobId.New(),
            SourceDocumentVersionId.New(),
            ProcessingJobState.Completed,
            DateTimeOffset.UtcNow,
            attemptCount: 1);

        Assert.False(job.RecoverInterrupted(DateTimeOffset.UtcNow));
        Assert.Equal(ProcessingJobState.Completed, job.State);
        Assert.Null(job.UpdatedAt);
    }

    [Theory]
    [InlineData(ImportDisposition.Created)]
    [InlineData(ImportDisposition.AlreadyExists)]
    public void ImportResultHasExplicitDisposition(ImportDisposition disposition)
    {
        var result = new ImportSourceResult(
            SourceDocumentId.New(),
            SourceDocumentVersionId.New(),
            Hash,
            disposition);

        Assert.Equal(disposition, result.Disposition);
    }

    private static SourceDocumentVersion CreateVersion(string hash, long byteLength) => new(
        SourceDocumentVersionId.New(),
        SourceDocumentId.New(),
        hash,
        "source.bin",
        null,
        byteLength,
        DateTimeOffset.UtcNow,
        "object-key",
        null,
        SourceProcessingState.Captured);
}
