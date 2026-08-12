using Loregrove.Domain.Sources;

namespace Loregrove.Application.Library;

public sealed record LibraryQuery(int Page = 1, int PageSize = 50, string? TextFilter = null)
{
    public static IReadOnlySet<int> AllowedPageSizes { get; } = new HashSet<int> { 25, 50, 100 };
}

public sealed record LibrarySourceListItem(
    SourceDocumentId Id,
    string DisplayName,
    string OriginalFileName,
    string? MediaType,
    long ByteLength,
    DateTimeOffset ImportedAt,
    SourceProcessingState ProcessingState);

public sealed record LibrarySourceDetails(
    SourceDocumentId Id,
    SourceDocumentVersionId VersionId,
    string DisplayName,
    string OriginalFileName,
    string? MediaType,
    long ByteLength,
    DateTimeOffset ImportedAt,
    string ContentHash,
    SourceProcessingState ProcessingState);

public sealed record LibraryPage(
    IReadOnlyList<LibrarySourceListItem> Items,
    int Page,
    int PageSize,
    int TotalCount)
{
    public int TotalPages => TotalCount == 0 ? 1 : (int)Math.Ceiling((double)TotalCount / PageSize);
}

public enum ImportItemState
{
    Queued = 0,
    Importing = 1,
    Imported = 2,
    AlreadyExists = 3,
    Failed = 4,
    Cancelled = 5,
}

public sealed record ImportProgress(
    string FileName,
    int Index,
    int Total,
    ImportItemState State,
    string? Message = null);

public sealed record ImportFileResult(
    string FileName,
    int Index,
    ImportItemState State,
    SourceDocumentId? DocumentId = null,
    SourceDocumentVersionId? VersionId = null,
    string? Message = null);

public sealed record ImportFilesResult(IReadOnlyList<ImportFileResult> Items)
{
    public int ImportedCount => Items.Count(item => item.State == ImportItemState.Imported);

    public int AlreadyExistsCount => Items.Count(item => item.State == ImportItemState.AlreadyExists);

    public int FailedCount => Items.Count(item => item.State == ImportItemState.Failed);

    public int CancelledCount => Items.Count(item => item.State == ImportItemState.Cancelled);
}
