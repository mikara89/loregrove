namespace Loregrove.Domain.Sources;

/// <summary>Half-open exact offsets mapping chunk characters back to a Tier-2 source anchor.</summary>
public sealed class ChunkEvidenceSpan
{
    public ChunkEvidenceSpan(
        ChunkId chunkId,
        int ordinal,
        SourceAnchorId sourceAnchorId,
        ParsedArtifactId parsedArtifactId,
        SourceDocumentVersionId documentVersionId,
        int anchorStart,
        int anchorEnd,
        int chunkStart,
        int chunkEnd)
    {
        if (chunkId.Value == Guid.Empty || sourceAnchorId.Value == Guid.Empty ||
            parsedArtifactId.Value == Guid.Empty || documentVersionId.Value == Guid.Empty)
        {
            throw new ArgumentException("Chunk, anchor, artifact, and source version identifiers are required.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        ValidateSpan(anchorStart, anchorEnd, nameof(anchorEnd));
        ValidateSpan(chunkStart, chunkEnd, nameof(chunkEnd));
        ChunkId = chunkId;
        Ordinal = ordinal;
        SourceAnchorId = sourceAnchorId;
        ParsedArtifactId = parsedArtifactId;
        DocumentVersionId = documentVersionId;
        AnchorStart = anchorStart;
        AnchorEnd = anchorEnd;
        ChunkStart = chunkStart;
        ChunkEnd = chunkEnd;
    }

    public ChunkId ChunkId { get; }
    public int Ordinal { get; }
    public SourceAnchorId SourceAnchorId { get; }
    public ParsedArtifactId ParsedArtifactId { get; }
    public SourceDocumentVersionId DocumentVersionId { get; }
    public int AnchorStart { get; }
    public int AnchorEnd { get; }
    public int ChunkStart { get; }
    public int ChunkEnd { get; }

    private static void ValidateSpan(int start, int end, string parameterName)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        if (end <= start)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Span end must be greater than its start.");
        }
    }
}
