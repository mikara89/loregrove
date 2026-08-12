using Loregrove.Application.Docling;
using Loregrove.Application.Parsing;
using Loregrove.Application.Persistence;
using Loregrove.Application.Sources;
using Loregrove.Application.Storage;
using Loregrove.Domain.Sources;
using Loregrove.Infrastructure.Docling;
using Loregrove.Infrastructure.LocalFiles;
using Loregrove.Infrastructure.Sqlite;
using Loregrove.Infrastructure.Sqlite.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Loregrove.IntegrationTests;

public sealed class DoclingRealProcessTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RealChildProcessDelaysReadinessDrainsBoundedOutputAndStopsGracefully()
    {
        var executable = FindTestHostExecutable();
        var location = new DoclingPackLocation(Path.GetDirectoryName(executable)!);
        var manifest = CreateManifest(Path.GetFileName(executable));
        var validator = new FixedValidator(location, manifest);
        var control = new HttpDoclingControlClient();
        var capturingLauncher = new CapturingLauncher(new SystemChildProcessLauncher());
        var manager = new DoclingProcessManager(
            new DoclingConfiguration { Mode = DoclingMode.ManagedLocal },
            new DoclingSupervisorOptions
            {
                StartupTimeout = TimeSpan.FromSeconds(5),
                ReadinessProbeTimeout = TimeSpan.FromMilliseconds(100),
                ReadinessPollInterval = TimeSpan.FromMilliseconds(10),
                IdleTimeout = TimeSpan.FromSeconds(5),
                GracefulShutdownTimeout = TimeSpan.FromSeconds(1),
                ForcedKillTimeout = TimeSpan.FromSeconds(1),
            },
            new FixedLocator(location),
            validator,
            new TestHostCommandBuilder(new DoclingCommandBuilder()),
            new LoopbackPortAllocator(),
            capturingLauncher,
            control,
            control);
        await using (manager)
        using (control)
        {
            var startup = manager.EnsureReadyAsync(CancellationToken.None);
            await WaitUntilAsync(() => manager.GetSnapshot().ProcessId is not null);

            Assert.Equal(DoclingProcessState.Starting, manager.GetSnapshot().State);
            var endpoint = await startup;
            Assert.Equal("127.0.0.1", endpoint.Endpoint.Host);

            await manager.StopAsync(CancellationToken.None);

            var child = Assert.IsType<CapturingChildProcess>(capturingLauncher.LastProcess);
            var diagnostics = child.GetDiagnostics();
            Assert.Equal(SystemChildProcess.DiagnosticCharactersPerStream, diagnostics.StandardOutput.Length);
            Assert.Equal(SystemChildProcess.DiagnosticCharactersPerStream, diagnostics.StandardError.Length);
            Assert.EndsWith("-END" + Environment.NewLine, diagnostics.StandardOutput, StringComparison.Ordinal);
            Assert.EndsWith("-END" + Environment.NewLine, diagnostics.StandardError, StringComparison.Ordinal);
            Assert.Equal(0, child.KillTreeCalls);
            Assert.Equal(DoclingProcessState.Stopped, manager.GetSnapshot().State);
        }
    }

    [Fact]
    public async Task OptionalRealDoclingSmokeIsExplicitlyReported()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("LOREGROVE_DOCLING_SMOKE"),
            "1",
            StringComparison.Ordinal))
        {
            output.WriteLine("real Docling smoke: NOT EXECUTED (LOREGROVE_DOCLING_SMOKE is not 1)");
            return;
        }

        var packPath = Environment.GetEnvironmentVariable("LOREGROVE_DOCLING_PACK");
        Assert.False(
            string.IsNullOrWhiteSpace(packPath),
            "LOREGROVE_DOCLING_PACK must identify the real pack when the smoke test is enabled.");

        using var library = new TemporaryDirectory();
        var paths = new LocalLibraryPaths(library.Path);
        var services = new ServiceCollection();
        services.AddSingleton<ILibraryPaths>(paths);
        services.AddSingleton<ILibraryDirectoryInitializer, LocalLibraryInitializer>();
        services.AddSingleton<IObjectStore, LocalObjectStore>();
        services.AddSingleton<IArtifactStore, LocalArtifactStore>();
        services.AddLoregroveParsing();
        services.AddLoregroveDocling(configuration =>
        {
            configuration.Mode = DoclingMode.ManagedLocal;
            configuration.DeveloperPackOverridePath = packPath;
        });
        services.AddLoregroveSqlite(paths.Database);
        await using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<IDoclingProcessManager>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await provider.GetRequiredService<ILibraryInitializer>().InitializeAsync(timeout.Token);
        SourceDocumentVersionId versionId;
        await using (var scope = provider.CreateAsyncScope())
        await using (var pdf = new MemoryStream(CreateMinimalPdf(), writable: false))
        {
            var imported = await scope.ServiceProvider.GetRequiredService<ImportSourceService>().ImportAsync(
                new ImportSourceCommand("Smoke PDF", "smoke.pdf", "application/pdf", pdf),
                timeout.Token);
            versionId = imported.VersionId;
        }

        ParseSourceResult result;
        await using (var scope = provider.CreateAsyncScope())
        {
            result = await scope.ServiceProvider.GetRequiredService<ParseSourceService>()
                .ParseAsync(versionId, timeout.Token);
        }

        Assert.Equal(ParseSourceDisposition.Parsed, result.Disposition);
        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<LoregroveDbContext>();
            Assert.Equal(1, await context.ParsedArtifacts.CountAsync(timeout.Token));
            var anchors = await context.SourceAnchors.AsNoTracking().ToListAsync(timeout.Token);
            Assert.NotEmpty(anchors);
            Assert.Contains(anchors, anchor => anchor.LocatorKind == SourceLocatorKind.PagedRegion &&
                anchor.NormalizedText.Contains("Loregrove smoke evidence", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(anchors, anchor => anchor.LocatorJson.Contains("boundingBox", StringComparison.Ordinal));
        }

        await manager.StopAsync(timeout.Token);
        output.WriteLine("real Docling smoke: EXECUTED (immutable object -> managed Docling -> artifact -> paged anchor)");
    }

    [Fact]
    public async Task MissingPackEntryPointProducesTypedLaunchFailureAfterTwoAttempts()
    {
        using var locationDirectory = new TemporaryDirectory();
        var location = new DoclingPackLocation(locationDirectory.Path);
        var manifest = CreateManifest("missing-pack-launcher");
        var control = new HttpDoclingControlClient();
        var manager = new DoclingProcessManager(
            new DoclingConfiguration { Mode = DoclingMode.ManagedLocal },
            new DoclingSupervisorOptions
            {
                StartupTimeout = TimeSpan.FromMilliseconds(100),
                ReadinessProbeTimeout = TimeSpan.FromMilliseconds(20),
                ReadinessPollInterval = TimeSpan.FromMilliseconds(5),
                IdleTimeout = TimeSpan.FromSeconds(1),
                GracefulShutdownTimeout = TimeSpan.FromMilliseconds(20),
                ForcedKillTimeout = TimeSpan.FromMilliseconds(20),
            },
            new FixedLocator(location),
            new FixedValidator(location, manifest),
            new DoclingCommandBuilder(),
            new LoopbackPortAllocator(),
            new SystemChildProcessLauncher(),
            control,
            control);
        await using (manager)
        using (control)
        {
            var exception = await Assert.ThrowsAsync<DoclingProcessException>(
                () => manager.EnsureReadyAsync(CancellationToken.None));

            Assert.Equal(DoclingFailureCode.ProcessLaunchFailed, exception.Code);
            Assert.Equal(DoclingProcessState.Faulted, manager.GetSnapshot().State);
            Assert.Null(manager.GetSnapshot().ProcessId);
        }
    }

    [Fact]
    public async Task RealChildIgnoringGracefulShutdownUsesOwnedTreeKillFallback()
    {
        var executable = FindTestHostExecutable();
        var location = new DoclingPackLocation(Path.GetDirectoryName(executable)!);
        var manifest = CreateManifest(Path.GetFileName(executable));
        var control = new HttpDoclingControlClient();
        var capturingLauncher = new CapturingLauncher(new SystemChildProcessLauncher());
        var manager = new DoclingProcessManager(
            new DoclingConfiguration { Mode = DoclingMode.ManagedLocal },
            new DoclingSupervisorOptions
            {
                StartupTimeout = TimeSpan.FromSeconds(3),
                ReadinessProbeTimeout = TimeSpan.FromMilliseconds(100),
                ReadinessPollInterval = TimeSpan.FromMilliseconds(10),
                IdleTimeout = TimeSpan.FromSeconds(3),
                GracefulShutdownTimeout = TimeSpan.FromMilliseconds(50),
                ForcedKillTimeout = TimeSpan.FromSeconds(2),
            },
            new FixedLocator(location),
            new FixedValidator(location, manifest),
            new AdditionalArgumentsCommandBuilder(
                new DoclingCommandBuilder(),
                ["--ignore-shutdown"]),
            new LoopbackPortAllocator(),
            capturingLauncher,
            control,
            control);
        await using (manager)
        using (control)
        {
            await manager.EnsureReadyAsync(CancellationToken.None);
            await manager.StopAsync(CancellationToken.None);

            var child = Assert.IsType<CapturingChildProcess>(capturingLauncher.LastProcess);
            Assert.Equal(1, child.KillTreeCalls);
            Assert.True(child.HasExited);
            Assert.Equal(DoclingProcessState.Stopped, manager.GetSnapshot().State);
        }
    }

    private static DoclingProcessingPackManifest CreateManifest(string entryPoint) =>
        new(
            SchemaVersion: 1,
            CommandContractVersion: 1,
            PackVersion: "test-1.0.0",
            PythonVersion: "3.12.0",
            DoclingVersion: "test",
            DoclingServeVersion: "test",
            RuntimeIdentifier: "test",
            EntryPoint: entryPoint,
            RequiredFiles: [entryPoint]);

    private static string FindTestHostExecutable()
    {
        var root = FindRepositoryRoot();
        var executableName = OperatingSystem.IsWindows()
            ? "Loregrove.DoclingTestHost.exe"
            : "Loregrove.DoclingTestHost";
        var executable = Path.Combine(
            root,
            "tests",
            "Loregrove.DoclingTestHost",
            "bin",
            "Release",
            "net10.0",
            executableName);
        Assert.True(File.Exists(executable), $"Docling test host was not built: {executable}");
        return executable;
    }

    private static byte[] CreateMinimalPdf()
    {
        const string content = "BT /F1 24 Tf 72 720 Td (Loregrove smoke evidence) Tj ET\n";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {System.Text.Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var builder = new System.Text.StringBuilder("%PDF-1.4\n%âãÏÓ\n");
        var offsets = new List<int>();
        foreach (var (value, index) in objects.Select((value, index) => (value, index)))
        {
            offsets.Add(System.Text.Encoding.Latin1.GetByteCount(builder.ToString()));
            builder.Append(index + 1).Append(" 0 obj\n").Append(value).Append("\nendobj\n");
        }

        var xrefOffset = System.Text.Encoding.Latin1.GetByteCount(builder.ToString());
        builder.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            builder.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture))
                .Append(" 00000 n \n");
        }

        builder.Append("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n")
            .Append(xrefOffset)
            .Append("\n%%EOF\n");
        return System.Text.Encoding.Latin1.GetBytes(builder.ToString());
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Loregrove.Core.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Loregrove repository root.");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!predicate())
        {
            await Task.Delay(10, cancellation.Token);
        }
    }

    private sealed class FixedLocator(DoclingPackLocation location) : IDoclingPackLocator
    {
        public Task<DoclingPackLocation?> LocateAsync(CancellationToken cancellationToken) =>
            Task.FromResult<DoclingPackLocation?>(location);
    }

    private sealed class FixedValidator(
        DoclingPackLocation location,
        DoclingProcessingPackManifest manifest) : IDoclingPackValidator
    {
        public Task<DoclingPackValidationResult> ValidateAsync(
            DoclingPackLocation ignored,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DoclingPackValidationResult(
                DoclingPackAvailability.Present,
                location,
                manifest,
                "pack-valid"));
    }

    private sealed class TestHostCommandBuilder(IDoclingCommandBuilder inner) : IDoclingCommandBuilder
    {
        public DoclingProcessStartSpec Build(
            DoclingPackLocation location,
            DoclingProcessingPackManifest manifest,
            int port)
        {
            var startSpec = inner.Build(location, manifest, port);
            return startSpec with
            {
                Arguments =
                [
                    .. startSpec.Arguments,
                    "--ready-delay-ms",
                    "150",
                    "--stdout-characters",
                    "200000",
                    "--stderr-characters",
                    "200000",
                ],
            };
        }
    }

    private sealed class AdditionalArgumentsCommandBuilder(
        IDoclingCommandBuilder inner,
        IReadOnlyList<string> additionalArguments) : IDoclingCommandBuilder
    {
        public DoclingProcessStartSpec Build(
            DoclingPackLocation location,
            DoclingProcessingPackManifest manifest,
            int port)
        {
            var startSpec = inner.Build(location, manifest, port);
            return startSpec with { Arguments = [.. startSpec.Arguments, .. additionalArguments] };
        }
    }

    private sealed class CapturingLauncher(IChildProcessLauncher inner) : IChildProcessLauncher
    {
        internal IChildProcess? LastProcess { get; private set; }

        public IChildProcess Start(DoclingProcessStartSpec startSpec)
        {
            LastProcess = new CapturingChildProcess(inner.Start(startSpec));
            return LastProcess;
        }
    }

    private sealed class CapturingChildProcess(IChildProcess inner) : IChildProcess
    {
        private int _killTreeCalls;

        public int Id => inner.Id;

        public bool HasExited => inner.HasExited;

        public Task<int> ExitTask => inner.ExitTask;

        internal int KillTreeCalls => Volatile.Read(ref _killTreeCalls);

        public ChildProcessDiagnostics GetDiagnostics() => inner.GetDiagnostics();

        public void KillTree()
        {
            Interlocked.Increment(ref _killTreeCalls);
            inner.KillTree();
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "loregrove-docling-real-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
