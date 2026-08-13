using System.Text.Json;
using Loregrove.Application.Parsing;
using Loregrove.Application.Persistence;
using Loregrove.Application.Storage;
using Loregrove.Domain.Sources;
using Microsoft.EntityFrameworkCore;

namespace Loregrove.Application.Chunking;

public sealed class ChunkingEvidenceException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class ChunkingDocumentReader(
    ILoregroveDbContext dbContext,
    IArtifactStore artifactStore,
    ISourceLocatorCodec locatorCodec)
{
    public async Task<ChunkingDocument> ReadAsync(
        SourceDocumentVersionId versionId,
        CancellationToken cancellationToken)
    {
        var artifact = await dbContext.ParsedArtifacts
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.DocumentVersionId == versionId && item.IsCurrent,
                cancellationToken)
            .ConfigureAwait(false) ?? throw new ChunkingEvidenceException("No current parsed artifact is available.");
        var anchors = await dbContext.SourceAnchors
            .AsNoTracking()
            .Where(anchor => anchor.ParsedArtifactId == artifact.Id)
            .OrderBy(anchor => anchor.Ordinal)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        byte[] bytes;
        await using (var stream = await artifactStore.OpenReadAsync(artifact.ArtifactObjectKey, cancellationToken)
            .ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            bytes = buffer.ToArray();
        }

        if (!string.Equals(
                ParsedArtifactSerializer.HashBytes(bytes),
                artifact.ArtifactContentHash,
                StringComparison.Ordinal))
        {
            throw new ChunkingEvidenceException("The parsed artifact content hash is inconsistent.");
        }

        try
        {
            using var json = JsonDocument.Parse(bytes);
            var root = json.RootElement;
            var parser = root.GetProperty("parser");
            var artifactVersionId = root.GetProperty("source").GetProperty("documentVersionId").GetString();
            var sourceHash = root.GetProperty("source").GetProperty("contentHash").GetString();
            if (!Guid.TryParse(artifactVersionId, out var parsedVersionId) ||
                parsedVersionId != versionId.Value ||
                !string.Equals(sourceHash, artifact.SourceContentHash, StringComparison.Ordinal) ||
                root.GetProperty("schemaVersion").GetInt32() != artifact.SchemaVersion ||
                !string.Equals(parser.GetProperty("id").GetString(), artifact.ParserId, StringComparison.Ordinal) ||
                !string.Equals(parser.GetProperty("version").GetString(), artifact.ParserVersion, StringComparison.Ordinal) ||
                !string.Equals(parser.GetProperty("configurationFingerprint").GetString(), artifact.ConfigurationFingerprint, StringComparison.Ordinal) ||
                !string.Equals(parser.GetProperty("fingerprint").GetString(), artifact.ParserFingerprint, StringComparison.Ordinal))
            {
                throw new ChunkingEvidenceException("The parsed artifact source identity is inconsistent.");
            }

            var blocks = root.GetProperty("blocks").EnumerateArray().ToArray();
            if (blocks.Length != artifact.BlockCount || anchors.Count != artifact.BlockCount)
            {
                throw new ChunkingEvidenceException("The parsed block and source-anchor counts are inconsistent.");
            }

            var observations = new ChunkingObservation[blocks.Length];
            var seenOrdinals = new HashSet<int>();
            for (var index = 0; index < blocks.Length; index++)
            {
                var block = blocks[index];
                var ordinal = block.GetProperty("ordinal").GetInt32();
                if (ordinal != index || !seenOrdinals.Add(ordinal))
                {
                    throw new ChunkingEvidenceException("Parsed block ordinals are inconsistent.");
                }

                var anchor = anchors[index];
                var text = block.GetProperty("text").GetString() ?? string.Empty;
                var textHash = block.GetProperty("textHash").GetString();
                var kindText = block.GetProperty("kind").GetString();
                if (anchor.Ordinal != ordinal || anchor.DocumentVersionId != versionId ||
                    anchor.ParsedArtifactId != artifact.Id ||
                    !Enum.TryParse<ParsedBlockKind>(kindText, ignoreCase: false, out var blockKind) ||
                    blockKind != anchor.Kind ||
                    !string.Equals(anchor.NormalizedText, text, StringComparison.Ordinal) ||
                    !string.Equals(anchor.NormalizedTextHash, textHash, StringComparison.Ordinal) ||
                    !string.Equals(ParsedArtifactSerializer.HashText(text), anchor.NormalizedTextHash, StringComparison.Ordinal))
                {
                    throw new ChunkingEvidenceException("A parsed block does not agree with its persisted source anchor.");
                }

                var headingPath = block.GetProperty("headingPath")
                    .EnumerateArray()
                    .Select(value => value.GetString() ?? string.Empty)
                    .ToArray();
                observations[index] = new ChunkingObservation(
                    anchor.Id,
                    anchor.Ordinal,
                    anchor.Kind,
                    anchor.NormalizedText,
                    anchor.NormalizedTextHash,
                    headingPath,
                    locatorCodec.Deserialize(anchor.LocatorKind, anchor.LocatorSchemaVersion, anchor.LocatorJson),
                    ChunkerDescriptor.Hash(string.Join('\n', anchor.LocatorKind, anchor.LocatorSchemaVersion, anchor.LocatorJson)));
            }

            return new ChunkingDocument(
                versionId,
                artifact.Id,
                artifact.SourceContentHash,
                artifact.ArtifactContentHash,
                observations);
        }
        catch (ChunkingEvidenceException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new ChunkingEvidenceException("The parsed artifact cannot be read safely for chunking.", exception);
        }
    }
}
