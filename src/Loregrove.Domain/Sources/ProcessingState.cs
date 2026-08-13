namespace Loregrove.Domain.Sources;

public enum SourceProcessingState
{
    Captured = 0,
    PendingProcessing = 1,
    Parsing = 2,
    Parsed = 3,
    ParseFailed = 4,
    Chunking = 5,
    Chunked = 6,
}

public enum ProcessingJobState
{
    Pending = 0,
    Processing = 1,
    Completed = 2,
    Failed = 3,
}

public enum ProcessingStage
{
    Parsing = 0,
    Chunking = 1,
    Embedding = 2,
}
