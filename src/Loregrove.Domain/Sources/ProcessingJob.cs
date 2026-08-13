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
        string? lastError = null,
        ProcessingStage stage = ProcessingStage.Parsing)
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
        Stage = stage;
        CreatedAt = createdAt;
        AttemptCount = attemptCount;
        UpdatedAt = updatedAt;
        LastError = lastError;
    }

    public ProcessingJobId Id { get; }

    public SourceDocumentVersionId DocumentVersionId { get; }

    public ProcessingJobState State { get; private set; }

    public ProcessingStage Stage { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    public int AttemptCount { get; private set; }

    public string? LastError { get; private set; }

    public void CompleteParsing(DateTimeOffset completedAt)
    {
        State = ProcessingJobState.Pending;
        Stage = ProcessingStage.Chunking;
        UpdatedAt = completedAt;
        LastError = null;
    }

    public void FailParsing(DateTimeOffset failedAt, string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        if (error.Length > 2000)
        {
            throw new ArgumentException("The processing error must not exceed 2000 characters.", nameof(error));
        }

        State = ProcessingJobState.Failed;
        Stage = ProcessingStage.Parsing;
        UpdatedAt = failedAt;
        LastError = error;
    }

    public void ReturnParsingToPending(DateTimeOffset updatedAt)
    {
        State = ProcessingJobState.Pending;
        Stage = ProcessingStage.Parsing;
        UpdatedAt = updatedAt;
        LastError = null;
    }

    public void CompleteChunking(DateTimeOffset completedAt)
    {
        State = ProcessingJobState.Pending;
        Stage = ProcessingStage.Embedding;
        UpdatedAt = completedAt;
        LastError = null;
    }

    public void FailChunking(DateTimeOffset failedAt, string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        if (error.Length > 2000)
        {
            throw new ArgumentException("The processing error must not exceed 2000 characters.", nameof(error));
        }

        State = ProcessingJobState.Failed;
        Stage = ProcessingStage.Chunking;
        UpdatedAt = failedAt;
        LastError = error;
    }

    public void ReturnChunkingToPending(DateTimeOffset updatedAt)
    {
        State = ProcessingJobState.Pending;
        Stage = ProcessingStage.Chunking;
        UpdatedAt = updatedAt;
        LastError = null;
    }

    public bool RecoverInterrupted(DateTimeOffset recoveredAt)
    {
        if (State != ProcessingJobState.Processing)
        {
            return false;
        }

        State = ProcessingJobState.Pending;
        UpdatedAt = recoveredAt;
        LastError = null;
        return true;
    }
}
