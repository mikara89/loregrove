using Loregrove.Application.Chunking;
using Loregrove.Application.Parsing;
using Loregrove.Application.Processing;
using Loregrove.Application.Search;
using Loregrove.Application.Storage;
using Loregrove.Domain.Sources;
using Loregrove.Infrastructure.Search;
using Loregrove.Infrastructure.Sqlite;
using Loregrove.Infrastructure.Sqlite.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Loregrove.IntegrationTests;

public sealed class ChunkingAndSearchIntegrationTests
{
    [Fact]
    public async Task EightConcurrentChunkRequestsCommitOneCompleteSearchableDerivation()
    {
        using var database = TemporaryDatabase.Create();
        var artifacts = new MemoryArtifactStore();
        await using var services = CreateServices(database.Path, artifacts);
        SourceDocumentVersionId versionId;
        await using (var seedScope = services.CreateAsyncScope())
        {
            var seedContext = seedScope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
            await seedContext.Database.MigrateAsync();
            versionId = await SeedParsedAsync(seedContext, artifacts,
                seedScope.ServiceProvider.GetRequiredService<ISourceLocatorCodec>());
        }

        var requests = Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var scope = services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ChunkSourceService>()
                .ChunkAsync(versionId, CancellationToken.None);
        });
        var results = await Task.WhenAll(requests);

        Assert.Single(results, result => result.Disposition == ChunkSourceDisposition.Chunked);
        Assert.All(results, result => Assert.Contains(
            result.Disposition,
            new[] { ChunkSourceDisposition.Chunked, ChunkSourceDisposition.Busy, ChunkSourceDisposition.AlreadyChunked }));
        await using var verifyScope = services.CreateAsyncScope();
        var context = verifyScope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
        Assert.Equal(1, await context.ChunkSets.CountAsync());
        Assert.Equal(1, await context.ChunkSets.CountAsync(set => set.IsCurrent));
        Assert.Equal(2, await context.Chunks.CountAsync());
        Assert.Equal(2, await context.ChunkEvidenceSpans.CountAsync());
        Assert.Equal(3, await context.LexicalSearchEntries.CountAsync());
        var version = await context.SourceDocumentVersions.AsNoTracking().SingleAsync();
        var job = await context.ProcessingJobs.AsNoTracking().SingleAsync();
        Assert.Equal(SourceProcessingState.Chunked, version.ProcessingState);
        Assert.Equal(ProcessingJobState.Pending, job.State);
        Assert.Equal(ProcessingStage.Embedding, job.Stage);
        Assert.Equal(1, job.AttemptCount);
        var search = verifyScope.ServiceProvider.GetRequiredService<ILexicalSearchService>();
        var page = await search.SearchAsync(new LexicalSearchQuery("sqlite"), CancellationToken.None);
        Assert.Single(page.Items);
        Assert.NotEmpty(page.Items[0].SourceAnchorIds);
    }

    [Fact]
    public async Task FailedChunkingRetriesWithoutReparsingOrDuplicateSets()
    {
        using var database = TemporaryDatabase.Create();
        var artifacts = new MemoryArtifactStore();
        var chunker = new FailOnceChunker();
        await using var services = CreateServices(database.Path, artifacts, chunker);
        SourceDocumentVersionId versionId;
        await using (var seedScope = services.CreateAsyncScope())
        {
            var context = seedScope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
            await context.Database.MigrateAsync();
            versionId = await SeedParsedAsync(context, artifacts,
                seedScope.ServiceProvider.GetRequiredService<ISourceLocatorCodec>());
        }

        await using (var firstScope = services.CreateAsyncScope())
        {
            var failed = await firstScope.ServiceProvider.GetRequiredService<ChunkSourceService>()
                .ChunkAsync(versionId, CancellationToken.None);
            Assert.Equal(ChunkSourceDisposition.Failed, failed.Disposition);
        }

        await using (var retryScope = services.CreateAsyncScope())
        {
            var retried = await retryScope.ServiceProvider.GetRequiredService<ChunkSourceService>()
                .RetryAsync(versionId, CancellationToken.None);
            Assert.Equal(ChunkSourceDisposition.Chunked, retried.Disposition);
            var context = retryScope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
            Assert.Equal(1, await context.ChunkSets.CountAsync());
            var job = await context.ProcessingJobs.AsNoTracking().SingleAsync();
            Assert.Equal(2, job.AttemptCount);
            Assert.Null(job.LastError);
            Assert.Equal(ProcessingStage.Embedding, job.Stage);
        }
    }

    [Fact]
    public async Task CancellationBeforeCommitLeavesNoPartialDerivationAndReturnsToPendingChunking()
    {
        using var database = TemporaryDatabase.Create();
        var artifacts = new MemoryArtifactStore();
        using var cancellation = new CancellationTokenSource();
        await using var services = CreateServices(
            database.Path,
            artifacts,
            transactionHook: new CancelingChunkHook(cancellation));
        SourceDocumentVersionId versionId;
        await using (var seedScope = services.CreateAsyncScope())
        {
            var context = seedScope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
            await context.Database.MigrateAsync();
            versionId = await SeedParsedAsync(context, artifacts,
                seedScope.ServiceProvider.GetRequiredService<ISourceLocatorCodec>());
        }

        await using var scope = services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ChunkSourceService>()
            .ChunkAsync(versionId, cancellation.Token);
        Assert.Equal(ChunkSourceDisposition.Cancelled, result.Disposition);
        var verify = scope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
        Assert.Equal(0, await verify.ChunkSets.CountAsync());
        Assert.Equal(0, await verify.Chunks.CountAsync());
        Assert.Equal(0, await verify.ChunkEvidenceSpans.CountAsync());
        Assert.Equal(0, await verify.LexicalSearchEntries.CountAsync());
        Assert.Equal(SourceProcessingState.Parsed,
            (await verify.SourceDocumentVersions.AsNoTracking().SingleAsync()).ProcessingState);
        var job = await verify.ProcessingJobs.AsNoTracking().SingleAsync();
        Assert.Equal(ProcessingJobState.Pending, job.State);
        Assert.Equal(ProcessingStage.Chunking, job.Stage);
        Assert.Equal(1, job.AttemptCount);
    }

    [Fact]
    public async Task RestartRecoveryReturnsInterruptedChunkingToParsedPendingState()
    {
        using var database = TemporaryDatabase.Create();
        await using var services = CreateServices(database.Path);
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
        await context.Database.MigrateAsync();
        var now = DateTimeOffset.UtcNow;
        var documentId = SourceDocumentId.New();
        var versionId = SourceDocumentVersionId.New();
        var hash = new string('e', 64);
        context.SourceDocuments.Add(new SourceDocument(documentId, "Interrupted", SourceKind.File, now, versionId));
        context.SourceDocumentVersions.Add(new SourceDocumentVersion(
            versionId, documentId, hash, "interrupted.txt", "text/plain", 1, now, $"ee/{hash}", null,
            SourceProcessingState.Chunking));
        context.ProcessingJobs.Add(new ProcessingJob(
            ProcessingJobId.New(), versionId, ProcessingJobState.Processing, now, 3, now,
            stage: ProcessingStage.Chunking));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var recovered = await scope.ServiceProvider.GetRequiredService<IProcessingJobRecovery>()
            .RecoverInterruptedJobsAsync(CancellationToken.None);

        Assert.Equal(1, recovered);
        Assert.Equal(SourceProcessingState.Parsed,
            (await context.SourceDocumentVersions.AsNoTracking().SingleAsync()).ProcessingState);
        var job = await context.ProcessingJobs.AsNoTracking().SingleAsync();
        Assert.Equal(ProcessingJobState.Pending, job.State);
        Assert.Equal(ProcessingStage.Chunking, job.Stage);
        Assert.Equal(3, job.AttemptCount);
    }

    [Fact]
    public async Task ChangedChunkerProfilePreservesHistoryAndReplacesOnlyCurrentSearchEntries()
    {
        using var database = TemporaryDatabase.Create();
        var artifacts = new MemoryArtifactStore();
        SourceDocumentVersionId versionId;
        await using (var firstServices = CreateServices(database.Path, artifacts))
        {
            await using var seedScope = firstServices.CreateAsyncScope();
            var context = seedScope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
            await context.Database.MigrateAsync();
            versionId = await SeedParsedAsync(context, artifacts,
                seedScope.ServiceProvider.GetRequiredService<ISourceLocatorCodec>());
            var first = await seedScope.ServiceProvider.GetRequiredService<ChunkSourceService>()
                .ChunkAsync(versionId, CancellationToken.None);
            Assert.Equal(ChunkSourceDisposition.Chunked, first.Disposition);
            var unchanged = await seedScope.ServiceProvider.GetRequiredService<ChunkSourceService>()
                .RechunkAsync(versionId, CancellationToken.None);
            Assert.Equal(ChunkSourceDisposition.AlreadyChunked, unchanged.Disposition);
            Assert.Equal(first.ChunkSetId, unchanged.ChunkSetId);
        }

        SqliteConnection.ClearAllPools();
        var changedChunker = new EvidenceAwareChunker(new EvidenceAwareChunkerOptions(80, 160, 20, 0));
        await using var secondServices = CreateServices(database.Path, artifacts, changedChunker);
        await using var secondScope = secondServices.CreateAsyncScope();
        var changed = await secondScope.ServiceProvider.GetRequiredService<ChunkSourceService>()
            .RechunkAsync(versionId, CancellationToken.None);
        Assert.Equal(ChunkSourceDisposition.Chunked, changed.Disposition);
        var verify = secondScope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
        Assert.Equal(2, await verify.ChunkSets.CountAsync());
        var currentSet = await verify.ChunkSets.AsNoTracking().SingleAsync(set => set.IsCurrent);
        Assert.Equal(changedChunker.Descriptor.Fingerprint, currentSet.ChunkerFingerprint);
        Assert.Equal(1, await verify.ChunkSets.CountAsync(set => !set.IsCurrent));
        var indexedChunkIds = await verify.LexicalSearchEntries.AsNoTracking()
            .Where(entry => entry.ChunkId != null)
            .Select(entry => entry.ChunkId!.Value)
            .ToArrayAsync();
        var currentChunkIds = await verify.Chunks.AsNoTracking()
            .Where(chunk => chunk.ChunkSetId == currentSet.Id)
            .Select(chunk => chunk.Id)
            .ToArrayAsync();
        Assert.Equal(currentChunkIds.OrderBy(id => id.Value), indexedChunkIds.OrderBy(id => id.Value));
        Assert.Equal(SourceProcessingState.Chunked,
            (await verify.SourceDocumentVersions.AsNoTracking().SingleAsync()).ProcessingState);
        var job = await verify.ProcessingJobs.AsNoTracking().SingleAsync();
        Assert.Equal(ProcessingJobState.Pending, job.State);
        Assert.Equal(ProcessingStage.Embedding, job.Stage);
        Assert.Equal(2, job.AttemptCount);
    }

    [Fact]
    public Task ChunkerWithInconsistentSpanLengthsIsRejectedBeforePersistence() =>
        AssertInvalidChunkerRejectedAsync(InvalidChunkerMode.InconsistentSpanLength);

    [Fact]
    public Task ChunkerWithValidLengthWrongTextIsRejectedBeforePersistence() =>
        AssertInvalidChunkerRejectedAsync(InvalidChunkerMode.WrongText);

    [Fact]
    public async Task SurrogatePairsRemainIntactAfterChunkPersistenceAndReload()
    {
        using var database = TemporaryDatabase.Create();
        var artifacts = new MemoryArtifactStore();
        var text = new string('a', 1999) + "😀" + new string('b', 2500);
        SourceDocumentVersionId versionId;
        string[] expectedChunkTexts;
        string[] expectedContentHashes;
        await using (var services = CreateServices(database.Path, artifacts))
        {
            await using var scope = services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
            await context.Database.MigrateAsync();
            versionId = await SeedParsedAsync(
                context,
                artifacts,
                scope.ServiceProvider.GetRequiredService<ISourceLocatorCodec>(),
                [new ParsedBlock(
                    0,
                    ParsedBlockKind.Paragraph,
                    text,
                    new TextSourceLocator(1, 1),
                    ["Unicode"])]);
            var result = await scope.ServiceProvider.GetRequiredService<ChunkSourceService>()
                .ChunkAsync(versionId, CancellationToken.None);
            Assert.Equal(ChunkSourceDisposition.Chunked, result.Disposition);
            var persisted = await context.Chunks.AsNoTracking()
                .Where(chunk => chunk.DocumentVersionId == versionId)
                .OrderBy(chunk => chunk.Ordinal)
                .ToArrayAsync();
            expectedChunkTexts = persisted.Select(chunk => chunk.Text).ToArray();
            expectedContentHashes = persisted.Select(chunk => chunk.ContentHash).ToArray();
        }

        SqliteConnection.ClearAllPools();
        await using var reloadedServices = CreateServices(database.Path, artifacts);
        await using var reloadedScope = reloadedServices.CreateAsyncScope();
        var reloaded = reloadedScope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
        var chunks = await reloaded.Chunks.AsNoTracking()
            .Where(chunk => chunk.DocumentVersionId == versionId)
            .OrderBy(chunk => chunk.Ordinal)
            .ToArrayAsync();
        Assert.True(chunks.Length >= 3);
        Assert.All(chunks, chunk => Assert.True(HasOnlyValidSurrogatePairs(chunk.Text)));
        Assert.Equal(expectedChunkTexts, chunks.Select(chunk => chunk.Text));
        Assert.Equal(expectedContentHashes, chunks.Select(chunk => chunk.ContentHash));
        Assert.Equal(text, string.Concat(chunks.Select(chunk => chunk.Text)));
        Assert.All(chunks, chunk => Assert.Equal(
            ParsedArtifactSerializer.HashText($"{chunk.ContextText}\n\n{chunk.Text}"),
            chunk.ContentHash));
        var anchorText = await reloaded.SourceAnchors.AsNoTracking()
            .Where(anchor => anchor.DocumentVersionId == versionId)
            .Select(anchor => anchor.NormalizedText)
            .SingleAsync();
        var evidenceRanges = await (
            from span in reloaded.ChunkEvidenceSpans.AsNoTracking()
            join chunk in reloaded.Chunks.AsNoTracking() on span.ChunkId equals chunk.Id
            where chunk.DocumentVersionId == versionId
            orderby chunk.Ordinal, span.Ordinal
            select new { span.AnchorStart, span.AnchorEnd })
            .ToArrayAsync();
        Assert.Equal(text, string.Concat(evidenceRanges.Select(
            range => anchorText[range.AnchorStart..range.AnchorEnd])));
    }

    [Fact]
    public async Task ChangedParsedArtifactCreatesNewCurrentSetWithSameChunker()
    {
        using var database = TemporaryDatabase.Create();
        var artifacts = new MemoryArtifactStore();
        await using var services = CreateServices(database.Path, artifacts);
        SourceDocumentVersionId versionId;
        await using (var firstScope = services.CreateAsyncScope())
        {
            var context = firstScope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
            await context.Database.MigrateAsync();
            versionId = await SeedParsedAsync(context, artifacts,
                firstScope.ServiceProvider.GetRequiredService<ISourceLocatorCodec>());
            Assert.Equal(ChunkSourceDisposition.Chunked,
                (await firstScope.ServiceProvider.GetRequiredService<ChunkSourceService>()
                    .ChunkAsync(versionId, CancellationToken.None)).Disposition);
        }

        ParsedArtifactId replacementId;
        await using (var replaceScope = services.CreateAsyncScope())
        {
            replacementId = await AddReplacementParsedAsync(
                replaceScope.ServiceProvider.GetRequiredService<LoregroveDbContext>(),
                artifacts,
                replaceScope.ServiceProvider.GetRequiredService<ISourceLocatorCodec>(),
                versionId);
        }

        await using var chunkScope = services.CreateAsyncScope();
        var result = await chunkScope.ServiceProvider.GetRequiredService<ChunkSourceService>()
            .ChunkAsync(versionId, CancellationToken.None);
        Assert.Equal(ChunkSourceDisposition.Chunked, result.Disposition);
        var verify = chunkScope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
        Assert.Equal(2, await verify.ChunkSets.CountAsync());
        Assert.Equal(replacementId, (await verify.ChunkSets.AsNoTracking().SingleAsync(set => set.IsCurrent)).ParsedArtifactId);
        var search = chunkScope.ServiceProvider.GetRequiredService<ILexicalSearchService>();
        Assert.Single((await search.SearchAsync(new LexicalSearchQuery("replacement"), CancellationToken.None)).Items);
        Assert.Empty((await search.SearchAsync(new LexicalSearchQuery("sqlite"), CancellationToken.None)).Items);
    }

    [Fact]
    public async Task Fts5SearchIsUnicodeSafeRankedPagedAndRebuildable()
    {
        using var database = TemporaryDatabase.Create();
        await using var services = CreateServices(database.Path);
        SeededEvidence seeded;
        await using (var seedScope = services.CreateAsyncScope())
        {
            var context = seedScope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
            await context.Database.MigrateAsync();
            seeded = await SeedAsync(context);
        }

        await using var searchScope = services.CreateAsyncScope();
        var search = searchScope.ServiceProvider.GetRequiredService<ILexicalSearchService>();
        var titleAndHeading = await search.SearchAsync(new LexicalSearchQuery("architecture"), CancellationToken.None);
        Assert.Equal(2, titleAndHeading.TotalCount);
        Assert.Equal(SearchTargetKind.Source, titleAndHeading.Items[0].Kind);
        Assert.Equal(SearchTargetKind.Chunk, titleAndHeading.Items[1].Kind);
        Assert.Equal(seeded.AnchorId, Assert.Single(titleAndHeading.Items[1].SourceAnchorIds));

        var diacritics = await search.SearchAsync(new LexicalSearchQuery("resume"), CancellationToken.None);
        Assert.Contains(diacritics.Items, result => result.SourceName == "Résumé-Architecture.txt");
        var unicode = await search.SearchAsync(new LexicalSearchQuery("čćž"), CancellationToken.None);
        Assert.Single(unicode.Items);

        var unsafeMarkup = await search.SearchAsync(new LexicalSearchQuery("alert"), CancellationToken.None);
        Assert.Single(unsafeMarkup.Items);
        Assert.Contains("alert", unsafeMarkup.Items[0].Snippet, StringComparison.OrdinalIgnoreCase);

        foreach (var grammar in new[] { "\"", "'", "AND", "OR", "NOT", "NEAR", "*", ":", "-", "(foo)", "a+b", "C++", "name:value" })
        {
            var exception = await Record.ExceptionAsync(() =>
                search.SearchAsync(new LexicalSearchQuery(grammar), CancellationToken.None));
            Assert.Null(exception);
        }

        var firstPage = await search.SearchAsync(new LexicalSearchQuery("architecture", 1, 10), CancellationToken.None);
        var before = firstPage.Items.Select(item => (item.Kind, item.SourceDocumentId, item.ChunkId)).ToArray();
        await searchScope.ServiceProvider.GetRequiredService<ILexicalSearchMaintenance>()
            .RebuildAsync(CancellationToken.None);
        var rebuilt = await search.SearchAsync(new LexicalSearchQuery("architecture", 1, 10), CancellationToken.None);
        Assert.Equal(before, rebuilt.Items.Select(item => (item.Kind, item.SourceDocumentId, item.ChunkId)).ToArray());
    }

    [Fact]
    public async Task DatabaseRejectsCrossArtifactChunkEvidence()
    {
        using var database = TemporaryDatabase.Create();
        await using var services = CreateServices(database.Path);
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
        await context.Database.MigrateAsync();
        var first = await SeedAsync(context);
        var second = await AddEvidenceOnlyAsync(context, "second.txt", "second evidence");

        context.ChunkEvidenceSpans.Add(new ChunkEvidenceSpan(
            first.ChunkId,
            99,
            second.AnchorId,
            second.ArtifactId,
            second.VersionId,
            0,
            6,
            0,
            6));

        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.IsType<SqliteException>(failure.InnerException);
    }

    [Fact]
    public async Task FtsQueryPlanUsesTheVirtualTableMatchIndex()
    {
        using var database = TemporaryDatabase.Create();
        await using var services = CreateServices(database.Path);
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
        await context.Database.MigrateAsync();
        await SeedAsync(context);
        await context.Database.OpenConnectionAsync();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN SELECT rowid FROM LexicalSearchFts WHERE LexicalSearchFts MATCH 'architecture';";
        var details = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            details.Add(reader.GetString(3));
        }

        Assert.Contains(details, detail => detail.Contains("VIRTUAL TABLE INDEX", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TenThousandChunkProjectionUsesBoundedDatabasePaging()
    {
        const int chunkCount = 10_000;
        using var database = TemporaryDatabase.Create();
        await using var services = CreateServices(database.Path);
        await using (var scope = services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
            await context.Database.MigrateAsync();
            var now = DateTimeOffset.UtcNow;
            var documentId = SourceDocumentId.New();
            var versionId = SourceDocumentVersionId.New();
            var artifactId = ParsedArtifactId.New();
            var setId = ChunkSetId.New();
            var hash = new string('d', 64);
            context.SourceDocuments.Add(new SourceDocument(documentId, "Bulk", SourceKind.File, now, versionId));
            context.SourceDocumentVersions.Add(new SourceDocumentVersion(
                versionId, documentId, hash, "bulk.txt", "text/plain", chunkCount, now, $"dd/{hash}", null,
                SourceProcessingState.Chunked));
            context.ProcessingJobs.Add(new ProcessingJob(
                ProcessingJobId.New(), versionId, ProcessingJobState.Pending, now, 2, now, stage: ProcessingStage.Embedding));
            context.ParsedArtifacts.Add(new ParsedArtifact(
                artifactId, versionId, hash, "bulk", "1", hash, hash, 1, hash, $"parsed/{hash}", now, 0, true));
            context.ChunkSets.Add(new ChunkSet(
                setId, versionId, artifactId, "bulk", "1", 1, hash, hash, now, chunkCount, true));
            for (var index = 0; index < chunkCount; index++)
            {
                var body = $"needle evidence passage {index:D5}";
                var contentHash = ParsedArtifactSerializer.HashText(body);
                var chunk = new Chunk(
                    ChunkId.New(), setId, versionId, artifactId, index, contentHash, body, "Bulk", contentHash, body.Length);
                context.Chunks.Add(chunk);
                context.LexicalSearchEntries.Add(new LexicalSearchEntry(
                    LexicalSearchEntryKind.Chunk, documentId, versionId, chunk.Id, "bulk.txt",
                    string.Empty, "Bulk", body));
            }

            await context.SaveChangesAsync();
        }

        await using var searchScope = services.CreateAsyncScope();
        var search = searchScope.ServiceProvider.GetRequiredService<ILexicalSearchService>();
        var page = await search.SearchAsync(new LexicalSearchQuery("needle", 2, 25), CancellationToken.None);
        Assert.Equal(chunkCount, page.TotalCount);
        Assert.Equal(25, page.Items.Count);
        Assert.Equal(2, page.Page);
    }

    private static ServiceProvider CreateServices(
        string databasePath,
        IArtifactStore? artifactStore = null,
        IChunker? chunker = null,
        IChunkTransactionHook? transactionHook = null)
    {
        var services = new ServiceCollection();
        if (artifactStore is not null)
        {
            services.AddSingleton<IArtifactStore>(artifactStore);
        }

        services.AddLoregroveSqlite(databasePath);
        if (chunker is not null)
        {
            services.AddSingleton(chunker);
        }

        if (transactionHook is not null)
        {
            services.AddScoped(_ => transactionHook);
        }

        services.AddLoregroveSearch();
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static async Task<SourceDocumentVersionId> SeedParsedAsync(
        LoregroveDbContext context,
        MemoryArtifactStore artifactStore,
        ISourceLocatorCodec locatorCodec,
        IReadOnlyList<ParsedBlock>? customBlocks = null)
    {
        var now = DateTimeOffset.UtcNow;
        var documentId = SourceDocumentId.New();
        var versionId = SourceDocumentVersionId.New();
        var descriptor = ParserDescriptor.Create("test", "1.0", 1, "fixture");
        var blocks = customBlocks?.ToArray() ??
        [
            new ParsedBlock(0, ParsedBlockKind.Paragraph, "SQLite persistence evidence", new TextSourceLocator(1, 1), ["Architecture"]),
            new ParsedBlock(1, ParsedBlockKind.Paragraph, "Security evidence", new TextSourceLocator(2, 2), ["Security"]),
        ];
        var parsed = new ParsedDocumentResult(descriptor, blocks, new Dictionary<string, string>());
        var source = new ParseSourceDescriptor(versionId, new string('c', 64), "concurrent.txt", "text/plain");
        var serialized = ParsedArtifactSerializer.Serialize(source, parsed);
        var objectKey = artifactStore.Add(serialized.Bytes);
        var artifactId = ParsedArtifactId.New();
        context.SourceDocuments.Add(new SourceDocument(documentId, "Concurrent", SourceKind.File, now, versionId));
        context.SourceDocumentVersions.Add(new SourceDocumentVersion(
            versionId, documentId, source.ContentHash, source.OriginalFileName, source.MediaType,
            serialized.Bytes.Length, now, $"cc/{source.ContentHash}", null, SourceProcessingState.Parsed));
        context.ProcessingJobs.Add(new ProcessingJob(
            ProcessingJobId.New(), versionId, ProcessingJobState.Pending, now, 0, now, stage: ProcessingStage.Chunking));
        context.ParsedArtifacts.Add(new ParsedArtifact(
            artifactId, versionId, source.ContentHash, descriptor.Id, descriptor.Version,
            descriptor.ConfigurationFingerprint, descriptor.Fingerprint, descriptor.OutputSchemaVersion,
            serialized.ContentHash, objectKey, now, blocks.Length, true));
        context.SourceAnchors.AddRange(blocks.Select(block => new SourceAnchor(
            SourceAnchorId.New(), artifactId, versionId, block.Ordinal, block.Kind, block.Locator.Kind,
            block.Locator.SchemaVersion, locatorCodec.Serialize(block.Locator), block.Text,
            ParsedArtifactSerializer.HashText(block.Text))));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return versionId;
    }

    private static async Task AssertInvalidChunkerRejectedAsync(InvalidChunkerMode mode)
    {
        using var database = TemporaryDatabase.Create();
        var artifacts = new MemoryArtifactStore();
        await using var services = CreateServices(database.Path, artifacts, new InvalidOutputChunker(mode));
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
        await context.Database.MigrateAsync();
        var versionId = await SeedParsedAsync(
            context,
            artifacts,
            scope.ServiceProvider.GetRequiredService<ISourceLocatorCodec>());

        var result = await scope.ServiceProvider.GetRequiredService<ChunkSourceService>()
            .ChunkAsync(versionId, CancellationToken.None);

        Assert.Equal(ChunkSourceDisposition.Failed, result.Disposition);
        Assert.Empty(await context.ChunkSets.AsNoTracking().ToArrayAsync());
        Assert.Empty(await context.Chunks.AsNoTracking().ToArrayAsync());
        Assert.Empty(await context.ChunkEvidenceSpans.AsNoTracking().ToArrayAsync());
        Assert.Empty(await context.LexicalSearchEntries.AsNoTracking().ToArrayAsync());
        Assert.Equal(SourceProcessingState.Parsed,
            (await context.SourceDocumentVersions.AsNoTracking().SingleAsync()).ProcessingState);
        var job = await context.ProcessingJobs.AsNoTracking().SingleAsync();
        Assert.Equal(ProcessingJobState.Failed, job.State);
        Assert.Equal(ProcessingStage.Chunking, job.Stage);
        Assert.Equal(1, job.AttemptCount);
    }

    private static bool HasOnlyValidSurrogatePairs(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]))
            {
                if (++index >= text.Length || !char.IsLowSurrogate(text[index]))
                {
                    return false;
                }
            }
            else if (char.IsLowSurrogate(text[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<ParsedArtifactId> AddReplacementParsedAsync(
        LoregroveDbContext context,
        MemoryArtifactStore artifactStore,
        ISourceLocatorCodec locatorCodec,
        SourceDocumentVersionId versionId)
    {
        var version = await context.SourceDocumentVersions.AsNoTracking().SingleAsync(item => item.Id == versionId);
        var descriptor = ParserDescriptor.Create("test", "2.0", 1, "replacement-fixture");
        var block = new ParsedBlock(
            0,
            ParsedBlockKind.Paragraph,
            "Replacement parsed evidence",
            new TextSourceLocator(1, 1),
            ["Replacement"]);
        var parsed = new ParsedDocumentResult(descriptor, [block], new Dictionary<string, string>());
        var source = new ParseSourceDescriptor(versionId, version.ContentHash, version.OriginalFileName, version.MediaType);
        var serialized = ParsedArtifactSerializer.Serialize(source, parsed);
        var artifactId = ParsedArtifactId.New();
        await context.ParsedArtifacts.Where(artifact => artifact.DocumentVersionId == versionId && artifact.IsCurrent)
            .ExecuteUpdateAsync(setters => setters.SetProperty(artifact => artifact.IsCurrent, false));
        context.ParsedArtifacts.Add(new ParsedArtifact(
            artifactId, versionId, version.ContentHash, descriptor.Id, descriptor.Version,
            descriptor.ConfigurationFingerprint, descriptor.Fingerprint, descriptor.OutputSchemaVersion,
            serialized.ContentHash, artifactStore.Add(serialized.Bytes), DateTimeOffset.UtcNow, 1, true));
        context.SourceAnchors.Add(new SourceAnchor(
            SourceAnchorId.New(), artifactId, versionId, 0, block.Kind, block.Locator.Kind,
            block.Locator.SchemaVersion, locatorCodec.Serialize(block.Locator), block.Text,
            ParsedArtifactSerializer.HashText(block.Text)));
        await context.SaveChangesAsync();
        await context.SourceDocumentVersions.Where(item => item.Id == versionId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                item => item.ProcessingState,
                SourceProcessingState.Parsed));
        await context.ProcessingJobs.Where(job => job.DocumentVersionId == versionId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.State, ProcessingJobState.Pending)
                .SetProperty(job => job.Stage, ProcessingStage.Chunking));
        context.ChangeTracker.Clear();
        return artifactId;
    }

    private static async Task<SeededEvidence> SeedAsync(LoregroveDbContext context)
    {
        var now = DateTimeOffset.UtcNow;
        var documentId = SourceDocumentId.New();
        var versionId = SourceDocumentVersionId.New();
        var artifactId = ParsedArtifactId.New();
        var anchorId = SourceAnchorId.New();
        var setId = ChunkSetId.New();
        var chunkId = ChunkId.New();
        const string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string body = "Architecture body with SQLite and Serbian čćž evidence. <script>alert('x')</script>";
        context.SourceDocuments.Add(new SourceDocument(documentId, "Résumé Architecture", SourceKind.File, now, versionId));
        context.SourceDocumentVersions.Add(new SourceDocumentVersion(
            versionId, documentId, hash, "Résumé-Architecture.txt", "text/plain", body.Length, now, $"aa/{hash}", null,
            SourceProcessingState.Chunked));
        context.ProcessingJobs.Add(new ProcessingJob(
            ProcessingJobId.New(), versionId, ProcessingJobState.Pending, now, 2, now, stage: ProcessingStage.Embedding));
        context.ParsedArtifacts.Add(new ParsedArtifact(
            artifactId, versionId, hash, "test", "1", hash, hash, 1, hash, $"parsed/{hash}", now, 1, true));
        context.SourceAnchors.Add(new SourceAnchor(
            anchorId, artifactId, versionId, 0, ParsedBlockKind.Paragraph, SourceLocatorKind.Text, 1, "{}", body, hash));
        context.ChunkSets.Add(new ChunkSet(
            setId, versionId, artifactId, "test", "1", 1, hash, hash, now, 1, true));
        context.Chunks.Add(new Chunk(
            chunkId, setId, versionId, artifactId, 0, hash, body, "Architecture › SQLite", hash, body.Length));
        context.ChunkEvidenceSpans.Add(new ChunkEvidenceSpan(
            chunkId, 0, anchorId, artifactId, versionId, 0, body.Length, 0, body.Length));
        context.LexicalSearchEntries.Add(new LexicalSearchEntry(
            LexicalSearchEntryKind.Source, documentId, versionId, null, "Résumé-Architecture.txt",
            "Résumé-Architecture.txt", string.Empty, string.Empty));
        context.LexicalSearchEntries.Add(new LexicalSearchEntry(
            LexicalSearchEntryKind.Chunk, documentId, versionId, chunkId, "Résumé-Architecture.txt",
            string.Empty, "Architecture › SQLite", body));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return new SeededEvidence(documentId, versionId, artifactId, anchorId, chunkId);
    }

    private static async Task<SeededEvidence> AddEvidenceOnlyAsync(
        LoregroveDbContext context,
        string fileName,
        string text)
    {
        var now = DateTimeOffset.UtcNow;
        var documentId = SourceDocumentId.New();
        var versionId = SourceDocumentVersionId.New();
        var artifactId = ParsedArtifactId.New();
        var anchorId = SourceAnchorId.New();
        var hash = new string('b', 64);
        context.SourceDocuments.Add(new SourceDocument(documentId, fileName, SourceKind.File, now, versionId));
        context.SourceDocumentVersions.Add(new SourceDocumentVersion(
            versionId, documentId, hash, fileName, "text/plain", text.Length, now, $"bb/{hash}", null,
            SourceProcessingState.Parsed));
        context.ProcessingJobs.Add(new ProcessingJob(
            ProcessingJobId.New(), versionId, ProcessingJobState.Pending, now, 1, now, stage: ProcessingStage.Chunking));
        context.ParsedArtifacts.Add(new ParsedArtifact(
            artifactId, versionId, hash, "test", "1", hash, hash, 1, hash, $"parsed/{hash}", now, 1, true));
        context.SourceAnchors.Add(new SourceAnchor(
            anchorId, artifactId, versionId, 0, ParsedBlockKind.Paragraph, SourceLocatorKind.Text, 1, "{}", text, hash));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return new SeededEvidence(documentId, versionId, artifactId, anchorId, default);
    }

    private sealed record SeededEvidence(
        SourceDocumentId DocumentId,
        SourceDocumentVersionId VersionId,
        ParsedArtifactId ArtifactId,
        SourceAnchorId AnchorId,
        ChunkId ChunkId);

    private sealed class TemporaryDatabase : IDisposable
    {
        private TemporaryDatabase(string path) => Path = path;
        public string Path { get; }
        public static TemporaryDatabase Create()
        {
            var directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "loregrove-search-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return new TemporaryDatabase(System.IO.Path.Combine(directory, "library.db"));
        }
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (directory is not null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class MemoryArtifactStore : IArtifactStore
    {
        private readonly Dictionary<string, byte[]> _artifacts = new(StringComparer.Ordinal);

        public string Add(byte[] bytes)
        {
            var hash = ParsedArtifactSerializer.HashBytes(bytes);
            var key = $"parsed/{hash}";
            _artifacts[key] = bytes.ToArray();
            return key;
        }

        public Task<StoredArtifact> StoreAsync(Stream content, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Stream> OpenReadAsync(string artifactObjectKey, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream(_artifacts[artifactObjectKey], writable: false));

        public Task<bool> ExistsAsync(string artifactObjectKey, CancellationToken cancellationToken) =>
            Task.FromResult(_artifacts.ContainsKey(artifactObjectKey));
    }

    private sealed class FailOnceChunker : IChunker
    {
        private readonly EvidenceAwareChunker _inner = new();
        private int _attempts;
        public ChunkerDescriptor Descriptor => _inner.Descriptor;

        public IReadOnlyList<ChunkCandidate> Chunk(
            ChunkingDocument document,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _attempts) == 1)
            {
                throw new InvalidOperationException("Injected deterministic chunker failure.");
            }

            return _inner.Chunk(document, cancellationToken);
        }
    }

    private enum InvalidChunkerMode
    {
        InconsistentSpanLength = 0,
        WrongText = 1,
    }

    private sealed class InvalidOutputChunker(InvalidChunkerMode mode) : IChunker
    {
        private readonly EvidenceAwareChunker _inner = new();

        public ChunkerDescriptor Descriptor => _inner.Descriptor;

        public IReadOnlyList<ChunkCandidate> Chunk(
            ChunkingDocument document,
            CancellationToken cancellationToken)
        {
            var candidates = _inner.Chunk(document, cancellationToken).ToArray();
            var first = candidates[0];
            var evidence = first.EvidenceSpans.ToArray();
            var text = first.Text;
            if (mode == InvalidChunkerMode.InconsistentSpanLength)
            {
                evidence[0] = evidence[0] with
                {
                    AnchorEnd = evidence[0].AnchorEnd - 1,
                    ChunkEnd = evidence[0].ChunkEnd - 1,
                };
            }
            else
            {
                text = $"X{text[1..]}";
            }

            var contentHash = ParsedArtifactSerializer.HashText(string.IsNullOrEmpty(first.ContextText)
                ? text
                : $"{first.ContextText}\n\n{text}");
            var evidenceIdentity = string.Join(
                '\n',
                evidence.Select(span => string.Join(
                    ':',
                    span.AnchorOrdinal,
                    span.AnchorTextHash,
                    span.LocatorFingerprint,
                    span.AnchorStart,
                    span.AnchorEnd,
                    span.ChunkStart,
                    span.ChunkEnd)));
            var chunkKey = ParsedArtifactSerializer.HashText(string.Join(
                '\n',
                document.SourceContentHash,
                document.ParsedArtifactContentHash,
                Descriptor.Fingerprint,
                first.Ordinal,
                contentHash,
                evidenceIdentity));
            candidates[0] = first with
            {
                Text = text,
                ContentHash = contentHash,
                ChunkKey = chunkKey,
                EvidenceSpans = evidence,
            };
            return candidates;
        }
    }

    private sealed class CancelingChunkHook(CancellationTokenSource cancellation) : IChunkTransactionHook
    {
        public Task OnStageAsync(ChunkTransactionStage stage, CancellationToken cancellationToken)
        {
            if (stage == ChunkTransactionStage.BeforeCommit)
            {
                cancellation.Cancel();
            }

            return Task.CompletedTask;
        }
    }
}
