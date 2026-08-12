namespace Loregrove.Domain.Sources;

public enum SourceProcessingState
{
    Captured = 0,
    PendingProcessing = 1,
}

public enum ProcessingJobState
{
    Pending = 0,
    Processing = 1,
    Completed = 2,
    Failed = 3,
}
