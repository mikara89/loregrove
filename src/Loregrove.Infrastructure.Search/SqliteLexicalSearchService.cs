using System.Data;
using System.Globalization;
using Loregrove.Application.Search;
using Loregrove.Domain.Sources;
using Loregrove.Infrastructure.Sqlite.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Loregrove.Infrastructure.Search;

public sealed class SqliteLexicalSearchService(IDbContextFactory<LoregroveDbContext> contextFactory)
    : ILexicalSearchService, ILexicalSearchMaintenance
{
    public async Task<LexicalSearchPage> SearchAsync(
        LexicalSearchQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Page < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "Page must be at least one.");
        }

        if (!LexicalSearchQuery.AllowedPageSizes.Contains(query.PageSize))
        {
            throw new ArgumentOutOfRangeException(nameof(query), "Page size must be 10, 25, 50, or 100.");
        }

        if (query.Text.Length > LexicalSearchQuery.MaximumQueryLength)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "Search text must not exceed 500 characters.");
        }

        var match = FtsQueryCompiler.Compile(query.Text.Trim());
        if (match is null)
        {
            return new LexicalSearchPage([], query.Page, query.PageSize, 0);
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var connection = context.Database.GetDbConnection();
        var totalCount = await CountAsync(connection, match, cancellationToken).ConfigureAwait(false);
        var raw = await ReadPageAsync(connection, match, query, cancellationToken).ConfigureAwait(false);
        var chunkIds = raw.Where(item => item.ChunkId is not null).Select(item => item.ChunkId!.Value).ToArray();
        var evidence = chunkIds.Length == 0
            ? new Dictionary<ChunkId, IReadOnlyList<SourceAnchorId>>()
            : (await context.ChunkEvidenceSpans.AsNoTracking()
                    .Where(span => chunkIds.Contains(span.ChunkId))
                    .OrderBy(span => span.ChunkId)
                    .ThenBy(span => span.Ordinal)
                    .Select(span => new { span.ChunkId, span.SourceAnchorId })
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false))
                .GroupBy(item => item.ChunkId)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<SourceAnchorId>)group.Select(item => item.SourceAnchorId).Distinct().ToArray());
        var items = raw.Select(item => new LexicalSearchResult(
            item.Kind,
            item.SourceDocumentId,
            item.DocumentVersionId,
            item.ChunkId,
            item.SourceName,
            item.ContextText,
            item.Snippet,
            item.ChunkId is { } chunkId && evidence.TryGetValue(chunkId, out var anchorIds) ? anchorIds : []))
            .ToArray();
        return new LexicalSearchPage(items, query.Page, query.PageSize, totalCount);
    }

    public async Task RebuildAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO LexicalSearchFts(LexicalSearchFts) VALUES('rebuild');",
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> CountAsync(
        System.Data.Common.DbConnection connection,
        string match,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM LexicalSearchFts WHERE LexicalSearchFts MATCH $match;";
        AddParameter(command, "$match", match);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyList<RawResult>> ReadPageAsync(
        System.Data.Common.DbConnection connection,
        string match,
        LexicalSearchQuery query,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT e.Kind,
                   e.SourceDocumentId,
                   e.DocumentVersionId,
                   e.ChunkId,
                   e.SourceName,
                   e.Heading,
                   snippet(LexicalSearchFts, -1, '', '', ' … ', 24) AS Snippet,
                   bm25(LexicalSearchFts, 8.0, 4.0, 1.0) AS Rank,
                   e.RowId
            FROM LexicalSearchFts
            JOIN LexicalSearchEntries AS e ON e.RowId = LexicalSearchFts.rowid
            WHERE LexicalSearchFts MATCH $match
            ORDER BY Rank, e.RowId
            LIMIT $limit OFFSET $offset;
            """;
        AddParameter(command, "$match", match);
        AddParameter(command, "$limit", query.PageSize);
        AddParameter(command, "$offset", (query.Page - 1) * query.PageSize);
        var results = new List<RawResult>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var chunkId = reader.IsDBNull(3) ? (ChunkId?)null : new ChunkId(Guid.Parse(reader.GetString(3)));
            results.Add(new RawResult(
                (SearchTargetKind)reader.GetInt32(0),
                new SourceDocumentId(Guid.Parse(reader.GetString(1))),
                new SourceDocumentVersionId(Guid.Parse(reader.GetString(2))),
                chunkId,
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6)));
        }

        return results;
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record RawResult(
        SearchTargetKind Kind,
        SourceDocumentId SourceDocumentId,
        SourceDocumentVersionId DocumentVersionId,
        ChunkId? ChunkId,
        string SourceName,
        string ContextText,
        string Snippet);
}
