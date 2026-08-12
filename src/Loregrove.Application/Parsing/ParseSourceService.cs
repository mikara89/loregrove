using Loregrove.Application.Persistence;
using Loregrove.Application.Storage;
using Loregrove.Domain.Sources;
using Microsoft.EntityFrameworkCore;

namespace Loregrove.Application.Parsing;

public sealed class ParseSourceService(
    ILoregroveDbContext dbContext,
    IObjectStore objectStore,
    IArtifactStore artifactStore,
    IDocumentParserResolver parserResolver,
    ISourceLocatorCodec locatorCodec,
    TimeProvider? timeProvider = null,
    IParseTransactionHook? transactionHook = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IParseTransactionHook _transactionHook = transactionHook ?? NoOpParseTransactionHook.Instance;

    public Task<ParseSourceResult> ParseAsync(
        SourceDocumentVersionId versionId,
        CancellationToken cancellationToken) =>
        ParseCoreAsync(versionId, retryOnly: false, cancellationToken);

    public Task<ParseSourceResult> RetryAsync(
        SourceDocumentVersionId versionId,
        CancellationToken cancellationToken) =>
        ParseCoreAsync(versionId, retryOnly: true, cancellationToken);

    private async Task<ParseSourceResult> ParseCoreAsync(
        SourceDocumentVersionId versionId,
        bool retryOnly,
        CancellationToken cancellationToken)
    {
        if (versionId.Value == Guid.Empty)
        {
            throw new ArgumentException("A source document version id is required.", nameof(versionId));
        }

        var version = await dbContext.SourceDocumentVersions
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == versionId, cancellationToken)
            .ConfigureAwait(false);
        if (version is null)
        {
            return new ParseSourceResult(ParseSourceDisposition.NotFound);
        }

        var descriptor = new ParseSourceDescriptor(
            version.Id,
            version.ContentHash,
            version.OriginalFileName,
            version.MediaType);
        var parser = parserResolver.Resolve(descriptor);
        if (parser is null)
        {
            return new ParseSourceResult(
                ParseSourceDisposition.Unsupported,
                Message: "No parser is available for this source format.");
        }

        var existing = await dbContext.ParsedArtifacts
            .AsNoTracking()
            .Where(artifact =>
                artifact.DocumentVersionId == versionId &&
                artifact.ParserFingerprint == parser.Descriptor.Fingerprint)
            .Select(artifact => (ParsedArtifactId?)artifact.Id)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (existing is { } existingId)
        {
            return new ParseSourceResult(ParseSourceDisposition.AlreadyParsed, existingId);
        }

        var claimed = await TryClaimAsync(
            versionId,
            parser.Descriptor.Fingerprint,
            retryOnly,
            cancellationToken).ConfigureAwait(false);
        if (!claimed)
        {
            return await ResolveUnclaimedAsync(versionId, parser.Descriptor.Fingerprint, retryOnly, cancellationToken)
                .ConfigureAwait(false);
        }

        ParsedDocumentResult parsed;
        try
        {
            await using var source = await objectStore.OpenReadAsync(version.ObjectKey, cancellationToken)
                .ConfigureAwait(false);
            parsed = await parser.ParseAsync(source, descriptor, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReturnToPendingAsync(versionId).ConfigureAwait(false);
            return new ParseSourceResult(ParseSourceDisposition.Cancelled);
        }
        catch (DocumentParseException)
        {
            const string message = "The source could not be parsed.";
            await MarkFailedAsync(versionId, message).ConfigureAwait(false);
            return new ParseSourceResult(ParseSourceDisposition.Failed, Message: message);
        }
        catch
        {
            await ReturnToPendingAsync(versionId).ConfigureAwait(false);
            throw;
        }

        try
        {
            ValidateResult(parsed, parser.Descriptor);
            await _transactionHook.OnStageAsync(
                ParseTransactionStage.AfterParserSuccess,
                cancellationToken).ConfigureAwait(false);
            var serialized = ParsedArtifactSerializer.Serialize(descriptor, parsed);
            await using var artifactContent = new MemoryStream(serialized.Bytes, writable: false);
            var stored = await artifactStore.StoreAsync(artifactContent, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(stored.ContentHash, serialized.ContentHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The artifact store returned an unexpected content hash.");
            }

            await _transactionHook.OnStageAsync(
                ParseTransactionStage.AfterArtifactFinalized,
                cancellationToken).ConfigureAwait(false);
            return await CommitSuccessfulParseAsync(
                version,
                parsed,
                stored,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReturnToPendingAsync(versionId).ConfigureAwait(false);
            return new ParseSourceResult(ParseSourceDisposition.Cancelled);
        }
        catch
        {
            await ReturnToPendingAsync(versionId).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<bool> TryClaimAsync(
        SourceDocumentVersionId versionId,
        string parserFingerprint,
        bool retryOnly,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        await using var transaction = await dbContext.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var candidates = dbContext.ProcessingJobs.Where(job =>
                job.DocumentVersionId == versionId &&
                job.Stage == ProcessingStage.Parsing);
            candidates = retryOnly
                ? candidates.Where(job => job.State == ProcessingJobState.Failed)
                : dbContext.ProcessingJobs.Where(job =>
                    job.DocumentVersionId == versionId && job.State == ProcessingJobState.Pending);
            candidates = candidates.Where(_ => !dbContext.ParsedArtifacts.Any(artifact =>
                artifact.DocumentVersionId == versionId &&
                artifact.ParserFingerprint == parserFingerprint));

            var changed = await candidates.ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.State, ProcessingJobState.Processing)
                    .SetProperty(job => job.Stage, ProcessingStage.Parsing)
                    .SetProperty(job => job.AttemptCount, job => job.AttemptCount + 1)
                    .SetProperty(job => job.UpdatedAt, now)
                    .SetProperty(job => job.LastError, (string?)null),
                cancellationToken).ConfigureAwait(false);
            if (changed == 0)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            await dbContext.SourceDocumentVersions
                .Where(item => item.Id == versionId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        item => item.ProcessingState,
                        SourceProcessingState.Parsing),
                    cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            dbContext.ClearTrackedChanges();
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            dbContext.ClearTrackedChanges();
            throw;
        }
    }

    private async Task<ParseSourceResult> CommitSuccessfulParseAsync(
        SourceDocumentVersion sourceVersion,
        ParsedDocumentResult parsed,
        StoredArtifact stored,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var artifactId = ParsedArtifactId.New();
        var artifact = new ParsedArtifact(
            artifactId,
            sourceVersion.Id,
            sourceVersion.ContentHash,
            parsed.Parser.Id,
            parsed.Parser.Version,
            parsed.Parser.ConfigurationFingerprint,
            parsed.Parser.Fingerprint,
            parsed.Parser.OutputSchemaVersion,
            stored.ContentHash,
            stored.ObjectKey,
            now,
            parsed.Blocks.Count,
            isCurrent: true);
        var anchors = parsed.Blocks.Select(block => new SourceAnchor(
            SourceAnchorId.New(),
            artifactId,
            sourceVersion.Id,
            block.Ordinal,
            block.Kind,
            block.Locator.Kind,
            block.Locator.SchemaVersion,
            locatorCodec.Serialize(block.Locator),
            block.Text,
            ParsedArtifactSerializer.HashText(block.Text))).ToArray();

        await using var transaction = await dbContext.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await dbContext.ParsedArtifacts
                .Where(item => item.DocumentVersionId == sourceVersion.Id && item.IsCurrent)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(item => item.IsCurrent, false),
                    cancellationToken).ConfigureAwait(false);
            dbContext.ParsedArtifacts.Add(artifact);
            dbContext.SourceAnchors.AddRange(anchors);

            var version = await dbContext.SourceDocumentVersions
                .SingleAsync(item => item.Id == sourceVersion.Id, cancellationToken)
                .ConfigureAwait(false);
            var job = await dbContext.ProcessingJobs
                .SingleAsync(item => item.DocumentVersionId == sourceVersion.Id, cancellationToken)
                .ConfigureAwait(false);
            version.MarkParsed();
            job.CompleteParsing(now);

            await _transactionHook.OnStageAsync(
                ParseTransactionStage.AfterRelationalEntitiesAdded,
                cancellationToken).ConfigureAwait(false);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await _transactionHook.OnStageAsync(
                ParseTransactionStage.BeforeCommit,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            dbContext.ClearTrackedChanges();
            return new ParseSourceResult(ParseSourceDisposition.Parsed, artifactId);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            dbContext.ClearTrackedChanges();
            throw;
        }
    }

    private async Task<ParseSourceResult> ResolveUnclaimedAsync(
        SourceDocumentVersionId versionId,
        string parserFingerprint,
        bool retryOnly,
        CancellationToken cancellationToken)
    {
        var artifactId = await dbContext.ParsedArtifacts
            .AsNoTracking()
            .Where(artifact =>
                artifact.DocumentVersionId == versionId &&
                artifact.ParserFingerprint == parserFingerprint)
            .Select(artifact => (ParsedArtifactId?)artifact.Id)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (artifactId is { } existing)
        {
            return new ParseSourceResult(ParseSourceDisposition.AlreadyParsed, existing);
        }

        var job = await dbContext.ProcessingJobs
            .AsNoTracking()
            .SingleAsync(item => item.DocumentVersionId == versionId, cancellationToken)
            .ConfigureAwait(false);
        if (job.State == ProcessingJobState.Processing)
        {
            return new ParseSourceResult(ParseSourceDisposition.Busy);
        }

        return new ParseSourceResult(
            ParseSourceDisposition.Failed,
            Message: retryOnly ? "The source does not have a failed parsing attempt to retry." : job.LastError);
    }

    private async Task MarkFailedAsync(SourceDocumentVersionId versionId, string error)
    {
        var now = _timeProvider.GetUtcNow();
        await using var transaction = await dbContext.BeginTransactionAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var version = await dbContext.SourceDocumentVersions.SingleAsync(item => item.Id == versionId)
                .ConfigureAwait(false);
            var job = await dbContext.ProcessingJobs.SingleAsync(item => item.DocumentVersionId == versionId)
                .ConfigureAwait(false);
            version.MarkParseFailed();
            job.FailParsing(now, error);
            await dbContext.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            dbContext.ClearTrackedChanges();
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            dbContext.ClearTrackedChanges();
            throw;
        }
    }

    private async Task ReturnToPendingAsync(SourceDocumentVersionId versionId)
    {
        var now = _timeProvider.GetUtcNow();
        await using var transaction = await dbContext.BeginTransactionAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await dbContext.ProcessingJobs
                .Where(job => job.DocumentVersionId == versionId && job.State == ProcessingJobState.Processing)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(job => job.State, ProcessingJobState.Pending)
                        .SetProperty(job => job.Stage, ProcessingStage.Parsing)
                        .SetProperty(job => job.UpdatedAt, now)
                        .SetProperty(job => job.LastError, (string?)null),
                    CancellationToken.None).ConfigureAwait(false);
            await dbContext.SourceDocumentVersions
                .Where(version => version.Id == versionId && version.ProcessingState == SourceProcessingState.Parsing)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        version => version.ProcessingState,
                        SourceProcessingState.PendingProcessing),
                    CancellationToken.None).ConfigureAwait(false);
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            dbContext.ClearTrackedChanges();
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            dbContext.ClearTrackedChanges();
            throw;
        }
    }

    private static void ValidateResult(ParsedDocumentResult result, ParserDescriptor expected)
    {
        if (result.Parser != expected)
        {
            throw new InvalidOperationException("The parser result descriptor did not match the selected parser.");
        }

        for (var ordinal = 0; ordinal < result.Blocks.Count; ordinal++)
        {
            var block = result.Blocks[ordinal];
            if (block.Ordinal != ordinal || string.IsNullOrWhiteSpace(block.Text))
            {
                throw new InvalidOperationException("Parser blocks must be non-empty and contiguously ordered.");
            }

            if (block.Locator is MarkdownSourceLocator markdown &&
                (markdown.BlockOrdinal != ordinal || !markdown.HeadingPath.SequenceEqual(block.HeadingPath)))
            {
                throw new InvalidOperationException("Markdown locator context must match its parsed block.");
            }
        }
    }

}
