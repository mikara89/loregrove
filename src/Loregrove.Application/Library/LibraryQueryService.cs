using Loregrove.Application.Persistence;
using Loregrove.Domain.Sources;
using Microsoft.EntityFrameworkCore;

namespace Loregrove.Application.Library;

public sealed class LibraryQueryService(ILoregroveDbContext dbContext)
{
    public async Task<LibraryPage> GetSourcesAsync(
        LibraryQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Page < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "Page must be at least one.");
        }

        if (!LibraryQuery.AllowedPageSizes.Contains(query.PageSize))
        {
            throw new ArgumentOutOfRangeException(nameof(query), "Page size must be 25, 50, or 100.");
        }

        var rows = CurrentVersions();
        var filter = query.TextFilter?.Trim();
        if (!string.IsNullOrEmpty(filter))
        {
            var pattern = $"%{EscapeLikePattern(filter)}%";
            rows = rows.Where(row =>
                EF.Functions.Like(row.DisplayName, pattern, "\\") ||
                EF.Functions.Like(row.OriginalFileName, pattern, "\\"));
        }

        var totalCount = await rows.CountAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1305 // Provider-side ToString orders SQLite's UTC ISO text value.
        var items = await rows
            // SQLite stores the UTC capture value in an ISO-sortable representation but does not
            // support ordering a DateTimeOffset expression directly. All imports use GetUtcNow.
            .OrderByDescending(row => row.ImportedAt.ToString())
            .ThenByDescending(row => row.DocumentId)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(row => new LibrarySourceListItem(
                row.DocumentId,
                row.DisplayName,
                row.OriginalFileName,
                row.MediaType,
                row.ByteLength,
                row.ImportedAt,
                row.ProcessingState))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
#pragma warning restore CA1305

        return new LibraryPage(items, query.Page, query.PageSize, totalCount);
    }

    public Task<LibrarySourceDetails?> GetSourceAsync(
        SourceDocumentId documentId,
        CancellationToken cancellationToken)
    {
        if (documentId.Value == Guid.Empty)
        {
            throw new ArgumentException("A source document id is required.", nameof(documentId));
        }

        return CurrentVersions()
            .Where(row => row.DocumentId == documentId)
            .Select(row => new LibrarySourceDetails(
                row.DocumentId,
                row.VersionId,
                row.DisplayName,
                row.OriginalFileName,
                row.MediaType,
                row.ByteLength,
                row.ImportedAt,
                row.ContentHash,
                row.ProcessingState))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private IQueryable<CurrentSourceRow> CurrentVersions() =>
        from document in dbContext.SourceDocuments.AsNoTracking()
        join version in dbContext.SourceDocumentVersions.AsNoTracking()
            on document.CurrentVersionId equals version.Id
        select new CurrentSourceRow
        {
            DocumentId = document.Id,
            VersionId = version.Id,
            DisplayName = document.DisplayName,
            OriginalFileName = version.OriginalFileName,
            MediaType = version.MediaType,
            ByteLength = version.ByteLength,
            ImportedAt = version.ImportedAt,
            ContentHash = version.ContentHash,
            ProcessingState = version.ProcessingState,
        };

    private static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private sealed class CurrentSourceRow
    {
        public SourceDocumentId DocumentId { get; init; }

        public SourceDocumentVersionId VersionId { get; init; }

        public required string DisplayName { get; init; }

        public required string OriginalFileName { get; init; }

        public string? MediaType { get; init; }

        public long ByteLength { get; init; }

        public DateTimeOffset ImportedAt { get; init; }

        public required string ContentHash { get; init; }

        public SourceProcessingState ProcessingState { get; init; }
    }
}
