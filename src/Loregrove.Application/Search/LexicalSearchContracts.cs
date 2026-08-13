using Loregrove.Domain.Sources;

namespace Loregrove.Application.Search;

public enum SearchTargetKind
{
    Source = 0,
    Chunk = 1,
}

public sealed record LexicalSearchQuery(string Text, int Page = 1, int PageSize = 25)
{
    public static IReadOnlySet<int> AllowedPageSizes { get; } = new HashSet<int> { 10, 25, 50, 100 };
    public const int MaximumQueryLength = 500;
}

public sealed record LexicalSearchResult(
    SearchTargetKind Kind,
    SourceDocumentId SourceDocumentId,
    SourceDocumentVersionId DocumentVersionId,
    ChunkId? ChunkId,
    string SourceName,
    string ContextText,
    string Snippet,
    IReadOnlyList<SourceAnchorId> SourceAnchorIds);

public sealed record LexicalSearchPage(
    IReadOnlyList<LexicalSearchResult> Items,
    int Page,
    int PageSize,
    int TotalCount)
{
    public int TotalPages => TotalCount == 0 ? 1 : (int)Math.Ceiling((double)TotalCount / PageSize);
}

public interface ILexicalSearchService
{
    Task<LexicalSearchPage> SearchAsync(LexicalSearchQuery query, CancellationToken cancellationToken);
}

public interface ILexicalSearchMaintenance
{
    Task RebuildAsync(CancellationToken cancellationToken);
}
