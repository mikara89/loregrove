using System.Text;
using Loregrove.Application.Parsing;
using Loregrove.Application.Persistence;
using Loregrove.Application.Sources;
using Loregrove.Application.Storage;
using Loregrove.Domain.Sources;
using Loregrove.Infrastructure.LocalFiles;
using Loregrove.Infrastructure.Sqlite;
using Loregrove.Infrastructure.Sqlite.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Loregrove.IntegrationTests;

public sealed class ParsingIntegrationTests
{
    [Fact]
    public async Task TxtParsePersistsArtifactAnchorsHashAndChunkingTransitionAcrossRestart()
    {
        using var directory = new TemporaryDirectory();
        SourceDocumentVersionId versionId;
        ParsedArtifactId artifactId;
        string artifactObjectKey;
        string artifactHash;

        await using (var first = BuildServices(directory.Path))
        {
            await InitializeAsync(first);
            versionId = await ImportAsync(first, "Evidence", "evidence.txt", "text/plain", "First.\nStill first.\n\nSecond.\n");

            var result = await ParseAsync(first, versionId);

            Assert.Equal(ParseSourceDisposition.Parsed, result.Disposition);
            artifactId = Assert.IsType<ParsedArtifactId>(result.ArtifactId);
            await using var scope = first.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
            var artifact = await context.ParsedArtifacts.AsNoTracking().SingleAsync();
            var anchors = await context.SourceAnchors.AsNoTracking().OrderBy(anchor => anchor.Ordinal).ToListAsync();
            var source = await context.SourceDocumentVersions.AsNoTracking().SingleAsync();
            var job = await context.ProcessingJobs.AsNoTracking().SingleAsync();
            Assert.True(artifact.IsCurrent);
            Assert.Equal(2, artifact.BlockCount);
            Assert.Equal(["First.\nStill first.", "Second."], anchors.Select(anchor => anchor.NormalizedText));
            Assert.All(anchors, anchor => Assert.Equal(ParsedArtifactSerializer.HashText(anchor.NormalizedText), anchor.NormalizedTextHash));
            Assert.Equal(SourceProcessingState.Parsed, source.ProcessingState);
            Assert.Equal(ProcessingJobState.Pending, job.State);
            Assert.Equal(ProcessingStage.Chunking, job.Stage);
            Assert.Equal(1, job.AttemptCount);
            Assert.Null(job.LastError);
            artifactObjectKey = artifact.ArtifactObjectKey;
            artifactHash = artifact.ArtifactContentHash;
        }

        await using var restarted = BuildServices(directory.Path);
        await InitializeAsync(restarted);
        await using (var scope = restarted.CreateAsyncScope())
        {
            var reader = scope.ServiceProvider.GetRequiredService<IParsedEvidenceReader>();
            var artifact = await reader.GetCurrentArtifactAsync(versionId, CancellationToken.None);
            Assert.NotNull(artifact);
            Assert.Equal(artifactId, artifact.Id);
            Assert.Equal(2, (await reader.GetAnchorsAsync(artifactId, CancellationToken.None)).Count);
        }

        var store = restarted.GetRequiredService<IArtifactStore>();
        await using var content = await store.OpenReadAsync(artifactObjectKey, CancellationToken.None);
        using var memory = new MemoryStream();
        await content.CopyToAsync(memory);
        Assert.Equal(artifactHash, ParsedArtifactSerializer.HashBytes(memory.ToArray()));
    }

