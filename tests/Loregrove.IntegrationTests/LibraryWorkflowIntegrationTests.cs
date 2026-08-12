using System.Collections.Concurrent;
using System.Diagnostics;
using Loregrove.Application.Client;
using Loregrove.Application.Library;
using Loregrove.Application.Platform;
using Loregrove.Application.Sources;
using Loregrove.Application.Storage;
using Loregrove.Domain.Sources;
using Loregrove.Infrastructure.LocalFiles;
using Loregrove.Infrastructure.Sqlite;
using Loregrove.Infrastructure.Sqlite.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Loregrove.IntegrationTests;

public sealed class LibraryWorkflowIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task QuerySupportsOrderingPaginationFilteringDetailsAndNoTracking()
    {
        using var directory = TemporaryDirectory.Create();
        await using var services = CreateServices(directory.Path);
        await InitializeAsync(services);
        var importedAt = new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero);
        var firstId = new SourceDocumentId(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var secondId = new SourceDocumentId(Guid.Parse("00000000-0000-0000-0000-000000000002"));
        var newestId = new SourceDocumentId(Guid.Parse("00000000-0000-0000-0000-000000000003"));

        await using (var seedScope = services.CreateAsyncScope())
        {
            var context = seedScope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
            AddSource(context, firstId, "Budget notes", "finance-archive.txt", importedAt);
            AddSource(context, secondId, "Project plan", "atlas.md", importedAt);
            AddSource(context, newestId, "Meeting photo", "whiteboard.png", importedAt.AddMinutes(1));
            await context.SaveChangesAsync();
        }

        await using var scope = services.CreateAsyncScope();
        var query = scope.ServiceProvider.GetRequiredService<LibraryQueryService>();
        var contextAfterQuery = scope.ServiceProvider.GetRequiredService<LoregroveDbContext>();

        var firstPage = await query.GetSourcesAsync(new LibraryQuery(1, 25), CancellationToken.None);
        var secondRead = await query.GetSourcesAsync(new LibraryQuery(1, 25), CancellationToken.None);
        var displayNameMatch = await query.GetSourcesAsync(
            new LibraryQuery(1, 25, "budget"),
            CancellationToken.None);
        var originalNameMatch = await query.GetSourcesAsync(
            new LibraryQuery(1, 25, "atlas.md"),
            CancellationToken.None);
        var escapedWildcard = await query.GetSourcesAsync(
            new LibraryQuery(1, 25, "%"),
            CancellationToken.None);
        var details = await query.GetSourceAsync(secondId, CancellationToken.None);
        var missing = await query.GetSourceAsync(SourceDocumentId.New(), CancellationToken.None);

        Assert.Equal(3, firstPage.TotalCount);
        Assert.Equal(newestId, firstPage.Items[0].Id);
        Assert.Equal(firstPage.Items.Select(item => item.Id), secondRead.Items.Select(item => item.Id));
        Assert.Equal([newestId, secondId, firstId], firstPage.Items.Select(item => item.Id));
        Assert.Equal(firstId, Assert.Single(displayNameMatch.Items).Id);
        Assert.Equal(secondId, Assert.Single(originalNameMatch.Items).Id);
        Assert.Empty(escapedWildcard.Items);
        Assert.Equal(secondId, details?.Id);
        Assert.Equal("atlas.md", details?.OriginalFileName);
        Assert.Null(missing);
        Assert.Empty(contextAfterQuery.ChangeTracker.Entries());
    }

    [Fact]
    public async Task QueryPagesTenThousandRowsWithoutMaterializingTheLibrary()
    {
        using var directory = TemporaryDirectory.Create();
        await using var services = CreateServices(directory.Path);
        await InitializeAsync(services);

        await using (var seedScope = services.CreateAsyncScope())
        {
            var context = seedScope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
            var importedAt = new DateTimeOffset(2026, 8, 12, 9, 0, 0, TimeSpan.Zero);
            for (var index = 0; index < 10_000; index++)
            {
                AddSource(
                    context,
                    SourceDocumentId.New(),
                    $"Synthetic source {index:D5}",
                    $"synthetic-{index:D5}.txt",
                    importedAt.AddSeconds(index));
            }

            await context.SaveChangesAsync();
        }

        await using var queryScope = services.CreateAsyncScope();
        var query = queryScope.ServiceProvider.GetRequiredService<LibraryQueryService>();
        var firstPageStartedAt = Stopwatch.GetTimestamp();
        var page = await query.GetSourcesAsync(new LibraryQuery(1, 100), CancellationToken.None);
        var firstPageElapsed = Stopwatch.GetElapsedTime(firstPageStartedAt);
        var filterStartedAt = Stopwatch.GetTimestamp();
        var filtered = await query.GetSourcesAsync(
            new LibraryQuery(1, 25, "09999"),
            CancellationToken.None);
        var filterElapsed = Stopwatch.GetElapsedTime(filterStartedAt);

        output.WriteLine($"10k first page query: {firstPageElapsed.TotalMilliseconds:F1} ms");
        output.WriteLine($"10k filtered query: {filterElapsed.TotalMilliseconds:F1} ms");

        Assert.Equal(10_000, page.TotalCount);
        Assert.Equal(100, page.Items.Count);
        Assert.Equal(1, page.Page);
        Assert.Equal("Synthetic source 09999", Assert.Single(filtered.Items).DisplayName);
    }

    [Fact]
    public async Task ImportCoordinatorReportsMixedOutcomesBoundsConcurrencyAndCreatesOneScopePerFile()
    {
        using var directory = TemporaryDirectory.Create();
        var registry = new ScopeProbeRegistry();
        await using var services = CreateServices(directory.Path, registry);
        await InitializeAsync(services);
        var coordinator = services.GetRequiredService<LibraryImportCoordinator>();
        var streamTracker = new StreamConcurrencyTracker();
        var files = Enumerable.Range(0, 8)
            .Select(index => Picked(
                $"source-{index}.txt",
                [(byte)index, 20, 30],
                () => new DelayedTrackingStream([(byte)index, 20, 30], streamTracker)))
            .Append(new PickedFile(
                "denied.txt",
                "denied.txt",
                "text/plain",
                null,
                _ => throw new UnauthorizedAccessException()))
            .ToArray();
        var updates = new ConcurrentBag<ImportProgress>();

        var result = await coordinator.ImportFilesAsync(
            files,
            new InlineProgress<ImportProgress>(updates.Add),
            CancellationToken.None);
        var duplicate = await coordinator.ImportFilesAsync(
            [Picked("renamed.txt", [0, 20, 30])],
            progress: null,
            CancellationToken.None);

        Assert.Equal(8, result.ImportedCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal("Access was denied.", result.Items.Single(item => item.State == ImportItemState.Failed).Message);
        Assert.Equal(ImportItemState.AlreadyExists, Assert.Single(duplicate.Items).State);
        Assert.InRange(streamTracker.Maximum, 2, LibraryImportCoordinator.MaximumConcurrentImports);
        Assert.Equal(8, registry.InstanceIds.Count);
        Assert.Contains(updates, update => update.State == ImportItemState.Queued);
        Assert.Contains(updates, update => update.State == ImportItemState.Importing);
        Assert.Contains(updates, update => update.State == ImportItemState.Imported);
        Assert.Contains(updates, update => update.State == ImportItemState.Failed);
    }

    [Fact]
    public async Task CancellationStopsQueuedItemsAndDisposesOpenedStreams()
    {
        using var directory = TemporaryDirectory.Create();
        await using var services = CreateServices(directory.Path);
        await InitializeAsync(services);
        var coordinator = services.GetRequiredService<LibraryImportCoordinator>();
        using var cancellation = new CancellationTokenSource();
        var opened = new ConcurrentBag<DisposalTrackingStream>();
        var files = Enumerable.Range(0, 12)
            .Select(index => new PickedFile(
                $"cancel-{index}.txt",
                $"cancel-{index}.txt",
                "text/plain",
                null,
                _ =>
                {
                    var stream = new DisposalTrackingStream([(byte)index]);
                    opened.Add(stream);
                    cancellation.Cancel();
                    return Task.FromResult<Stream>(stream);
                }))
            .ToArray();

        var result = await coordinator.ImportFilesAsync(files, progress: null, cancellation.Token);

        Assert.All(result.Items, item => Assert.Equal(ImportItemState.Cancelled, item.State));
        Assert.InRange(opened.Count, 1, LibraryImportCoordinator.MaximumConcurrentImports);
        Assert.All(opened, stream => Assert.True(stream.IsDisposed));
    }

    [Fact]
    public async Task ImportDuplicateQueryAndRestartKeepTwoDurableRows()
    {
        using var directory = TemporaryDirectory.Create();
        IReadOnlyList<SourceDocumentId> firstIds;

        await using (var firstServices = CreateServices(directory.Path))
        {
            await InitializeAsync(firstServices);
            var client = firstServices.GetRequiredService<ILibraryClient>();
            var result = await client.ImportFilesAsync(
                [
                    Picked("alpha.txt", [1, 2, 3]),
                    Picked("beta.txt", [4, 5, 6]),
                    Picked("alpha-copy.txt", [1, 2, 3]),
                ],
                progress: null,
                CancellationToken.None);
            var page = await client.GetSourcesAsync(new LibraryQuery(), CancellationToken.None);

            Assert.Equal(2, result.ImportedCount);
            Assert.Equal(1, result.AlreadyExistsCount);
            Assert.Equal(2, page.TotalCount);
            firstIds = page.Items.Select(item => item.Id).OrderBy(id => id.Value).ToArray();
        }

        SqliteConnection.ClearAllPools();
        await using var restartedServices = CreateServices(directory.Path);
        await InitializeAsync(restartedServices);
        var restartedClient = restartedServices.GetRequiredService<ILibraryClient>();
        var restartedPage = await restartedClient.GetSourcesAsync(new LibraryQuery(), CancellationToken.None);

        Assert.Equal(2, restartedPage.TotalCount);
        Assert.Equal(firstIds, restartedPage.Items.Select(item => item.Id).OrderBy(id => id.Value));
    }

    private static ServiceProvider CreateServices(string root, ScopeProbeRegistry? registry = null)
    {
        var paths = new LocalLibraryPaths(root);
        var collection = new ServiceCollection();
        collection.AddSingleton<ILibraryPaths>(paths);
        collection.AddSingleton<ILibraryDirectoryInitializer, LocalLibraryInitializer>();
        collection.AddSingleton<IObjectStore, LocalObjectStore>();
        collection.AddSingleton<IDesktopPlatform, EmptyDesktopPlatform>();
        collection.AddLoregroveSqlite(paths.Database);
        collection.AddSingleton<LibraryImportCoordinator>();
        collection.AddSingleton<ILibraryClient, LibraryClient>();
        if (registry is not null)
        {
            collection.AddSingleton(registry);
            collection.AddScoped<IImportTransactionHook, ConcurrentScopeProbe>();
        }

        return collection.BuildServiceProvider(validateScopes: true);
    }

    private static Task InitializeAsync(IServiceProvider services) =>
        services.GetRequiredService<ILibraryInitializer>().InitializeAsync(CancellationToken.None);

    private static PickedFile Picked(
        string fileName,
        byte[] bytes,
        Func<Stream>? streamFactory = null) =>
        new(
            fileName,
            fileName,
            "text/plain",
            bytes.LongLength,
            _ => Task.FromResult(streamFactory?.Invoke() ?? new MemoryStream(bytes, writable: false)));

    private static void AddSource(
        LoregroveDbContext context,
        SourceDocumentId documentId,
        string displayName,
        string originalFileName,
        DateTimeOffset importedAt)
    {
        var versionId = SourceDocumentVersionId.New();
        var contentHash = Convert.ToHexString(Guid.NewGuid().ToByteArray())
            .ToLowerInvariant()
            .PadRight(64, '0');
        context.SourceDocuments.Add(new SourceDocument(
            documentId,
            displayName,
            SourceKind.File,
            importedAt,
            versionId));
        context.SourceDocumentVersions.Add(new SourceDocumentVersion(
            versionId,
            documentId,
            contentHash,
            originalFileName,
            "text/plain",
            1024,
            importedAt,
            $"{contentHash[..2]}/{contentHash}",
            previousVersionId: null,
            SourceProcessingState.PendingProcessing));
    }

    private sealed class EmptyDesktopPlatform : IDesktopPlatform
    {
        public Task<IReadOnlyList<PickedFile>> PickFilesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PickedFile>>([]);

        public Task<PickedFolder?> PickFolderAsync(CancellationToken cancellationToken) =>
            Task.FromResult<PickedFolder?>(null);

        public Task OpenExternalFileAsync(string path, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RevealFileAsync(string path, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SetClipboardTextAsync(string text, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ScopeProbeRegistry
    {
        public ConcurrentDictionary<Guid, byte> InstanceIds { get; } = new();
    }

    private sealed class ConcurrentScopeProbe(ScopeProbeRegistry registry) : IImportTransactionHook
    {
        private readonly Guid _instanceId = Guid.NewGuid();
        private int _active;

        public async Task OnStageAsync(
            ImportTransactionStage stage,
            CancellationToken cancellationToken)
        {
            registry.InstanceIds.TryAdd(_instanceId, 0);
            if (stage != ImportTransactionStage.AfterDocumentAdded)
            {
                return;
            }

            if (Interlocked.Exchange(ref _active, 1) != 0)
            {
                throw new InvalidOperationException("A scoped import dependency was reused concurrently.");
            }

            try
            {
                await Task.Delay(50, cancellationToken);
            }
            finally
            {
                Volatile.Write(ref _active, 0);
            }
        }
    }

    private sealed class StreamConcurrencyTracker
    {
        private int _active;
        private int _maximum;

        public int Maximum => Volatile.Read(ref _maximum);

        public void Enter()
        {
            var active = Interlocked.Increment(ref _active);
            var maximum = Volatile.Read(ref _maximum);
            while (active > maximum)
            {
                var observed = Interlocked.CompareExchange(ref _maximum, active, maximum);
                if (observed == maximum)
                {
                    break;
                }

                maximum = observed;
            }
        }

        public void Exit() => Interlocked.Decrement(ref _active);
    }

    private sealed class DelayedTrackingStream(byte[] bytes, StreamConcurrencyTracker tracker)
        : MemoryStream(bytes, writable: false)
    {
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            tracker.Enter();
            try
            {
                await Task.Delay(60, cancellationToken);
                return await base.ReadAsync(buffer, cancellationToken);
            }
            finally
            {
                tracker.Exit();
            }
        }
    }

    private sealed class DisposalTrackingStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            IsDisposed = true;
            await base.DisposeAsync();
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "loregrove-library-workflow",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
