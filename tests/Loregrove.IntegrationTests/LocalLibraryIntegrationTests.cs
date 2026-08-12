using Loregrove.Application.Persistence;
using Loregrove.Application.Sources;
using Loregrove.Application.Storage;
using Loregrove.Domain.Sources;
using Loregrove.Infrastructure.LocalFiles;
using Loregrove.Infrastructure.Sqlite;
using Loregrove.Infrastructure.Sqlite.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Loregrove.IntegrationTests;

public sealed class LocalLibraryIntegrationTests
{
    [Fact]
    public async Task EmptyLibraryInitializesMigrationAndReinitializesWithoutDataLoss()
    {
        using var directory = TemporaryDirectory.Create();
        await using var services = CreateServices(directory.Path);
        await InitializeAsync(services);
        var paths = services.GetRequiredService<ILibraryPaths>();

        var first = await ImportAsync(services, "Evidence", "evidence.txt", "text/plain", [1, 2, 3]);
        await InitializeAsync(services);

        Assert.True(File.Exists(paths.Database));
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
        var migrations = await context.Database.GetAppliedMigrationsAsync();
        Assert.Contains("20260811183241_InitialSqlitePersistence", migrations);
        Assert.Contains("20260812170000_DoclingComplexDocumentEvidence", migrations);
        Assert.Equal(1, await context.SourceDocuments.CountAsync());
        Assert.Equal(ImportDisposition.Created, first.Disposition);
    }

    [Fact]
    public async Task CompleteCaptureSurvivesServiceAndDatabaseRestart()
    {
        using var directory = TemporaryDirectory.Create();
        ImportSourceResult imported;
        string objectKey;

        await using (var firstServices = CreateServices(directory.Path))
        {
            await InitializeAsync(firstServices);
            imported = await ImportAsync(firstServices, "Durable", "durable.bin", null, [9, 8, 7]);
            await using var scope = firstServices.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
            objectKey = (await context.SourceDocumentVersions.AsNoTracking().SingleAsync()).ObjectKey;
        }

        SqliteConnection.ClearAllPools();
        await using var restartedServices = CreateServices(directory.Path);
        await InitializeAsync(restartedServices);
        await using var restartedScope = restartedServices.CreateAsyncScope();
        var restartedContext = restartedScope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
        var objectStore = restartedServices.GetRequiredService<IObjectStore>();

        Assert.Equal(imported.DocumentId, (await restartedContext.SourceDocuments.SingleAsync()).Id);
        Assert.Equal(imported.VersionId, (await restartedContext.SourceDocumentVersions.SingleAsync()).Id);
        Assert.Single(await restartedContext.ProcessingJobs.ToListAsync());
        await using var source = await objectStore.OpenReadAsync(objectKey, CancellationToken.None);
        Assert.Equal(new byte[] { 9, 8, 7 }, await ReadAllAsync(source));
    }

    [Fact]
    public async Task SixteenConcurrentDuplicateImportsCreateExactlyOneCapture()
    {
        using var directory = TemporaryDirectory.Create();
        await using var services = CreateServices(directory.Path);
        await InitializeAsync(services);
        var bytes = new byte[512 * 1024];
        Random.Shared.NextBytes(bytes);

        var imports = Enumerable.Range(0, 16)
            .Select(index => ImportAsync(
                services,
                $"Source {index}",
                $"renamed-{index}.bin",
                "application/octet-stream",
                bytes));
        var results = await Task.WhenAll(imports);

        Assert.Single(results, result => result.Disposition == ImportDisposition.Created);
        Assert.Equal(15, results.Count(result => result.Disposition == ImportDisposition.AlreadyExists));
        Assert.Single(results.Select(result => result.DocumentId).Distinct());
        Assert.Single(results.Select(result => result.VersionId).Distinct());
        await AssertCountsAsync(services, documents: 1, versions: 1, jobs: 1);
        Assert.Single(FinalObjectFiles(services.GetRequiredService<ILibraryPaths>()));
    }

    [Fact]
    public async Task SameFilenameWithDifferentBytesCreatesIndependentCaptures()
    {
        using var directory = TemporaryDirectory.Create();
        await using var services = CreateServices(directory.Path);
        await InitializeAsync(services);

        var first = await ImportAsync(services, "Report", "report.pdf", "application/pdf", [1]);
        var second = await ImportAsync(services, "Report", "report.pdf", "application/pdf", [2]);

        Assert.Equal(ImportDisposition.Created, first.Disposition);
        Assert.Equal(ImportDisposition.Created, second.Disposition);
        Assert.NotEqual(first.DocumentId, second.DocumentId);
        await AssertCountsAsync(services, documents: 2, versions: 2, jobs: 2);
        Assert.Equal(2, FinalObjectFiles(services.GetRequiredService<ILibraryPaths>()).Count());
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
        Assert.All(await context.SourceDocumentVersions.ToListAsync(), version => Assert.Null(version.PreviousVersionId));
    }

