namespace Loregrove.Domain.Sources;

/// <summary>A bounded retrieval unit derived from, but never replacing, parsed evidence.</summary>
public sealed class Chunk
{
    public Chunk(
        ChunkId id,
        ChunkSetId chunkSetId,
        SourceDocumentVersionId documentVersionId,
        ParsedArtifactId parsedArtifactId,
        int ordinal,
        string chunkKey,
        string text,
        string contextText,
        string contentHash,
        int characterLength)
    {
        if (id.Value == Guid.Empty || chunkSetId.Value == Guid.Empty ||
            documentVersionId.Value == Guid.Empty || parsedArtifactId.Value == Guid.Empty)
        {
            throw new ArgumentException("Chunk, chunk set, source version, and parsed artifact identifiers are required.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        ValidateHash(chunkKey, nameof(chunkKey));
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(contextText);
        ValidateHash(contentHash, nameof(contentHash));
        if (characterLength != text.Length)
        {
            throw new ArgumentException("Character length must equal the source-derived text length.", nameof(characterLength));
        }

        Id = id;
        ChunkSetId = chunkSetId;
        DocumentVersionId = documentVersionId;
        ParsedArtifactId = parsedArtifactId;
        Ordinal = ordinal;
        ChunkKey = chunkKey;
        Text = text;
        ContextText = contextText;
        ContentHash = contentHash;
        CharacterLength = characterLength;
    }

    public ChunkId Id { get; }
    public ChunkSetId ChunkSetId { get; }
    public SourceDocumentVersionId DocumentVersionId { get; }
    public ParsedArtifactId ParsedArtifactId { get; }
    public int Ordinal { get; }
    public string ChunkKey { get; }
    public string Text { get; }
    public string ContextText { get; }
    public string ContentHash { get; }
    public int CharacterLength { get; }

    private static void ValidateHash(string value, string parameterName)
    {
        if (value.Length != 64 || !value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw new ArgumentException("A lowercase SHA-256 hex value is required.", parameterName);
        }
    }
}
