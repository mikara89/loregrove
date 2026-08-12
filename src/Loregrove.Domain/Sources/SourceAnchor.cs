namespace Loregrove.Domain.Sources;

public enum ParsedBlockKind
{
    Heading = 0,
    Paragraph = 1,
    ListItem = 2,
    BlockQuote = 3,
    Code = 4,
    PlainText = 5,
    Table = 6,
    Formula = 7,
    Caption = 8,
}

public enum SourceLocatorKind
{
    Text = 0,
    Markdown = 1,
    PagedRegion = 2,
    StructuredDocument = 3,
    Presentation = 4,
    ImageRegion = 5,
    Spreadsheet = 6,
}

/// <summary>
/// A durable Tier-2 observation tied to an exact parsed artifact and source location.
/// </summary>
public sealed class SourceAnchor
{
    public SourceAnchor(
        SourceAnchorId id,
        ParsedArtifactId parsedArtifactId,
        SourceDocumentVersionId documentVersionId,
        int ordinal,
        ParsedBlockKind kind,
        SourceLocatorKind locatorKind,
        int locatorSchemaVersion,
        string locatorJson,
        string normalizedText,
        string normalizedTextHash)
    {
        if (id.Value == Guid.Empty || parsedArtifactId.Value == Guid.Empty || documentVersionId.Value == Guid.Empty)
        {
            throw new ArgumentException("Anchor, artifact, and source version identifiers are required.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        ArgumentOutOfRangeException.ThrowIfLessThan(locatorSchemaVersion, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(locatorJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedText);
        if (normalizedTextHash.Length != 64 ||
            !normalizedTextHash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw new ArgumentException("A lowercase SHA-256 text hash is required.", nameof(normalizedTextHash));
        }

        Id = id;
        ParsedArtifactId = parsedArtifactId;
        DocumentVersionId = documentVersionId;
        Ordinal = ordinal;
        Kind = kind;
        LocatorKind = locatorKind;
        LocatorSchemaVersion = locatorSchemaVersion;
        LocatorJson = locatorJson;
        NormalizedText = normalizedText;
        NormalizedTextHash = normalizedTextHash;
    }

    public SourceAnchorId Id { get; }

    public ParsedArtifactId ParsedArtifactId { get; }

    public SourceDocumentVersionId DocumentVersionId { get; }

    public int Ordinal { get; }

    public ParsedBlockKind Kind { get; }

    public SourceLocatorKind LocatorKind { get; }

    public int LocatorSchemaVersion { get; }

    public string LocatorJson { get; }

    public string NormalizedText { get; }

    public string NormalizedTextHash { get; }
}
