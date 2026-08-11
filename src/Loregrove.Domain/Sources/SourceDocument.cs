namespace Loregrove.Domain.Sources;

/// <summary>
/// The logical identity of a source. Version relationships are resolved separately.
/// </summary>
public sealed class SourceDocument
{
    public SourceDocument(
        SourceDocumentId id,
        string displayName,
        SourceKind sourceKind,
        DateTimeOffset createdAt,
        SourceDocumentVersionId currentVersionId)
    {
        if (id.Value == Guid.Empty)
        {
            throw new ArgumentException("A source document id is required.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("A display name is required.", nameof(displayName));
        }

        if (currentVersionId.Value == Guid.Empty)
        {
            throw new ArgumentException("A current version id is required.", nameof(currentVersionId));
        }

        Id = id;
        DisplayName = displayName;
        SourceKind = sourceKind;
        CreatedAt = createdAt;
        CurrentVersionId = currentVersionId;
    }

    public SourceDocumentId Id { get; }

    public string DisplayName { get; }

    public SourceKind SourceKind { get; }

    public DateTimeOffset CreatedAt { get; }

    public SourceDocumentVersionId CurrentVersionId { get; }
}
