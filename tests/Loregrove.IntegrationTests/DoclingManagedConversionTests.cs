using Loregrove.Application.Docling;
using Loregrove.Application.Parsing;
using Loregrove.Domain.Sources;
using Loregrove.Infrastructure.Docling;

namespace Loregrove.IntegrationTests;

public sealed class DoclingManagedConversionTests
{
    [Fact]
    public async Task InvalidGenerationIsReacquiredAndResubmittedExactlyOnce()
    {
        var manager = new FakeProcessManager();
        var client = new CrashOnceClient(manager);
        var parser = CreateParser(manager, client);
        await using var source = new MemoryStream([1, 2, 3]);

        var result = await parser.ParseAsync(source, Source(), CancellationToken.None);

        Assert.Equal(2, manager.AcquireCount);
        Assert.Equal(2, client.SubmissionCount);
        Assert.Equal(["source.pdf", "source.pdf"], client.SafeFileNames);
        Assert.All(client.SafeFileNames, fileName => Assert.Equal(fileName, Path.GetFileName(fileName)));
        Assert.Single(result.Blocks);
        Assert.Equal("Evidence", result.Blocks[0].Text);
        Assert.True(manager.Leases[0].Disposed);
        Assert.True(manager.Leases[1].Disposed);
    }

    [Fact]
    public async Task CallerCancellationAfterSubmissionStopsOwnedGeneration()
    {
        var manager = new FakeProcessManager();
        var client = new BlockingClient();
        var parser = CreateParser(manager, client);
        await using var source = new MemoryStream([1, 2, 3]);
        using var cancellation = new CancellationTokenSource();

        var parsing = parser.ParseAsync(source, Source(), cancellation.Token);
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => parsing);
        Assert.Equal(1, manager.StopCount);
        Assert.False(manager.Leases[0].IsValid);
        Assert.True(manager.Leases[0].Disposed);
    }

    [Fact]
    public async Task ValidInfrastructureFailureIsNotTransparentlyRetried()
    {
        var manager = new FakeProcessManager();
        var client = new AlwaysTransportFailureClient();
        var parser = CreateParser(manager, client);
        await using var source = new MemoryStream([1]);

        var exception = await Assert.ThrowsAsync<ParserInfrastructureException>(() =>
            parser.ParseAsync(source, Source(), CancellationToken.None));

        Assert.Equal(ParserInfrastructureFailureCode.TransportFailure, exception.Code);
        Assert.Equal(1, manager.AcquireCount);
        Assert.Equal(1, client.SubmissionCount);
    }

    [Fact]
    public async Task PackDoclingVersionAndInputFormatAffectParserFingerprint()
    {
        var manager = new FakeProcessManager();
        var client = new AlwaysTransportFailureClient();
        var first = CreateParser(manager, client, new ValidPackInspector("2.0", "1.21"));
        var changedDocling = CreateParser(manager, client, new ValidPackInspector("2.1", "1.21"));

        var firstPdf = await first.GetDescriptorAsync(Source(), CancellationToken.None);
        var changedPdf = await changedDocling.GetDescriptorAsync(Source(), CancellationToken.None);
        var firstDocx = await first.GetDescriptorAsync(
            Source() with
            {
                OriginalFileName = "source.docx",
                MediaType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            },
            CancellationToken.None);

        Assert.NotEqual(firstPdf.Fingerprint, changedPdf.Fingerprint);
        Assert.NotEqual(firstPdf.Fingerprint, firstDocx.Fingerprint);
    }

    private static DoclingDocumentParser CreateParser(
        FakeProcessManager manager,
        IDoclingConversionClient client,
        IDoclingPackInspector? inspector = null) => new(
        new DoclingConfiguration { Mode = DoclingMode.ManagedLocal },
        DoclingConversionProfile.Conservative,
        inspector ?? new ValidPackInspector(),
        manager,
        client,
        new UnusedXlsxReader());

    private static ParseSourceDescriptor Source() => new(
        SourceDocumentVersionId.New(),
        new string('a', 64),
        @"C:\Users\private\source.pdf",
        "application/pdf");

    private static DoclingConversionResult Success() => new(
        DoclingConversionStatus.Success,
        "Evidence\n",
        """
        {
          "schema_name":"DoclingDocument","version":"1.0.0",
          "body":{"self_ref":"#/body","children":[{"$ref":"#/texts/0"}]},
          "furniture":{"self_ref":"#/furniture","children":[]},
          "groups":[],"tables":[],"pictures":[],"key_value_items":[],
          "texts":[{"self_ref":"#/texts/0","label":"paragraph","text":"Evidence","children":[],"prov":[{"page_no":1,"bbox":{"l":1,"t":10,"r":20,"b":2,"coord_origin":"BOTTOMLEFT"},"charspan":[0,8]}]}]
        }
        """,
        0,
        null);

    private sealed class ValidPackInspector(string doclingVersion = "2.0", string serveVersion = "1.21") : IDoclingPackInspector
    {
        public Task<DoclingPackValidationResult> InspectAsync(CancellationToken cancellationToken) => Task.FromResult(new DoclingPackValidationResult(
            DoclingPackAvailability.Present,
            new DoclingPackLocation("unused"),
            new DoclingProcessingPackManifest(1, 1, "1.0", "3.12", doclingVersion, serveVersion, "win-x64", "unused", ["unused"]),
            "pack-valid"));
    }

    private sealed class FakeProcessManager : IDoclingProcessManager
    {
        internal List<FakeLease> Leases { get; } = [];
        internal int AcquireCount { get; private set; }
        internal int StopCount { get; private set; }

        public Task<DoclingReadyEndpoint> EnsureReadyAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The parser must acquire a lease directly.");

        public Task<IDoclingProcessLease> AcquireAsync(CancellationToken cancellationToken)
        {
            var generation = ++AcquireCount;
            var lease = new FakeLease(new Uri($"http://127.0.0.1:{5000 + generation}/"), generation);
            Leases.Add(lease);
            return Task.FromResult<IDoclingProcessLease>(lease);
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            foreach (var lease in Leases)
            {
                lease.Invalidate();
            }

            return Task.CompletedTask;
        }

        public DoclingProcessSnapshot GetSnapshot() => new(
            DoclingProcessState.Busy,
            Leases.LastOrDefault()?.Endpoint,
            null,
            Leases.LastOrDefault()?.GenerationId,
            null,
            "test",
            null);
    }

    private sealed class FakeLease(Uri endpoint, long generationId) : IDoclingProcessLease
    {
        public Uri Endpoint { get; } = endpoint;
        public long GenerationId { get; } = generationId;
        public bool IsValid { get; private set; } = true;
        internal bool Disposed { get; private set; }
        internal void Invalidate() => IsValid = false;
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CrashOnceClient(FakeProcessManager manager) : IDoclingConversionClient
    {
        internal int SubmissionCount { get; private set; }
        internal List<string> SafeFileNames { get; } = [];

        public Task<DoclingConversionResult> ConvertAsync(
            Uri endpoint,
            DoclingConversionRequest request,
            Func<bool>? isLeaseValid,
            CancellationToken cancellationToken)
        {
            SubmissionCount++;
            SafeFileNames.Add(request.SafeFileName);
            if (SubmissionCount == 1)
            {
                manager.Leases[^1].Invalidate();
                throw new ParserInfrastructureException(
                    ParserInfrastructureFailureCode.RuntimeFailure,
                    "generation exited");
            }

            return Task.FromResult(Success());
        }
    }

    private sealed class BlockingClient : IDoclingConversionClient
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<DoclingConversionResult> ConvertAsync(
            Uri endpoint,
            DoclingConversionRequest request,
            Func<bool>? isLeaseValid,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
    }

    private sealed class AlwaysTransportFailureClient : IDoclingConversionClient
    {
        internal int SubmissionCount { get; private set; }

        public Task<DoclingConversionResult> ConvertAsync(
            Uri endpoint,
            DoclingConversionRequest request,
            Func<bool>? isLeaseValid,
            CancellationToken cancellationToken)
        {
            SubmissionCount++;
            throw new ParserInfrastructureException(
                ParserInfrastructureFailureCode.TransportFailure,
                "connection reset");
        }
    }

    private sealed class UnusedXlsxReader : IXlsxStructureReader
    {
        public Task<XlsxMappedStructure> ReadAsync(Stream stream, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("PDF conversion must not invoke the workbook reader.");
    }
}