    [Fact]
    public async Task MarkdownEvidenceAndTypedHeadingLocatorSurviveRestart()
    {
        using var directory = new TemporaryDirectory();
        SourceDocumentVersionId versionId;
        ParsedArtifactId artifactId;
        await using (var first = BuildServices(directory.Path))
        {
            await InitializeAsync(first);
            versionId = await ImportAsync(
                first,
                "Markdown restart",
                "restart.md",
                "text/markdown",
                "# Architecture\n\n## Persistence\n\nSQLite evidence.\n");
            artifactId = Assert.IsType<ParsedArtifactId>((await ParseAsync(first, versionId)).ArtifactId);
        }

        await using var restarted = BuildServices(directory.Path);
        await InitializeAsync(restarted);
        await using var scope = restarted.CreateAsyncScope();
        var reader = scope.ServiceProvider.GetRequiredService<IParsedEvidenceReader>();
        var artifact = await reader.GetCurrentArtifactAsync(versionId, CancellationToken.None);
        Assert.NotNull(artifact);
        Assert.Equal(artifactId, artifact.Id);
        var anchors = await reader.GetAnchorsAsync(artifactId, CancellationToken.None);
        var paragraph = Assert.Single(anchors, anchor => anchor.Kind == ParsedBlockKind.Paragraph);
        var locator = Assert.IsType<MarkdownSourceLocator>(paragraph.Locator);
        Assert.Equal((5, 5), (locator.StartLine, locator.EndLine));
        Assert.Equal(["Architecture", "Persistence"], locator.HeadingPath);
        Assert.Equal("SQLite evidence.", paragraph.NormalizedText);
    }

    [Fact]
    public async Task UnsupportedSourceIsDeferredWithoutAttemptOrFailure()
    {
        using var directory = new TemporaryDirectory();
        await using var services = BuildServices(directory.Path);
        await InitializeAsync(services);
        var versionId = await ImportAsync(
            services,
            "PDF",
            "source.pdf",
            "application/pdf",
            "%PDF-fake");

        var result = await ParseAsync(services, versionId);

        Assert.Equal(ParseSourceDisposition.Unsupported, result.Disposition);
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
        Assert.Equal(0, await context.ParsedArtifacts.CountAsync());
        Assert.Equal(0, await context.SourceAnchors.CountAsync());
        Assert.Equal(SourceProcessingState.PendingProcessing,
            (await context.SourceDocumentVersions.AsNoTracking().SingleAsync()).ProcessingState);
        var job = await context.ProcessingJobs.AsNoTracking().SingleAsync();
        Assert.Equal(ProcessingJobState.Pending, job.State);
        Assert.Equal(ProcessingStage.Parsing, job.Stage);
        Assert.Equal(0, job.AttemptCount);
    }

    [Fact]
    public async Task EightConcurrentRequestsCreateOneLogicalParseAndArtifactObject()
    {
        using var directory = new TemporaryDirectory();
        await using var services = BuildServices(directory.Path);
        await InitializeAsync(services);
        var versionId = await ImportAsync(services, "Concurrent", "concurrent.md", "text/markdown", "# One\n\nEvidence.\n");

        var requests = Enumerable.Range(0, 8).Select(_ => ParseAsync(services, versionId));
        var results = await Task.WhenAll(requests);

        Assert.Equal(1, results.Count(result => result.Disposition == ParseSourceDisposition.Parsed));
        Assert.All(results, result => Assert.Contains(
            result.Disposition,
            new[] { ParseSourceDisposition.Parsed, ParseSourceDisposition.Busy, ParseSourceDisposition.AlreadyParsed }));
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
        Assert.Equal(1, await context.ParsedArtifacts.CountAsync());
        Assert.Equal(2, await context.SourceAnchors.CountAsync());
        Assert.Equal(1, await context.ParsedArtifacts.CountAsync(artifact => artifact.IsCurrent));
        Assert.Equal(1, (await context.ProcessingJobs.AsNoTracking().SingleAsync()).AttemptCount);
        Assert.Single(FinalArtifactFiles(services));
    }

    [Fact]
    public async Task FailedParseRetriesSameBytesAndClearsErrorOnSuccess()
    {
        using var directory = new TemporaryDirectory();
        var parser = new FailOnceParser();
        await using var services = BuildServices(directory.Path, parser);
        await InitializeAsync(services);
        var versionId = await ImportAsync(services, "Retry", "retry.txt", "text/plain", "unchanged");

        var failed = await ParseAsync(services, versionId);
        var retried = await RetryAsync(services, versionId);

        Assert.Equal(ParseSourceDisposition.Failed, failed.Disposition);
        Assert.Equal("The source could not be parsed.", failed.Message);
        Assert.Equal(ParseSourceDisposition.Parsed, retried.Disposition);
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
        Assert.Equal(SourceProcessingState.Parsed,
            (await context.SourceDocumentVersions.AsNoTracking().SingleAsync()).ProcessingState);
        var job = await context.ProcessingJobs.AsNoTracking().SingleAsync();
        Assert.Equal(2, job.AttemptCount);
        Assert.Equal(ProcessingJobState.Pending, job.State);
        Assert.Equal(ProcessingStage.Chunking, job.Stage);
        Assert.Null(job.LastError);
        Assert.Equal(1, await context.ParsedArtifacts.CountAsync());
    }

