namespace Loregrove.Domain.Sources;

/// <summary>Immutable history record for one deterministic derivation from a parsed artifact.</summary>
public sealed class ChunkSet
{
    public ChunkSet(
        ChunkSetId id,
        SourceDocumentVersionId documentVersionId,
        ParsedArtifactId parsedArtifactId,
        string chunkerId,
        string chunkerVersion,
        int chunkSchemaVersion,
        string configurationFingerprint,
        string chunkerFingerprint,
        DateTimeOffset createdAt,
        int chunkCount,
        bool isCurrent)
    {
        if (id.Value == Guid.Empty || documentVersionId.Value == Guid.Empty || parsedArtifactId.Value == Guid.Empty)
        {
            throw new ArgumentException("Chunk set, source version, and parsed artifact identifiers are required.");
        }

        ValidateRequired(chunkerId, 128, nameof(chunkerId));
        ValidateRequired(chunkerVersion, 64, nameof(chunkerVersion));
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSchemaVersion, 1);
        ValidateHash(configurationFingerprint, nameof(configurationFingerprint));
        ValidateHash(chunkerFingerprint, nameof(chunkerFingerprint));
        ArgumentOutOfRangeException.ThrowIfNegative(chunkCount);

        Id = id;
        DocumentVersionId = documentVersionId;
        ParsedArtifactId = parsedArtifactId;
        ChunkerId = chunkerId;
        ChunkerVersion = chunkerVersion;
        ChunkSchemaVersion = chunkSchemaVersion;
        ConfigurationFingerprint = configurationFingerprint;
        ChunkerFingerprint = chunkerFingerprint;
        CreatedAt = createdAt;
        ChunkCount = chunkCount;
        IsCurrent = isCurrent;
    }

    public ChunkSetId Id { get; }
    public SourceDocumentVersionId DocumentVersionId { get; }
    public ParsedArtifactId ParsedArtifactId { get; }
    public string ChunkerId { get; }
    public string ChunkerVersion { get; }
    public int ChunkSchemaVersion { get; }
    public string ConfigurationFingerprint { get; }
    public string ChunkerFingerprint { get; }
    public DateTimeOffset CreatedAt { get; }
    public int ChunkCount { get; }
    public bool IsCurrent { get; private set; }

    public void MarkNotCurrent() => IsCurrent = false;

    private static void ValidateRequired(string value, int maximumLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            throw new ArgumentException($"A value of at most {maximumLength} characters is required.", parameterName);
        }
    }

    private static void ValidateHash(string value, string parameterName)
    {
        if (value.Length != 64 || !value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw new ArgumentException("A lowercase SHA-256 hex value is required.", parameterName);
        }
    }
}