    [Theory]
    [InlineData(ImportTransactionStage.AfterDocumentAdded)]
    [InlineData(ImportTransactionStage.AfterVersionAdded)]
    [InlineData(ImportTransactionStage.BeforeProcessingJobAdded)]
    [InlineData(ImportTransactionStage.BeforeCommit)]
    public async Task FailureAtEveryTransactionStageRollsBackAllRelationalRows(ImportTransactionStage stage)
    {
        using var directory = TemporaryDirectory.Create();
        await using var services = CreateServices(directory.Path, new ThrowingTransactionHook(stage));
        await InitializeAsync(services);

        await Assert.ThrowsAsync<InjectedTransactionException>(() =>
            ImportAsync(services, "Orphan", "orphan.bin", null, [4, 5, 6]));

        await AssertCountsAsync(services, documents: 0, versions: 0, jobs: 0);
        Assert.Single(FinalObjectFiles(services.GetRequiredService<ILibraryPaths>()));
    }

    [Fact]
    public async Task ProcessingJobInsertFailureRollsBackExecutedInsertsAndClearsTrackedEntities()
    {
        using var directory = TemporaryDirectory.Create();
        await using var services = CreateServices(directory.Path);
        await InitializeAsync(services);
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ImportSourceService>();

        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER FailProcessingJobInsert
            BEFORE INSERT ON ProcessingJobs
            BEGIN
                SELECT RAISE(ABORT, 'injected processing job insert failure');
            END;
            """);

        await using (var failedContent = new MemoryStream([10, 20, 30], writable: false))
        {
            var failure = await Assert.ThrowsAsync<DbUpdateException>(() => service.ImportAsync(
                new ImportSourceCommand("Failed", "failed.bin", null, failedContent),
                CancellationToken.None));
            var sqliteFailure = Assert.IsType<SqliteException>(failure.InnerException);
            Assert.Equal(19, sqliteFailure.SqliteErrorCode);
            Assert.Contains("injected processing job insert failure", sqliteFailure.Message, StringComparison.Ordinal);
        }

        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Equal(0, await context.SourceDocuments.CountAsync());
        Assert.Equal(0, await context.SourceDocumentVersions.CountAsync());
        Assert.Equal(0, await context.ProcessingJobs.CountAsync());

        await context.Database.ExecuteSqlRawAsync("DROP TRIGGER FailProcessingJobInsert;");
        await using var successfulContent = new MemoryStream([40, 50, 60], writable: false);
        var successful = await service.ImportAsync(
            new ImportSourceCommand("Successful", "successful.bin", null, successfulContent),
            CancellationToken.None);

        Assert.Equal(ImportDisposition.Created, successful.Disposition);
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Equal(1, await context.SourceDocuments.CountAsync());
        Assert.Equal(1, await context.SourceDocumentVersions.CountAsync());
        Assert.Equal(1, await context.ProcessingJobs.CountAsync());
        Assert.Equal(2, FinalObjectFiles(services.GetRequiredService<ILibraryPaths>()).Count());
    }

    [Fact]
    public async Task CancellationBeforeCommitRollsBackRelationalRowsAndLeavesSafeObject()
    {
        using var directory = TemporaryDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        await using var services = CreateServices(
            directory.Path,
            new CancelingTransactionHook(ImportTransactionStage.BeforeCommit, cancellation));
        await InitializeAsync(services);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImportAsync(
            services,
            "Cancelled",
            "cancelled.bin",
            null,
            [7, 8, 9],
            cancellation.Token));

        await AssertCountsAsync(services, documents: 0, versions: 0, jobs: 0);
        Assert.Single(FinalObjectFiles(services.GetRequiredService<ILibraryPaths>()));
    }

    [Fact]
    public async Task InitializationRecoversInterruptedJobsWithoutIncrementingAttempts()
    {
        using var directory = TemporaryDirectory.Create();
        await using (var firstServices = CreateServices(directory.Path))
        {
            await InitializeAsync(firstServices);
            await ImportAsync(firstServices, "Recover", "recover.bin", null, [1, 3, 5]);
            await using var scope = firstServices.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
            await context.ProcessingJobs.ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.State, ProcessingJobState.Processing)
                .SetProperty(job => job.AttemptCount, 3));
        }

        SqliteConnection.ClearAllPools();
        await using var restartedServices = CreateServices(directory.Path);
        await InitializeAsync(restartedServices);
        await using var restartedScope = restartedServices.CreateAsyncScope();
        var restartedContext = restartedScope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
        var recovered = await restartedContext.ProcessingJobs.AsNoTracking().SingleAsync();

        Assert.Equal(ProcessingJobState.Pending, recovered.State);
        Assert.Equal(3, recovered.AttemptCount);
        Assert.NotNull(recovered.UpdatedAt);
    }

    [Fact]
    public async Task ForeignKeysWalBusyTimeoutAndQuickCheckAreEnabled()
    {
        using var directory = TemporaryDirectory.Create();
        await using var services = CreateServices(directory.Path);
        await InitializeAsync(services);
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LoregroveDbContext>();

        Assert.Equal("wal", await ScalarPragmaAsync(context, "journal_mode"));
        Assert.Equal("1", await ScalarPragmaAsync(context, "foreign_keys"));
        Assert.Equal("5000", await ScalarPragmaAsync(context, "busy_timeout"));

        context.ProcessingJobs.Add(new ProcessingJob(
            ProcessingJobId.New(),
            SourceDocumentVersionId.New(),
            ProcessingJobState.Pending,
            DateTimeOffset.UtcNow,
            attemptCount: 0));
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        var diagnostics = services.GetRequiredService<IDatabaseIntegrityDiagnostics>();
        var integrity = await diagnostics.QuickCheckAsync(CancellationToken.None);
        Assert.True(integrity.IsHealthy, integrity.Message);
    }

    private static ServiceProvider CreateServices(string root, IImportTransactionHook? hook = null)
    {
        var paths = new LocalLibraryPaths(root);
        var collection = new ServiceCollection();
        collection.AddSingleton<ILibraryPaths>(paths);
        collection.AddSingleton<ILibraryDirectoryInitializer, LocalLibraryInitializer>();
        collection.AddSingleton<IObjectStore, LocalObjectStore>();
        collection.AddLoregroveSqlite(paths.Database);
        if (hook is not null)
        {
            collection.AddScoped(_ => hook);
        }

        return collection.BuildServiceProvider(validateScopes: true);
    }

    private static async Task InitializeAsync(IServiceProvider services) =>
        await services.GetRequiredService<ILibraryInitializer>().InitializeAsync(CancellationToken.None);

    private static async Task<ImportSourceResult> ImportAsync(
        IServiceProvider services,
        string displayName,
        string originalFileName,
        string? mediaType,
        byte[] bytes,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ImportSourceService>();
        await using var content = new MemoryStream(bytes, writable: false);
        return await service.ImportAsync(
            new ImportSourceCommand(displayName, originalFileName, mediaType, content),
            cancellationToken);
    }

    private static async Task AssertCountsAsync(
        IServiceProvider services,
        int documents,
        int versions,
        int jobs)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
        Assert.Equal(documents, await context.SourceDocuments.CountAsync());
        Assert.Equal(versions, await context.SourceDocumentVersions.CountAsync());
        Assert.Equal(jobs, await context.ProcessingJobs.CountAsync());
    }

    private static async Task<string> ScalarPragmaAsync(LoregroveDbContext context, string pragma)
    {
        await context.Database.OpenConnectionAsync();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"PRAGMA {pragma};";
        return Convert.ToString(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static IEnumerable<string> FinalObjectFiles(ILibraryPaths paths) =>
        Directory.EnumerateFiles(paths.Objects, "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}.tmp{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal));

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var destination = new MemoryStream();
        await stream.CopyToAsync(destination);
        return destination.ToArray();
    }

    private sealed class ThrowingTransactionHook(ImportTransactionStage stage) : IImportTransactionHook
    {
        public Task OnStageAsync(ImportTransactionStage currentStage, CancellationToken cancellationToken) =>
            currentStage == stage
                ? throw new InjectedTransactionException(stage)
                : Task.CompletedTask;
    }

    private sealed class CancelingTransactionHook(
        ImportTransactionStage stage,
        CancellationTokenSource cancellation) : IImportTransactionHook
    {
        public Task OnStageAsync(ImportTransactionStage currentStage, CancellationToken cancellationToken)
        {
            if (currentStage == stage)
            {
                cancellation.Cancel();
            }

            return Task.CompletedTask;
        }
    }

    private sealed class InjectedTransactionException(ImportTransactionStage stage)
        : Exception($"Injected failure at {stage}.");

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "loregrove-sqlite-integration",
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
