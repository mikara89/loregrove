namespace Loregrove.Domain.Sources;

public sealed class ProcessingJob
{
    public ProcessingJob(
        ProcessingJobId id,
        SourceDocumentVersionId documentVersionId,
        ProcessingJobState state,
        DateTimeOffset createdAt,
        int attemptCount,
        DateTimeOffset? updatedAt = null,
        string? lastError = null)
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
        if (lastError?.Length > 2000)
        {
            throw new ArgumentException("The processing error must not exceed 2000 characters.", nameof(lastError));
        }

        Id = id;
        DocumentVersionId = documentVersionId;
        State = state;
        CreatedAt = createdAt;
        AttemptCount = attemptCount;
        UpdatedAt = updatedAt;
        LastError = lastError;
    }

    public ProcessingJobId Id { get; }

    public SourceDocumentVersionId DocumentVersionId { get; }

    public ProcessingJobState State { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    public int AttemptCount { get; private set; }

    public string? LastError { get; private set; }

    public bool RecoverInterrupted(DateTimeOffset recoveredAt)
    {
        if (State != ProcessingJobState.Processing)
        {
            return false;
        }

        State = ProcessingJobState.Pending;
        UpdatedAt = recoveredAt;
        return true;
    }
}
