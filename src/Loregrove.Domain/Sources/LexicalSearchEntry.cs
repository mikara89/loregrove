namespace Loregrove.Domain.Sources;

public enum LexicalSearchEntryKind
{
    Source = 0,
    Chunk = 1,
}

/// <summary>Durable, rebuildable content-table row for the disposable FTS5 projection.</summary>
public sealed class LexicalSearchEntry
{
    public LexicalSearchEntry(
        LexicalSearchEntryKind kind,
        SourceDocumentId sourceDocumentId,
        SourceDocumentVersionId documentVersionId,
        ChunkId? chunkId,
        string sourceName,
        string title,
        string heading,
        string body)
    {
        if (sourceDocumentId.Value == Guid.Empty || documentVersionId.Value == Guid.Empty)
        {
            throw new ArgumentException("Source document and version identifiers are required.");
        }

        if (kind == LexicalSearchEntryKind.Source && chunkId is not null ||
            kind == LexicalSearchEntryKind.Chunk && chunkId is null)
        {
            throw new ArgumentException("Search entry kind and chunk identity are inconsistent.", nameof(chunkId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(heading);
        ArgumentNullException.ThrowIfNull(body);
        Kind = kind;
        SourceDocumentId = sourceDocumentId;
        DocumentVersionId = documentVersionId;
        ChunkId = chunkId;
        SourceName = sourceName;
        Title = title;
        Heading = heading;
        Body = body;
    }

    public long RowId { get; private set; }
    public LexicalSearchEntryKind Kind { get; }
    public SourceDocumentId SourceDocumentId { get; }
    public SourceDocumentVersionId DocumentVersionId { get; }
    public ChunkId? ChunkId { get; }
    public string SourceName { get; }
    public string Title { get; }
    public string Heading { get; }
    public string Body { get; }
}
