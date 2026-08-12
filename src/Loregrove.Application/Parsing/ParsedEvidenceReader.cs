using Loregrove.Application.Persistence;
using Loregrove.Domain.Sources;
using Microsoft.EntityFrameworkCore;

namespace Loregrove.Application.Parsing;

public sealed record ParsedArtifactView(
    ParsedArtifactId Id,
    SourceDocumentVersionId DocumentVersionId,
    string ParserId,
    string ParserVersion,
    string ParserFingerprint,
    int SchemaVersion,
    string ArtifactContentHash,
    int BlockCount,
    DateTimeOffset CreatedAt);

public sealed record SourceAnchorView(
    SourceAnchorId Id,
    ParsedArtifactId ParsedArtifactId,
    SourceDocumentVersionId DocumentVersionId,
    int Ordinal,
    ParsedBlockKind Kind,
    SourceLocator Locator,
    string NormalizedText,
    string NormalizedTextHash);

public interface IParsedEvidenceReader
{
    Task<ParsedArtifactView?> GetCurrentArtifactAsync(
        SourceDocumentVersionId versionId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SourceAnchorView>> GetAnchorsAsync(
        ParsedArtifactId artifactId,
        CancellationToken cancellationToken);
}

public sealed class ParsedEvidenceReader(
    ILoregroveDbContext dbContext,
    ISourceLocatorCodec locatorCodec) : IParsedEvidenceReader
{
    public Task<ParsedArtifactView?> GetCurrentArtifactAsync(
        SourceDocumentVersionId versionId,
        CancellationToken cancellationToken) =>
        dbContext.ParsedArtifacts
            .AsNoTracking()
            .Where(artifact => artifact.DocumentVersionId == versionId && artifact.IsCurrent)
            .Select(artifact => new ParsedArtifactView(
                artifact.Id,
                artifact.DocumentVersionId,
                artifact.ParserId,
                artifact.ParserVersion,
                artifact.ParserFingerprint,
                artifact.SchemaVersion,
                artifact.ArtifactContentHash,
                artifact.BlockCount,
                artifact.CreatedAt))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<SourceAnchorView>> GetAnchorsAsync(
        ParsedArtifactId artifactId,
        CancellationToken cancellationToken)
    {
        var anchors = await dbContext.SourceAnchors
            .AsNoTracking()
            .Where(anchor => anchor.ParsedArtifactId == artifactId)
            .OrderBy(anchor => anchor.Ordinal)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return anchors.Select(anchor => new SourceAnchorView(
            anchor.Id,
            anchor.ParsedArtifactId,
            anchor.DocumentVersionId,
            anchor.Ordinal,
            anchor.Kind,
            locatorCodec.Deserialize(
                anchor.LocatorKind,
                anchor.LocatorSchemaVersion,
                anchor.LocatorJson),
            anchor.NormalizedText,
            anchor.NormalizedTextHash)).ToArray();
    }
}
