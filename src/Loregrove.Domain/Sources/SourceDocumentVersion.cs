namespace Loregrove.Domain.Sources;

/// <summary>
/// Immutable identity and capture metadata for one exact set of original source bytes.
/// </summary>
public sealed class SourceDocumentVersion
{
    public SourceDocumentVersion(
        SourceDocumentVersionId id,
        SourceDocumentId documentId,
        string contentHash,
        string originalFileName,
        string? mediaType,
        long byteLength,
        DateTimeOffset importedAt,
        string objectKey,
        SourceDocumentVersionId? previousVersionId,
        SourceProcessingState processingState)
    {
        if (id.Value == Guid.Empty)
        {
            throw new ArgumentException("A source document version id is required.", nameof(id));
        }

        if (documentId.Value == Guid.Empty)
        {
            throw new ArgumentException("A source document id is required.", nameof(documentId));
        }

        if (!IsSha256(contentHash))
        {
            throw new ArgumentException("The content hash must be a lowercase SHA-256 hex value.", nameof(contentHash));
        }

        if (string.IsNullOrEmpty(originalFileName))
        {
            throw new ArgumentException("An original file name is required.", nameof(originalFileName));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(byteLength);

        if (string.IsNullOrWhiteSpace(objectKey))
        {
            throw new ArgumentException("An object key is required.", nameof(objectKey));
        }

        if (previousVersionId is { Value: var previousValue } && previousValue == Guid.Empty)
        {
            throw new ArgumentException("A previous version id cannot be empty.", nameof(previousVersionId));
        }

        Id = id;
        DocumentId = documentId;
        ContentHash = contentHash;
        OriginalFileName = originalFileName;
        MediaType = mediaType;
        ByteLength = byteLength;
        ImportedAt = importedAt;
        ObjectKey = objectKey;
        PreviousVersionId = previousVersionId;
        ProcessingState = processingState;
    }

    public SourceDocumentVersionId Id { get; }

    public SourceDocumentId DocumentId { get; }

    public string ContentHash { get; }

    public string OriginalFileName { get; }

    public string? MediaType { get; }

    public long ByteLength { get; }

    public DateTimeOffset ImportedAt { get; }

    public string ObjectKey { get; }

    public SourceDocumentVersionId? PreviousVersionId { get; }

    public SourceProcessingState ProcessingState { get; private set; }

    public void MarkParsing() => ProcessingState = SourceProcessingState.Parsing;

    public void MarkParsed() => ProcessingState = SourceProcessingState.Parsed;

    public void MarkParseFailed() => ProcessingState = SourceProcessingState.ParseFailed;

    public void MarkChunking() => ProcessingState = SourceProcessingState.Chunking;

    public void MarkChunked() => ProcessingState = SourceProcessingState.Chunked;

    public void ReturnToParsed() => ProcessingState = SourceProcessingState.Parsed;

    public void ReturnToPendingProcessing() => ProcessingState = SourceProcessingState.PendingProcessing;

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
