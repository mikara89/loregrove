using System.Text;
using Loregrove.Application.Docling;
using Loregrove.Application.Parsing;
using Loregrove.Application.Persistence;
using Loregrove.Application.Security;
using Loregrove.Application.Sources;
using Loregrove.Application.Storage;
using Loregrove.Domain.Sources;
using Loregrove.Infrastructure.Docling;
using Loregrove.Infrastructure.LocalFiles;
using Loregrove.Infrastructure.Sqlite;
using Loregrove.Infrastructure.Sqlite.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Loregrove.IntegrationTests;

public sealed class DoclingPipelineIntegrationTests
{
    [Fact]
    public async Task RemoteConversionReadsCapturedObjectAndCommitsPartialEvidenceTransactionally()
    {
        using var library = new TestLibrary();
        var conversion = new FakeConversionClient(DoclingConversionStatus.PartialSuccess);
        var process = new CountingProcessManager();
        await using var services = BuildServices(library.Path, conversion, process);
        await InitializeAsync(services);
        var bytes = Encoding.UTF8.GetBytes("captured immutable PDF bytes");
        var versionId = await ImportAsync(services, bytes);

        var result = await ParseAsync(services, versionId);

        Assert.Equal(ParseSourceDisposition.Parsed, result.Disposition);
        Assert.Equal(bytes, conversion.UploadedBytes);
        Assert.Equal(0, process.AcquireCount);
        Assert.Equal(0, process.StopCount);
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
        var artifact = await context.ParsedArtifacts.AsNoTracking().SingleAsync();
        Assert.Equal(ParsedArtifactCompleteness.Partial, artifact.Completeness);
        Assert.Equal(1, artifact.WarningCount);
        Assert.Equal("docling-partial-success", artifact.SafeDiagnosticCode);
        Assert.Equal(1, await context.SourceAnchors.CountAsync());
        Assert.Equal(SourceProcessingState.Parsed,
            (await context.SourceDocumentVersions.AsNoTracking().SingleAsync()).ProcessingState);
        var job = await context.ProcessingJobs.AsNoTracking().SingleAsync();
        Assert.Equal(ProcessingJobState.Pending, job.State);
        Assert.Equal(ProcessingStage.Chunking, job.Stage);
        Assert.Equal(1, job.AttemptCount);

        await using var artifactStream = await services.GetRequiredService<IArtifactStore>()
            .OpenReadAsync(artifact.ArtifactObjectKey, CancellationToken.None);
        using var reader = new StreamReader(artifactStream, Encoding.UTF8);
        var json = await reader.ReadToEndAsync();
        Assert.Contains("\"doclingDocument\"", json, StringComparison.Ordinal);
        Assert.Contains("\"markdown\"", json, StringComparison.Ordinal);
        Assert.Contains("\"completeness\":\"Partial\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("task-id", json, StringComparison.Ordinal);
        Assert.DoesNotContain("processing_time", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConversionFailureMarksDocumentFailedWithoutEvidence()
    {
        using var library = new TestLibrary();
        var conversion = new FakeConversionClient(DoclingConversionStatus.DocumentFailure);
        await using var services = BuildServices(library.Path, conversion, new CountingProcessManager());
        await InitializeAsync(services);
        var versionId = await ImportAsync(services, Encoding.UTF8.GetBytes("bad source"));

        var result = await ParseAsync(services, versionId);

        Assert.Equal(ParseSourceDisposition.Failed, result.Disposition);
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
        Assert.Equal(0, await context.ParsedArtifacts.CountAsync());
        Assert.Equal(0, await context.SourceAnchors.CountAsync());
        Assert.Equal(SourceProcessingState.ParseFailed,
            (await context.SourceDocumentVersions.AsNoTracking().SingleAsync()).ProcessingState);
        var job = await context.ProcessingJobs.AsNoTracking().SingleAsync();
        Assert.Equal(ProcessingJobState.Failed, job.State);
        Assert.Equal(1, job.AttemptCount);
        Assert.Equal("The source could not be parsed.", job.LastError);
    }

    [Fact]
    public async Task TransportFailureConsumesAttemptButReturnsJobToRetryablePending()
    {
        using var library = new TestLibrary();
        await using var services = BuildServices(
            library.Path,
            new FakeConversionClient(ParserInfrastructureFailureCode.TransportFailure),
            new CountingProcessManager());
        await InitializeAsync(services);
        var versionId = await ImportAsync(services, Encoding.UTF8.GetBytes("retry source"));

        var result = await ParseAsync(services, versionId);

        Assert.Equal(ParseSourceDisposition.RetryableFailure, result.Disposition);
        Assert.Equal(ParserInfrastructureFailureCode.TransportFailure, result.InfrastructureFailureCode);
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
        Assert.Equal(SourceProcessingState.PendingProcessing,
            (await context.SourceDocumentVersions.AsNoTracking().SingleAsync()).ProcessingState);
        var job = await context.ProcessingJobs.AsNoTracking().SingleAsync();
        Assert.Equal(ProcessingJobState.Pending, job.State);
        Assert.Equal(ProcessingStage.Parsing, job.Stage);
        Assert.Equal(1, job.AttemptCount);
        Assert.Null(job.LastError);
    }

    [Fact]
    public async Task SchemaIncompatibilityReturnsSourceAndJobToRetryableParsingState()
    {
        const string incompatibleDocument = "{\"schema_name\":\"DoclingDocument\",\"texts\":[]}";
        using var library = new TestLibrary();
        await using var services = BuildServices(
            library.Path,
            new FakeConversionClient(incompatibleDocument),
            new CountingProcessManager());
        await InitializeAsync(services);
        var versionId = await ImportAsync(services, Encoding.UTF8.GetBytes("retry incompatible schema"));

        var result = await ParseAsync(services, versionId);

        Assert.Equal(ParseSourceDisposition.RetryableFailure, result.Disposition);
        Assert.Equal(ParserInfrastructureFailureCode.ApiIncompatible, result.InfrastructureFailureCode);
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
        Assert.Null(job.LastError);
    }

    [Fact]
    public async Task MultiPageProvenanceSurvivesAnchorPersistence()
    {
        const string multiPageDocument = """
            {
              "schema_name":"DoclingDocument","version":"1.0.0",
              "body":{"self_ref":"#/body","children":[{"$ref":"#/texts/0"}]},
              "furniture":{"self_ref":"#/furniture","children":[]},
              "groups":[],"tables":[],"pictures":[],"key_value_items":[],
              "pages":{"1":{"page_no":1,"size":{"width":612,"height":792}},"2":{"page_no":2,"size":{"width":620,"height":800}}},
              "texts":[{"self_ref":"#/texts/0","label":"paragraph","text":"Across pages","children":[],"prov":[
                {"page_no":1,"bbox":{"l":1,"t":10,"r":20,"b":2,"coord_origin":"BOTTOMLEFT"},"charspan":[0,6]},
                {"page_no":2,"bbox":{"l":2,"t":12,"r":22,"b":3,"coord_origin":"BOTTOMLEFT"},"charspan":[7,12]}
              ]}]
            }
            """;
        using var library = new TestLibrary();
        await using var services = BuildServices(
            library.Path,
            new FakeConversionClient(multiPageDocument),
            new CountingProcessManager());
        await InitializeAsync(services);
        var versionId = await ImportAsync(services, Encoding.UTF8.GetBytes("multi-page evidence"));

        var result = await ParseAsync(services, versionId);

        Assert.Equal(ParseSourceDisposition.Parsed, result.Disposition);
        var artifactId = Assert.IsType<ParsedArtifactId>(result.ArtifactId);
        await using var scope = services.CreateAsyncScope();
        var reader = scope.ServiceProvider.GetRequiredService<IParsedEvidenceReader>();
        var anchor = Assert.Single(await reader.GetAnchorsAsync(artifactId, CancellationToken.None));
        var locator = Assert.IsType<PagedRegionSourceLocator>(anchor.Locator);
        Assert.Equal(2, locator.SchemaVersion);
        Assert.Equal([1, 2], locator.Regions.Select(region => region.PageNumber));
        Assert.Equal(620, locator.Regions[1].PageWidth);
        Assert.Equal(800, locator.Regions[1].PageHeight);
        Assert.Equal(new SourceCharacterSpan(7, 12), locator.Regions[1].CharacterSpan);
    }

    [Fact]
    public async Task SameRemoteProfileIsIdempotentWithoutSecondUploadOrAttempt()
    {
        using var library = new TestLibrary();
        var conversion = new FakeConversionClient(DoclingConversionStatus.Success);
        await using var services = BuildServices(library.Path, conversion, new CountingProcessManager());
        await InitializeAsync(services);
        var versionId = await ImportAsync(services, Encoding.UTF8.GetBytes("same source"));

        var first = await ParseAsync(services, versionId);
        var second = await ParseAsync(services, versionId);

        Assert.Equal(ParseSourceDisposition.Parsed, first.Disposition);
        Assert.Equal(ParseSourceDisposition.AlreadyParsed, second.Disposition);
        Assert.Equal(1, conversion.SubmissionCount);
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
        Assert.Equal(1, (await context.ProcessingJobs.AsNoTracking().SingleAsync()).AttemptCount);
        Assert.Equal(1, await context.ParsedArtifacts.CountAsync());
    }

    [Fact]
    public async Task RemoteCredentialComesFromSecretStoreAndIsNeverPersisted()
    {
        using var library = new TestLibrary();
        const string secret = "private-api-key";
        var conversion = new FakeConversionClient(DoclingConversionStatus.Success);
        await using var services = BuildServices(
            library.Path,
            conversion,
            new CountingProcessManager(),
            new InMemorySecretStore("docling-api", secret));
        await InitializeAsync(services);
        var versionId = await ImportAsync(services, Encoding.UTF8.GetBytes("credential source"));

        var result = await ParseAsync(services, versionId);

        Assert.Equal(ParseSourceDisposition.Parsed, result.Disposition);
        Assert.Equal(secret, conversion.ApiKey);
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
        var artifact = await context.ParsedArtifacts.AsNoTracking().SingleAsync();
        await using var artifactStream = await services.GetRequiredService<IArtifactStore>()
            .OpenReadAsync(artifact.ArtifactObjectKey, CancellationToken.None);
        using var reader = new StreamReader(artifactStream, Encoding.UTF8);
        Assert.DoesNotContain(secret, await reader.ReadToEndAsync(), StringComparison.Ordinal);
    }

    private static async Task<SourceDocumentVersionId> ImportAsync(IServiceProvider services, byte[] bytes)
    {
        await using var scope = services.CreateAsyncScope();
        await using var stream = new MemoryStream(bytes, writable: false);
        var result = await scope.ServiceProvider.GetRequiredService<ImportSourceService>().ImportAsync(
            new ImportSourceCommand("PDF", @"C:\Users\private\evidence.pdf", "application/pdf", stream),
            CancellationToken.None);
        return result.VersionId;
    }

    private static async Task<ParseSourceResult> ParseAsync(IServiceProvider services, SourceDocumentVersionId versionId)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ParseSourceService>().ParseAsync(versionId, CancellationToken.None);
    }

    private static Task InitializeAsync(IServiceProvider services) =>
        services.GetRequiredService<ILibraryInitializer>().InitializeAsync(CancellationToken.None);

    private static ServiceProvider BuildServices(
        string root,
        IDoclingConversionClient conversion,
        CountingProcessManager process,
        ISecretStore? secretStore = null)
    {
        var paths = new LocalLibraryPaths(root);
        var services = new ServiceCollection();
        services.AddSingleton<ILibraryPaths>(paths);
        services.AddSingleton<ILibraryDirectoryInitializer, LocalLibraryInitializer>();
        services.AddSingleton<IObjectStore, LocalObjectStore>();
        services.AddSingleton<IArtifactStore, LocalArtifactStore>();
        services.AddLoregroveParsing();
        services.AddLoregroveDocling(configuration =>
        {
            configuration.Mode = DoclingMode.Remote;
            configuration.RemoteEndpoint = new Uri("http://127.0.0.1:5001/");
            configuration.AllowRemoteDocumentUpload = true;
            configuration.RemoteCredentialKey = secretStore is null ? null : "docling-api";
        });
        if (secretStore is not null)
        {
            services.AddSingleton(secretStore);
        }

        services.Replace(ServiceDescriptor.Singleton(conversion));
        services.Replace(ServiceDescriptor.Singleton<IDoclingProcessManager>(process));
        services.AddLoregroveSqlite(paths.Database);
        return services.BuildServiceProvider();
    }

    private sealed class FakeConversionClient : IDoclingConversionClient
    {
        private readonly DoclingConversionStatus? _status;
        private readonly ParserInfrastructureFailureCode? _failure;
        private readonly string? _structuredJson;

        internal FakeConversionClient(DoclingConversionStatus status) => _status = status;
        internal FakeConversionClient(ParserInfrastructureFailureCode failure) => _failure = failure;
        internal FakeConversionClient(string structuredJson)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(structuredJson);
            _status = DoclingConversionStatus.Success;
            _structuredJson = structuredJson;
        }
        internal byte[] UploadedBytes { get; private set; } = [];
        internal int SubmissionCount { get; private set; }
        internal string? ApiKey { get; private set; }

        public async Task<DoclingConversionResult> ConvertAsync(
            Uri endpoint,
            DoclingConversionRequest request,
            Func<bool>? isLeaseValid,
            CancellationToken cancellationToken)
        {
            SubmissionCount++;
            ApiKey = request.ApiKey;
            using var memory = new MemoryStream();
            await request.Source.CopyToAsync(memory, cancellationToken);
            UploadedBytes = memory.ToArray();
            if (_failure is { } failure)
            {
                throw new ParserInfrastructureException(failure, "injected transport failure");
            }

            if (_status == DoclingConversionStatus.DocumentFailure)
            {
                return new(DoclingConversionStatus.DocumentFailure, null, null, 1, "docling-conversion-failed");
            }

            return new(
                _status!.Value,
                "# Evidence\n",
                _structuredJson ?? """
                {
                  "version":"1.0.0","schema_name":"DoclingDocument",
                  "body":{"self_ref":"#/body","children":[{"$ref":"#/texts/0"}]},
                  "furniture":{"self_ref":"#/furniture","children":[]},
                  "groups":[],"tables":[],"pictures":[],"key_value_items":[],
                  "texts":[{"self_ref":"#/texts/0","label":"paragraph","text":"Evidence","children":[],"prov":[{"page_no":1,"bbox":{"l":1,"t":10,"r":20,"b":2,"coord_origin":"BOTTOMLEFT"},"charspan":[0,8]}]}]
                }
                """,
                _status == DoclingConversionStatus.PartialSuccess ? 1 : 0,
                _status == DoclingConversionStatus.PartialSuccess ? "docling-partial-success" : null);
        }
    }

    private sealed class InMemorySecretStore(string key, string value) : ISecretStore
    {
        public Task SetAsync(string secretKey, string secretValue, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string?> GetAsync(string secretKey, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(secretKey == key ? value : null);

        public Task RemoveAsync(string secretKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class CountingProcessManager : IDoclingProcessManager
    {
        internal int AcquireCount { get; private set; }
        internal int StopCount { get; private set; }
        public Task<DoclingReadyEndpoint> EnsureReadyAsync(CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task<IDoclingProcessLease> AcquireAsync(CancellationToken cancellationToken)
        {
            AcquireCount++;
            throw new InvalidOperationException("Remote conversion must never acquire a local process.");
        }
        public Task StopAsync(CancellationToken cancellationToken) { StopCount++; return Task.CompletedTask; }
        public DoclingProcessSnapshot GetSnapshot() => new(DoclingProcessState.Stopped, null, null, null, null, null, null);
    }

    private sealed class TestLibrary : IDisposable
    {
        internal TestLibrary()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"loregrove-docling-pipeline-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // SQLite connection pooling can briefly retain a Windows handle after provider disposal.
            }
        }
    }
}
