namespace Loregrove.Domain.Sources;

public sealed class ProcessingJob
{
    public ProcessingJob(
        ProcessingJobId id,
        SourceDocumentVersionId documentVersionId,
        ProcessingJobState state,
        DateTimeOffset createdAt,
        int attemptCount)
    {
        if (id.Value == Guid.Empty)
        {
            throw new ArgumentException("A processing job id is required.", nameof(id));
        }

        if (documentVersionId.Value == Guid.Empty)
        {
            throw new ArgumentException("A source document version id is required.", nameof(documentVersionId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(attemptCount);

        Id = id;
        DocumentVersionId = documentVersionId;
        State = state;
        CreatedAt = createdAt;
        AttemptCount = attemptCount;
    }

    public ProcessingJobId Id { get; }

    public SourceDocumentVersionId DocumentVersionId { get; }

    public ProcessingJobState State { get; }

    public DateTimeOffset CreatedAt { get; }

    public int AttemptCount { get; }
}
