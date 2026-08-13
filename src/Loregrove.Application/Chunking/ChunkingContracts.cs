using System.Security.Cryptography;
using System.Text;
using Loregrove.Application.Parsing;
using Loregrove.Domain.Sources;

namespace Loregrove.Application.Chunking;

public sealed record ChunkerDescriptor(
    string Id,
    string Version,
    int SchemaVersion,
    string ConfigurationFingerprint,
    string Fingerprint)
{
    public static ChunkerDescriptor Create(string id, string version, int schemaVersion, string configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentOutOfRangeException.ThrowIfLessThan(schemaVersion, 1);
        ArgumentNullException.ThrowIfNull(configuration);
        var configurationFingerprint = Hash(configuration);
        return new ChunkerDescriptor(
            id,
            version,
            schemaVersion,
            configurationFingerprint,
            Hash(string.Join('\n', id, version, schemaVersion, configurationFingerprint)));
    }

    internal static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed record EvidenceAwareChunkerOptions(
    int TargetCharacters = 1200,
    int MaximumCharacters = 2000,
    int MinimumCharacters = 200,
    int OverlapCharacters = 0)
{
    public string CanonicalConfiguration => string.Join(
        '\n',
        $"targetCharacters={TargetCharacters}",
        $"maximumCharacters={MaximumCharacters}",
        $"minimumCharacters={MinimumCharacters}",
        $"overlapCharacters={OverlapCharacters}",
        "separator=LF-LF",
        "breakOrder=newline,sentence,whitespace,hard");

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(TargetCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumCharacters, TargetCharacters);
        ArgumentOutOfRangeException.ThrowIfNegative(MinimumCharacters);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MinimumCharacters, MaximumCharacters);
        if (OverlapCharacters != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(OverlapCharacters), "The initial profile requires zero overlap.");
        }
    }
}

public sealed record ChunkingDocument(
    SourceDocumentVersionId DocumentVersionId,
    ParsedArtifactId ParsedArtifactId,
    string SourceContentHash,
    string ParsedArtifactContentHash,
    IReadOnlyList<ChunkingObservation> Observations);

public sealed record ChunkingObservation(
    SourceAnchorId SourceAnchorId,
    int AnchorOrdinal,
    ParsedBlockKind Kind,
    string NormalizedText,
    string NormalizedTextHash,
    IReadOnlyList<string> HeadingPath,
    SourceLocator Locator,
    string LocatorFingerprint);

public sealed record ChunkEvidenceCandidate(
    SourceAnchorId SourceAnchorId,
    int AnchorOrdinal,
    string AnchorTextHash,
    string LocatorFingerprint,
    int AnchorStart,
    int AnchorEnd,
    int ChunkStart,
    int ChunkEnd);

public sealed record ChunkCandidate(
    int Ordinal,
    string ChunkKey,
    string Text,
    string ContextText,
    string ContentHash,
    IReadOnlyList<ChunkEvidenceCandidate> EvidenceSpans)
{
    public string CanonicalContent => string.IsNullOrEmpty(ContextText) ? Text : $"{ContextText}\n\n{Text}";
}

public interface IChunker
{
    ChunkerDescriptor Descriptor { get; }

    IReadOnlyList<ChunkCandidate> Chunk(ChunkingDocument document, CancellationToken cancellationToken);
}

public enum ChunkSourceDisposition
{
    Chunked = 0,
    AlreadyChunked = 1,
    Busy = 2,
    NotReady = 3,
    Failed = 4,
    Cancelled = 5,
    NotFound = 6,
}

public sealed record ChunkSourceResult(
    ChunkSourceDisposition Disposition,
    ChunkSetId? ChunkSetId = null,
    int ChunkCount = 0,
    string? Message = null);
