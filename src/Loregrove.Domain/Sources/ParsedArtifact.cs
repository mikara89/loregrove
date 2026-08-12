namespace Loregrove.Domain.Sources;

public enum ParsedArtifactCompleteness
{
    Complete = 0,
    Partial = 1,
}

/// <summary>
/// Immutable metadata for one deterministic parser observation of an immutable source version.
/// </summary>
public sealed class ParsedArtifact
{
    public ParsedArtifact(
        ParsedArtifactId id,
        SourceDocumentVersionId documentVersionId,
        string sourceContentHash,
        string parserId,
        string parserVersion,
        string configurationFingerprint,
        string parserFingerprint,
        int schemaVersion,
        string artifactContentHash,
        string artifactObjectKey,
        DateTimeOffset createdAt,
        int blockCount,
        bool isCurrent,
        ParsedArtifactCompleteness completeness = ParsedArtifactCompleteness.Complete,
        int warningCount = 0,
        string? safeDiagnosticCode = null)
    {
        if (id.Value == Guid.Empty)
        {
            throw new ArgumentException("A parsed artifact id is required.", nameof(id));
        }

        if (documentVersionId.Value == Guid.Empty)
        {
            throw new ArgumentException("A source document version id is required.", nameof(documentVersionId));
        }

        ValidateHash(sourceContentHash, nameof(sourceContentHash));
        ValidateRequired(parserId, 128, nameof(parserId));
        ValidateRequired(parserVersion, 64, nameof(parserVersion));
        ValidateHash(configurationFingerprint, nameof(configurationFingerprint));
        ValidateHash(parserFingerprint, nameof(parserFingerprint));
        ArgumentOutOfRangeException.ThrowIfLessThan(schemaVersion, 1);
        ValidateHash(artifactContentHash, nameof(artifactContentHash));
        ValidateRequired(artifactObjectKey, 256, nameof(artifactObjectKey));
        ArgumentOutOfRangeException.ThrowIfNegative(blockCount);
        ArgumentOutOfRangeException.ThrowIfNegative(warningCount);
        if (safeDiagnosticCode?.Length > 128)
        {
            throw new ArgumentException("The safe diagnostic code must not exceed 128 characters.", nameof(safeDiagnosticCode));
        }

        Id = id;
        DocumentVersionId = documentVersionId;
        SourceContentHash = sourceContentHash;
        ParserId = parserId;
        ParserVersion = parserVersion;
        ConfigurationFingerprint = configurationFingerprint;
        ParserFingerprint = parserFingerprint;
        SchemaVersion = schemaVersion;
        ArtifactContentHash = artifactContentHash;
        ArtifactObjectKey = artifactObjectKey;
        CreatedAt = createdAt;
        BlockCount = blockCount;
        IsCurrent = isCurrent;
        Completeness = completeness;
        WarningCount = warningCount;
        SafeDiagnosticCode = safeDiagnosticCode;
    }

    public ParsedArtifactId Id { get; }

    public SourceDocumentVersionId DocumentVersionId { get; }

    public string SourceContentHash { get; }

    public string ParserId { get; }

    public string ParserVersion { get; }

    public string ConfigurationFingerprint { get; }

    public string ParserFingerprint { get; }

    public int SchemaVersion { get; }

    public string ArtifactContentHash { get; }

    public string ArtifactObjectKey { get; }

    public DateTimeOffset CreatedAt { get; }

    public int BlockCount { get; }

    public bool IsCurrent { get; private set; }

    public ParsedArtifactCompleteness Completeness { get; }

    public int WarningCount { get; }

    public string? SafeDiagnosticCode { get; }

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