    [Fact]
    public async Task UnexpectedParserFailurePropagatesAndLeavesParsingRetryableWithoutErrorText()
    {
        using var directory = new TemporaryDirectory();
        await using var services = BuildServices(directory.Path, new UnexpectedFailureParser());
        await InitializeAsync(services);
        var versionId = await ImportAsync(services, "Infrastructure", "infrastructure.txt", "text/plain", "private source text");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ParseAsync(services, versionId));

        Assert.Equal("injected infrastructure detail", exception.Message);
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
        Assert.Equal(SourceProcessingState.PendingProcessing,
            (await context.SourceDocumentVersions.AsNoTracking().SingleAsync()).ProcessingState);
        var job = await context.ProcessingJobs.AsNoTracking().SingleAsync();
        Assert.Equal(ProcessingJobState.Pending, job.State);
        Assert.Equal(ProcessingStage.Parsing, job.Stage);
        Assert.Equal(1, job.AttemptCount);
        Assert.Null(job.LastError);
        Assert.Equal(0, await context.ParsedArtifacts.CountAsync());
    }

    [Fact]
    public async Task ParsingDisposesImmutableSourceStream()
    {
        using var directory = new TemporaryDirectory();
        var probe = new DisposalProbeObjectStore();
        await using var services = BuildServices(directory.Path, objectStore: probe);
        await InitializeAsync(services);
        var versionId = await ImportAsync(services, "Dispose", "dispose.txt", "text/plain", "evidence");
        probe.TrackNextOpen = true;

        var result = await ParseAsync(services, versionId);

        Assert.Equal(ParseSourceDisposition.Parsed, result.Disposition);
        Assert.True(probe.LastOpenedStreamDisposed);
    }

    [Fact]
    public async Task CancellationReturnsSourceAndJobToRetryableParsingState()
    {
        using var directory = new TemporaryDirectory();
        var parser = new CancellableParser();
        await using var services = BuildServices(directory.Path, parser);
        await InitializeAsync(services);
        var versionId = await ImportAsync(services, "Cancel", "cancel.txt", "text/plain", "unchanged");
        using var cancellation = new CancellationTokenSource();

        var parsing = ParseAsync(services, versionId, cancellation.Token);
        await parser.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        var result = await parsing;

        Assert.Equal(ParseSourceDisposition.Cancelled, result.Disposition);
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
        Assert.Equal(SourceProcessingState.PendingProcessing,
            (await context.SourceDocumentVersions.AsNoTracking().SingleAsync()).ProcessingState);
        var job = await context.ProcessingJobs.AsNoTracking().SingleAsync();
        Assert.Equal(ProcessingJobState.Pending, job.State);
        Assert.Equal(ProcessingStage.Parsing, job.Stage);
        Assert.Equal(1, job.AttemptCount);
        Assert.Null(job.LastError);
        Assert.Equal(0, await context.ParsedArtifacts.CountAsync());
    }

    [Fact]
    public async Task ChangedFingerprintPreservesHistoricalArtifactAndAnchors()
    {
        using var directory = new TemporaryDirectory();
        SourceDocumentVersionId versionId;
        ParsedArtifactId firstArtifactId;
        await using (var first = BuildServices(directory.Path, new VersionedTestParser("1.0.0")))
        {
            await InitializeAsync(first);
            versionId = await ImportAsync(first, "Versioned", "versioned.txt", "text/plain", "evidence");
            firstArtifactId = Assert.IsType<ParsedArtifactId>((await ParseAsync(first, versionId)).ArtifactId);
        }

        await using var second = BuildServices(directory.Path, new VersionedTestParser("2.0.0"));
        await InitializeAsync(second);
        var secondResult = await ParseAsync(second, versionId);

        Assert.Equal(ParseSourceDisposition.Parsed, secondResult.Disposition);
        await using var scope = second.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
        var artifacts = await context.ParsedArtifacts.AsNoTracking().OrderBy(item => item.ParserVersion).ToListAsync();
        Assert.Equal(2, artifacts.Count);
        Assert.Equal(firstArtifactId, artifacts[0].Id);
        Assert.False(artifacts[0].IsCurrent);
        Assert.True(artifacts[1].IsCurrent);
        Assert.Equal(2, await context.SourceAnchors.CountAsync());
        Assert.Equal(2, FinalArtifactFiles(second).Count());
    }

    [Fact]
    public async Task SameFingerprintReturnsAlreadyParsedWithoutDuplicatesOrAttempt()
    {
        using var directory = new TemporaryDirectory();
        await using var services = BuildServices(directory.Path);
        await InitializeAsync(services);
        var versionId = await ImportAsync(services, "Stable", "stable.txt", "text/plain", "evidence");
        var first = await ParseAsync(services, versionId);

        var second = await ParseAsync(services, versionId);

        Assert.Equal(ParseSourceDisposition.AlreadyParsed, second.Disposition);
        Assert.Equal(first.ArtifactId, second.ArtifactId);
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
        Assert.Equal(1, await context.ParsedArtifacts.CountAsync());
        Assert.Equal(1, await context.SourceAnchors.CountAsync());
        Assert.Equal(1, (await context.ProcessingJobs.AsNoTracking().SingleAsync()).AttemptCount);
    }

    [Fact]
    public async Task DatabaseEnforcesFingerprintCurrentArtifactAndAnchorOrdinalUniqueness()
    {
        using var directory = new TemporaryDirectory();
        await using var services = BuildServices(directory.Path);
        await InitializeAsync(services);
        var versionId = await ImportAsync(services, "Constraints", "constraints.txt", "text/plain", "evidence");
        await ParseAsync(services, versionId);

        await using (var fingerprintScope = services.CreateAsyncScope())
        {
            var context = fingerprintScope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
            var existing = await context.ParsedArtifacts.AsNoTracking().SingleAsync();
            context.ParsedArtifacts.Add(CloneArtifact(
                existing,
                ParsedArtifactId.New(),
                existing.ParserFingerprint,
                isCurrent: false));
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }

        await using (var currentScope = services.CreateAsyncScope())
        {
            var context = currentScope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
            var existing = await context.ParsedArtifacts.AsNoTracking().SingleAsync();
            context.ParsedArtifacts.Add(CloneArtifact(
                existing,
                ParsedArtifactId.New(),
                new string('b', 64),
                isCurrent: true));
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }

        await using (var anchorScope = services.CreateAsyncScope())
        {
            var context = anchorScope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
            var existing = await context.SourceAnchors.AsNoTracking().SingleAsync();
            context.SourceAnchors.Add(new SourceAnchor(
                SourceAnchorId.New(),
                existing.ParsedArtifactId,
                existing.DocumentVersionId,
                existing.Ordinal,
                existing.Kind,
                existing.LocatorKind,
                existing.LocatorSchemaVersion,
                existing.LocatorJson,
                existing.NormalizedText,
                existing.NormalizedTextHash));
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task DatabaseRejectsAnchorWhoseVersionDoesNotOwnArtifact()
    {
        using var directory = new TemporaryDirectory();
        await using var services = BuildServices(directory.Path);
        await InitializeAsync(services);
        var artifactVersionId = await ImportAsync(
            services,
            "Artifact source",
            "artifact.txt",
            "text/plain",
            "evidence");
        await ParseAsync(services, artifactVersionId);
        var contradictoryVersionId = await ImportAsync(
            services,
            "Contradictory source",
            "contradictory.txt",
            "text/plain",
            "other evidence");

        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
        var existing = await context.SourceAnchors.AsNoTracking().SingleAsync();
        context.SourceAnchors.Add(new SourceAnchor(
            SourceAnchorId.New(),
            existing.ParsedArtifactId,
            contradictoryVersionId,
            existing.Ordinal + 1,
            existing.Kind,
            existing.LocatorKind,
            existing.LocatorSchemaVersion,
            existing.LocatorJson,
            existing.NormalizedText,
            existing.NormalizedTextHash));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Theory]
    [InlineData(ParseTransactionStage.AfterParserSuccess, false)]
    [InlineData(ParseTransactionStage.AfterArtifactFinalized, true)]
    [InlineData(ParseTransactionStage.AfterRelationalEntitiesAdded, true)]
    [InlineData(ParseTransactionStage.BeforeCommit, true)]
    public async Task InjectedFailureLeavesNoPartialRelationalEvidenceAndRetryableState(
        ParseTransactionStage stage,
        bool expectsOrphan)
    {
        using var directory = new TemporaryDirectory();
        await using var services = BuildServices(directory.Path, hook: new ThrowingParseHook(stage));
        await InitializeAsync(services);
        var versionId = await ImportAsync(services, "Rollback", "rollback.txt", "text/plain", "evidence");

        await Assert.ThrowsAsync<InjectedParseException>(() => ParseAsync(services, versionId));

        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
        Assert.Equal(0, await context.ParsedArtifacts.CountAsync());
        Assert.Equal(0, await context.SourceAnchors.CountAsync());
        Assert.Equal(SourceProcessingState.PendingProcessing,
            (await context.SourceDocumentVersions.AsNoTracking().SingleAsync()).ProcessingState);
        var job = await context.ProcessingJobs.AsNoTracking().SingleAsync();
        Assert.Equal(ProcessingJobState.Pending, job.State);
        Assert.Equal(ProcessingStage.Parsing, job.Stage);
        Assert.Equal(1, job.AttemptCount);
        Assert.Equal(expectsOrphan ? 1 : 0, FinalArtifactFiles(services).Count());
    }

    [Fact]
    public async Task SqliteFailureDuringAnchorInsertRollsBackArtifactAndCompleteAnchorSet()
    {
        using var directory = new TemporaryDirectory();
        await using var services = BuildServices(directory.Path);
        await InitializeAsync(services);
        var versionId = await ImportAsync(services, "Database failure", "database-failure.txt", "text/plain", "one\n\n two");
        await using (var triggerScope = services.CreateAsyncScope())
        {
            var context = triggerScope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
            await context.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER FailSourceAnchorInsert
                BEFORE INSERT ON SourceAnchors
                BEGIN
                    SELECT RAISE(ABORT, 'injected anchor failure');
                END;
                """);
        }

        await Assert.ThrowsAsync<DbUpdateException>(() => ParseAsync(services, versionId));

        await using var scope = services.CreateAsyncScope();
        var verification = scope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
        Assert.Equal(0, await verification.ParsedArtifacts.CountAsync());
        Assert.Equal(0, await verification.SourceAnchors.CountAsync());
        Assert.Equal(SourceProcessingState.PendingProcessing,
            (await verification.SourceDocumentVersions.AsNoTracking().SingleAsync()).ProcessingState);
        var job = await verification.ProcessingJobs.AsNoTracking().SingleAsync();
        Assert.Equal(ProcessingJobState.Pending, job.State);
        Assert.Equal(ProcessingStage.Parsing, job.Stage);
        Assert.Equal(1, job.AttemptCount);
        Assert.Null(job.LastError);
        Assert.Single(FinalArtifactFiles(services));
    }

    [Fact]
    public void LocatorCodecRoundTripsKnownTypesAndRejectsInvalidPersistencePayloads()
    {
        var codec = new JsonSourceLocatorCodec();
        var markdown = new MarkdownSourceLocator(2, 4, 3, ["Architecture", "Persistence"]);

        var json = codec.Serialize(markdown);
        var roundTrip = codec.Deserialize(SourceLocatorKind.Markdown, 1, json);

        var actual = Assert.IsType<MarkdownSourceLocator>(roundTrip);
        Assert.Equal((2, 4, 3), (actual.StartLine, actual.EndLine, actual.BlockOrdinal));
        Assert.Equal(markdown.HeadingPath, actual.HeadingPath);
        Assert.Throws<InvalidDataException>(() => codec.Deserialize(SourceLocatorKind.Markdown, 2, json));
        Assert.Throws<InvalidDataException>(() => codec.Deserialize(
            SourceLocatorKind.Text,
            1,
            "{\"startLine\":1,\"endLine\":1,\"arbitrary\":true}"));
    }

    [Fact]
    public async Task StartupRecoveryRepairsInterruptedParsingJobAndSourceTogether()
    {
        using var directory = new TemporaryDirectory();
        await using (var first = BuildServices(directory.Path))
        {
            await InitializeAsync(first);
            await ImportAsync(first, "Recover", "recover.md", "text/markdown", "# recover");
            await using var scope = first.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
            await context.ProcessingJobs.ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.State, ProcessingJobState.Processing)
                .SetProperty(job => job.Stage, ProcessingStage.Parsing)
                .SetProperty(job => job.AttemptCount, 3));
            await context.SourceDocumentVersions.ExecuteUpdateAsync(setters => setters
                .SetProperty(version => version.ProcessingState, SourceProcessingState.Parsing));
        }

        await using var restarted = BuildServices(directory.Path);
        await InitializeAsync(restarted);
        await using var restartedScope = restarted.CreateAsyncScope();
        var restartedContext = restartedScope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
        Assert.Equal(SourceProcessingState.PendingProcessing,
            (await restartedContext.SourceDocumentVersions.AsNoTracking().SingleAsync()).ProcessingState);
        var recovered = await restartedContext.ProcessingJobs.AsNoTracking().SingleAsync();
        Assert.Equal(ProcessingJobState.Pending, recovered.State);
        Assert.Equal(ProcessingStage.Parsing, recovered.Stage);
        Assert.Equal(3, recovered.AttemptCount);
        Assert.Null(recovered.LastError);
    }

    private static async Task<SourceDocumentVersionId> ImportAsync(
        IServiceProvider services,
        string displayName,
        string filename,
        string? mediaType,
        string content)
    {
        await using var scope = services.CreateAsyncScope();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content), writable: false);
        var result = await scope.ServiceProvider.GetRequiredService<ImportSourceService>().ImportAsync(
            new ImportSourceCommand(displayName, filename, mediaType, stream),
            CancellationToken.None);
        return result.VersionId;
    }

    private static async Task<ParseSourceResult> ParseAsync(
        IServiceProvider services,
        SourceDocumentVersionId versionId,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ParseSourceService>()
            .ParseAsync(versionId, cancellationToken);
    }

    private static async Task<ParseSourceResult> RetryAsync(
        IServiceProvider services,
        SourceDocumentVersionId versionId)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ParseSourceService>()
            .RetryAsync(versionId, CancellationToken.None);
    }

    private static async Task InitializeAsync(IServiceProvider services) =>
        await services.GetRequiredService<ILibraryInitializer>().InitializeAsync(CancellationToken.None);

    private static ServiceProvider BuildServices(
        string root,
        IDocumentParser? parser = null,
        IParseTransactionHook? hook = null,
        IObjectStore? objectStore = null)
    {
        var paths = new LocalLibraryPaths(root);
        var collection = new ServiceCollection();
        collection.AddSingleton<ILibraryPaths>(paths);
        collection.AddSingleton<ILibraryDirectoryInitializer, LocalLibraryInitializer>();
        collection.AddSingleton<IObjectStore>(objectStore ?? new LocalObjectStore(paths));
        collection.AddSingleton<IArtifactStore, LocalArtifactStore>();
        collection.AddLoregroveParsing();
        if (parser is not null)
        {
            collection.RemoveAll<IDocumentParser>();
            collection.AddSingleton(parser);
        }

        if (hook is not null)
        {
            collection.AddSingleton(hook);
        }

        collection.AddLoregroveSqlite(paths.Database);
        return collection.BuildServiceProvider();
    }

    private static IEnumerable<string> FinalArtifactFiles(IServiceProvider services)
    {
        var root = services.GetRequiredService<ILibraryPaths>().Artifacts;
        return Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
            : [];
    }

    private static ParsedArtifact CloneArtifact(
        ParsedArtifact artifact,
        ParsedArtifactId id,
        string parserFingerprint,
        bool isCurrent) => new(
            id,
            artifact.DocumentVersionId,
            artifact.SourceContentHash,
            artifact.ParserId,
            artifact.ParserVersion,
            artifact.ConfigurationFingerprint,
            parserFingerprint,
            artifact.SchemaVersion,
            artifact.ArtifactContentHash,
            artifact.ArtifactObjectKey,
            artifact.CreatedAt.AddSeconds(1),
            artifact.BlockCount,
            isCurrent);

    private class VersionedTestParser(string version) : IDocumentParser
    {
        public ParserDescriptor Descriptor { get; } = ParserDescriptor.Create(
            "test-text",
            version,
            1,
            "fixed");

        public bool CanParse(ParseSourceDescriptor source) => true;

        public virtual Task<ParsedDocumentResult> ParseAsync(
            Stream source,
            ParseSourceDescriptor descriptor,
            CancellationToken cancellationToken) => Task.FromResult(new ParsedDocumentResult(
                Descriptor,
                [new ParsedBlock(0, ParsedBlockKind.PlainText, $"observed-{version}", new TextSourceLocator(1, 1), [])],
                new Dictionary<string, string>()));
    }

    private sealed class FailOnceParser() : VersionedTestParser("retry")
    {
        private int _attempt;

        public override Task<ParsedDocumentResult> ParseAsync(
            Stream source,
            ParseSourceDescriptor descriptor,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _attempt) == 1)
            {
                throw new DocumentParseException("Deterministic parser failure.");
            }

            return base.ParseAsync(source, descriptor, cancellationToken);
        }
    }

    private sealed class UnexpectedFailureParser() : VersionedTestParser("unexpected")
    {
        public override Task<ParsedDocumentResult> ParseAsync(
            Stream source,
            ParseSourceDescriptor descriptor,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("injected infrastructure detail");
    }

    private sealed class CancellableParser() : VersionedTestParser("cancel")
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<ParsedDocumentResult> ParseAsync(
            Stream source,
            ParseSourceDescriptor descriptor,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return await base.ParseAsync(source, descriptor, cancellationToken);
        }
    }

    private sealed class ThrowingParseHook(ParseTransactionStage failureStage) : IParseTransactionHook
    {
        public Task OnStageAsync(ParseTransactionStage stage, CancellationToken cancellationToken)
        {
            if (stage == failureStage)
            {
                throw new InjectedParseException(stage);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class InjectedParseException(ParseTransactionStage stage)
        : Exception($"Injected parsing failure at {stage}.");

    private sealed class DisposalProbeObjectStore : IObjectStore
    {
        private readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

        public bool TrackNextOpen { get; set; }

        public bool LastOpenedStreamDisposed { get; private set; }

        public async Task<StoredObject> StoreAsync(Stream content, CancellationToken cancellationToken)
        {
            using var memory = new MemoryStream();
            await content.CopyToAsync(memory, cancellationToken);
            var bytes = memory.ToArray();
            var hash = ParsedArtifactSerializer.HashBytes(bytes);
            var key = $"{hash[..2]}/{hash}";
            _objects[key] = bytes;
            return new StoredObject(hash, key, bytes.Length);
        }

        public Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TrackNextOpen)
            {
                return Task.FromResult<Stream>(new MemoryStream(_objects[objectKey], writable: false));
            }

            TrackNextOpen = false;
            return Task.FromResult<Stream>(new DisposalProbeStream(
                _objects[objectKey],
                () => LastOpenedStreamDisposed = true));
        }

        public Task<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken) =>
            Task.FromResult(_objects.ContainsKey(objectKey));

        private sealed class DisposalProbeStream(byte[] bytes, Action disposed)
            : MemoryStream(bytes, writable: false)
        {
            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    disposed();
                }

                base.Dispose(disposing);
            }
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "loregrove-parsing-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A failed test should report its assertion rather than cleanup noise.
            }
            catch (UnauthorizedAccessException)
            {
                // A failed test should report its assertion rather than cleanup noise.
            }
        }
    }
}
