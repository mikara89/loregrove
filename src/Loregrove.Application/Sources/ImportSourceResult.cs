using Loregrove.Domain.Sources;

namespace Loregrove.Application.Sources;

public enum ImportDisposition
{
    Created = 0,
    AlreadyExists = 1,
}

public sealed record ImportSourceResult(
    SourceDocumentId DocumentId,
    SourceDocumentVersionId VersionId,
    string ContentHash,
    ImportDisposition Disposition);
