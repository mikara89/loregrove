using Loregrove.Domain.Sources;

namespace Loregrove.Application.Sources;

/// <summary>
/// Atomic metadata boundary for one captured source.
/// </summary>
/// <remarks>
/// Implementations must atomically check exact-content uniqueness and, for a new capture, persist
/// the document, version, and processing job together. The object is already finalized before this
/// method is called. A failure can therefore leave a safe, immutable, unreferenced object for later
/// garbage collection; callers must not delete that object because another capture may reference it.
/// </remarks>
public interface ISourceDocumentRepository
{
    Task<SourceCaptureCommitResult> TryAddCaptureAsync(
        SourceDocument document,
        SourceDocumentVersion version,
        ProcessingJob processingJob,
        CancellationToken cancellationToken);
}

public sealed record SourceCaptureCommitResult(
    SourceDocumentId DocumentId,
    SourceDocumentVersionId VersionId,
    bool Created);
