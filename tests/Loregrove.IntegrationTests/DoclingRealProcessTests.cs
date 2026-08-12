using Loregrove.Application.Docling;
using Loregrove.Infrastructure.Docling;
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

        var services = new ServiceCollection();
        services.AddLoregroveDocling(configuration =>
        {
            configuration.Mode = DoclingMode.ManagedLocal;
            configuration.DeveloperPackOverridePath = packPath;
        });
        await using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<IDoclingProcessManager>();

        await using (var lease = await manager.AcquireAsync(CancellationToken.None))
        {
            Assert.Equal("127.0.0.1", lease.Endpoint.Host);
        }

        await manager.StopAsync(CancellationToken.None);
        output.WriteLine("real Docling smoke: EXECUTED");
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
}
