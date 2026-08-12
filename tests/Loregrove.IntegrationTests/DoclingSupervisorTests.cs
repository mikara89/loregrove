using System.Collections.Concurrent;
using Loregrove.Application.Docling;
using Loregrove.Infrastructure.Docling;

namespace Loregrove.IntegrationTests;

public sealed class DoclingSupervisorTests
{
    [Theory]
    [InlineData(DoclingMode.Disabled, DoclingFailureCode.Disabled)]
    [InlineData(DoclingMode.OneShot, DoclingFailureCode.LocalProcessUnavailableForMode)]
    [InlineData(DoclingMode.Remote, DoclingFailureCode.LocalProcessUnavailableForMode)]
    public async Task NonManagedModesNeverLaunchLocalProcess(
        DoclingMode mode,
        DoclingFailureCode expectedFailure)
    {
        var context = CreateManager(mode: mode);
        await using var manager = context.Manager;

        var exception = await Assert.ThrowsAsync<DoclingProcessException>(
            () => manager.EnsureReadyAsync(CancellationToken.None));

        Assert.Equal(expectedFailure, exception.Code);
        Assert.Equal(0, context.Harness.LaunchCount);
    }

    [Fact]
    public async Task MissingPackFailsWithoutLaunching()
    {
        var context = CreateManager(packPresent: false);
        await using var manager = context.Manager;

        var exception = await Assert.ThrowsAsync<DoclingProcessException>(
            () => manager.AcquireAsync(CancellationToken.None));

        Assert.Equal(DoclingFailureCode.PackMissing, exception.Code);
        Assert.Equal(0, context.Harness.LaunchCount);
        Assert.Equal(DoclingProcessState.Faulted, manager.GetSnapshot().State);
    }

    [Fact]
    public async Task ThirtyTwoConcurrentEnsureReadyCallsShareOneLaunch()
    {
        var context = CreateManager(new FakeProcessBehavior(ReadyDelay: TimeSpan.FromMilliseconds(50)));
        await using var manager = context.Manager;

        var endpoints = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => manager.EnsureReadyAsync(CancellationToken.None)));

        Assert.Equal(1, context.Harness.LaunchCount);
        Assert.Single(endpoints.Select(endpoint => endpoint.Endpoint).Distinct());
        Assert.Single(endpoints.Select(endpoint => endpoint.GenerationId).Distinct());
    }

    [Fact]
    public async Task CallerCancellationDoesNotCancelSharedStartup()
    {
        var context = CreateManager(new FakeProcessBehavior(NeverReady: true));
        await using var manager = context.Manager;
        using var cancelledWait = new CancellationTokenSource();

        var survivingA = manager.EnsureReadyAsync(CancellationToken.None);
        var cancelled = manager.EnsureReadyAsync(cancelledWait.Token);
        var survivingC = manager.EnsureReadyAsync(CancellationToken.None);

        await WaitUntilAsync(() => context.Harness.LaunchCount == 1);
        cancelledWait.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.False(survivingA.IsCompleted);
        Assert.False(survivingC.IsCompleted);

        context.Harness.Processes[0].SignalReady();
        var endpoints = await Task.WhenAll(survivingA, survivingC);

        Assert.Equal(1, context.Harness.LaunchCount);
        Assert.Equal(endpoints[0], endpoints[1]);
    }

    [Fact]
    public async Task ProcessRemainsStartingUntilReadinessIsConfirmed()
    {
        var context = CreateManager(new FakeProcessBehavior(NeverReady: true));
        await using var manager = context.Manager;

        var startup = manager.EnsureReadyAsync(CancellationToken.None);
        await WaitUntilAsync(() => context.Harness.LaunchCount == 1);

        Assert.Equal(DoclingProcessState.Starting, manager.GetSnapshot().State);
        Assert.False(startup.IsCompleted);
        context.Harness.Processes[0].SignalReady();
        await startup;
    }

    [Fact]
    public async Task NeverReadyProcessTimesOutTwiceThenFaults()
    {
        var context = CreateManager(
            new FakeProcessBehavior(NeverReady: true),
            new FakeProcessBehavior(NeverReady: true),
            startupTimeout: TimeSpan.FromMilliseconds(80));
        await using var manager = context.Manager;

        var exception = await Assert.ThrowsAsync<DoclingProcessException>(
            () => manager.EnsureReadyAsync(CancellationToken.None));

        Assert.Equal(DoclingFailureCode.ReadinessTimeout, exception.Code);
        Assert.Equal(2, context.Harness.LaunchCount);
        Assert.Equal(
            2,
            context.Harness.StartSpecs
                .Select(spec => spec.Arguments[spec.Arguments.ToList().IndexOf("--port") + 1])
                .Distinct(StringComparer.Ordinal)
                .Count());
        Assert.Equal(DoclingProcessState.Faulted, manager.GetSnapshot().State);
        Assert.All(context.Harness.Processes, process => Assert.True(process.HasExited));
    }

    [Fact]
    public async Task FailedStartupCleanupRetainsUnkillableProcessAndBlocksRetryThenRecovers()
    {
        var context = CreateManager(
            new FakeProcessBehavior(
                NeverReady: true,
                IgnoreGracefulShutdown: true,
                IgnoreKill: true),
            new FakeProcessBehavior(),
            startupTimeout: TimeSpan.FromMilliseconds(50),
            gracefulTimeout: TimeSpan.FromMilliseconds(20),
            forcedKillTimeout: TimeSpan.FromMilliseconds(20));
        var manager = context.Manager;

        var startupFailure = await Assert.ThrowsAsync<DoclingProcessException>(
            () => manager.EnsureReadyAsync(CancellationToken.None));

        Assert.Equal(DoclingFailureCode.ShutdownFailed, startupFailure.Code);
        Assert.Equal(DoclingProcessState.Faulted, manager.GetSnapshot().State);
        Assert.Equal(1, context.Harness.LaunchCount);
        Assert.False(context.Harness.Processes[0].HasExited);

        context.Harness.Processes[0].Exit(-1);
        var recovered = await manager.EnsureReadyAsync(CancellationToken.None);

        Assert.Equal(2, context.Harness.LaunchCount);
        Assert.Equal(recovered.GenerationId, manager.GetSnapshot().GenerationId);
        Assert.Single(context.Harness.Processes, process => !process.HasExited);

        await manager.StopAsync(CancellationToken.None);
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task FailedStartupCleanupKillsFirstProcessBeforeRetrying()
    {
        var context = CreateManager(
            new FakeProcessBehavior(
                NeverReady: true,
                IgnoreGracefulShutdown: true),
            new FakeProcessBehavior(),
            startupTimeout: TimeSpan.FromMilliseconds(50),
            gracefulTimeout: TimeSpan.FromMilliseconds(20));
        await using var manager = context.Manager;

        var endpoint = await manager.EnsureReadyAsync(CancellationToken.None);

        Assert.Equal(2, context.Harness.LaunchCount);
        Assert.Equal(1, context.Harness.KillTreeCalls);
        Assert.True(context.Harness.Processes[0].HasExited);
        Assert.Equal(endpoint.GenerationId, manager.GetSnapshot().GenerationId);
        Assert.Single(context.Harness.Processes, process => !process.HasExited);
    }

    [Fact]
    public async Task StopBeforeChildOwnershipPublicationRetainsUnkillableGenerationThenRecovers()
    {
        var context = CreateManager(
            new FakeProcessBehavior(
                NeverReady: true,
                IgnoreGracefulShutdown: true,
                IgnoreKill: true),
            new FakeProcessBehavior(),
            gracefulTimeout: TimeSpan.FromMilliseconds(20),
            forcedKillTimeout: TimeSpan.FromMilliseconds(20));
        var manager = context.Manager;
        Task? stopTask = null;
        context.Harness.AfterProcessStarted = () =>
        {
            context.Harness.AfterProcessStarted = null;
            stopTask = manager.StopAsync(CancellationToken.None);
        };

        var startupFailure = await Assert.ThrowsAsync<DoclingProcessException>(
            () => manager.EnsureReadyAsync(CancellationToken.None));
        var stopFailure = await Assert.ThrowsAsync<DoclingProcessException>(
            () => stopTask!);

        Assert.Equal(DoclingFailureCode.ShutdownFailed, startupFailure.Code);
        Assert.Equal(DoclingFailureCode.ShutdownFailed, stopFailure.Code);
        Assert.Equal(DoclingProcessState.Faulted, manager.GetSnapshot().State);
        Assert.Equal(DoclingFailureCode.ShutdownFailed, manager.GetSnapshot().LastFailureCode);
        Assert.Equal(1, context.Harness.LaunchCount);
        Assert.False(context.Harness.Processes[0].HasExited);
        Assert.Equal(
            context.Harness.Processes[0].Id,
            manager.GetSnapshot().ProcessId);

        var blockedFailure = await Assert.ThrowsAsync<DoclingProcessException>(
            () => manager.EnsureReadyAsync(CancellationToken.None));
        Assert.Equal(DoclingFailureCode.ShutdownFailed, blockedFailure.Code);
        Assert.Equal(1, context.Harness.LaunchCount);

        context.Harness.Processes[0].Exit(-1);
        var recovered = await manager.EnsureReadyAsync(CancellationToken.None);

        Assert.Equal(2, context.Harness.LaunchCount);
        Assert.Equal(recovered.GenerationId, manager.GetSnapshot().GenerationId);
        Assert.Single(context.Harness.Processes, process => !process.HasExited);

        await manager.StopAsync(CancellationToken.None);
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task FirstStartupFailureRecoversExactlyOnce()
    {
        var context = CreateManager(
            new FakeProcessBehavior(CrashImmediately: true),
            new FakeProcessBehavior());
        await using var manager = context.Manager;

        var endpoint = await manager.EnsureReadyAsync(CancellationToken.None);

        Assert.Equal(2, context.Harness.LaunchCount);
        Assert.Equal(endpoint.GenerationId, manager.GetSnapshot().GenerationId);
        Assert.Single(context.Harness.Processes, process => !process.HasExited);
    }

    [Fact]
    public async Task TwoStartupFailuresNeverLaunchAThirdProcess()
    {
        var context = CreateManager(
            new FakeProcessBehavior(CrashImmediately: true),
            new FakeProcessBehavior(CrashImmediately: true));
        await using var manager = context.Manager;

        await Assert.ThrowsAsync<DoclingProcessException>(
            () => manager.EnsureReadyAsync(CancellationToken.None));

        Assert.Equal(2, context.Harness.LaunchCount);
        Assert.Equal(DoclingProcessState.Faulted, manager.GetSnapshot().State);
        Assert.All(context.Harness.Processes, process => Assert.True(process.HasExited));
    }

    [Fact]
    public async Task EightConcurrentAcquisitionsUseOneWarmProcessAndOneLeaseAtATime()
    {
        var context = CreateManager(idleTimeout: TimeSpan.FromSeconds(2));
        await using var manager = context.Manager;
        var active = 0;
        var maximum = 0;

        async Task UseLeaseAsync()
        {
            await using var lease = await manager.AcquireAsync(CancellationToken.None);
            var now = Interlocked.Increment(ref active);
            UpdateMaximum(ref maximum, now);
            await Task.Delay(15);
            Interlocked.Decrement(ref active);
        }

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => UseLeaseAsync()));

        Assert.Equal(1, maximum);
        Assert.Equal(1, context.Harness.LaunchCount);
        Assert.Single(context.Harness.Processes, process => !process.HasExited);
    }

    [Fact]
    public async Task CancelledLeaseWaitDoesNotReleaseActiveLease()
    {
        var context = CreateManager(idleTimeout: TimeSpan.FromSeconds(2));
        await using var manager = context.Manager;
        await using var activeLease = await manager.AcquireAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.AcquireAsync(cancellation.Token));

        Assert.True(activeLease.IsValid);
        Assert.Equal(DoclingProcessState.Busy, manager.GetSnapshot().State);
        Assert.Equal(1, context.Harness.LaunchCount);
    }

    [Fact]
    public async Task LeaseReleaseAndReacquireBeforeIdleTimeoutReuseGeneration()
    {
        var context = CreateManager(idleTimeout: TimeSpan.FromMilliseconds(250));
        await using var manager = context.Manager;
        long firstGeneration;
        await using (var first = await manager.AcquireAsync(CancellationToken.None))
        {
            firstGeneration = first.GenerationId;
        }

        await Task.Delay(40);
        await using var second = await manager.AcquireAsync(CancellationToken.None);

        Assert.Equal(firstGeneration, second.GenerationId);
        Assert.Equal(1, context.Harness.LaunchCount);
    }

    [Fact]
    public async Task ProcessStopsAfterIdleTimeout()
    {
        var context = CreateManager(idleTimeout: TimeSpan.FromMilliseconds(60));
        await using var manager = context.Manager;
        await using (var lease = await manager.AcquireAsync(CancellationToken.None))
        {
        }

        await WaitUntilAsync(
            () => manager.GetSnapshot().State == DoclingProcessState.Stopped,
            TimeSpan.FromSeconds(2));

        Assert.True(context.Harness.Processes[0].HasExited);
        Assert.Equal(1, context.Harness.LaunchCount);
    }

    [Fact]
    public async Task NewWorkWhileIdleCancelsShutdownAndReusesProcess()
    {
        var context = CreateManager(idleTimeout: TimeSpan.FromSeconds(2));
        await using var manager = context.Manager;
        long generation;
        await using (var lease = await manager.AcquireAsync(CancellationToken.None))
        {
            generation = lease.GenerationId;
        }

        await WaitUntilAsync(() => manager.GetSnapshot().State == DoclingProcessState.Idle);
        await using var replacement = await manager.AcquireAsync(CancellationToken.None);

        Assert.Equal(generation, replacement.GenerationId);
        Assert.Equal(1, context.Harness.LaunchCount);
        Assert.False(context.Harness.Processes[0].HasExited);
    }

    [Fact]
    public async Task WorkArrivingDuringIdleStopWaitsThenStartsOneCleanReplacement()
    {
        var context = CreateManager(
            new FakeProcessBehavior(ManualGracefulShutdown: true),
            new FakeProcessBehavior(),
            idleTimeout: TimeSpan.FromMilliseconds(30),
            gracefulTimeout: TimeSpan.FromSeconds(5));
        await using var manager = context.Manager;
        long firstGeneration;
        await using (var lease = await manager.AcquireAsync(CancellationToken.None))
        {
            firstGeneration = lease.GenerationId;
        }

        await WaitUntilAsync(() => context.Harness.ShutdownRequests == 1);
        Assert.Equal(DoclingProcessState.Stopping, manager.GetSnapshot().State);
        var replacementTask = manager.AcquireAsync(CancellationToken.None);

        Assert.False(replacementTask.IsCompleted);
        context.Harness.Processes[0].SignalGracefulShutdown();
        await using var replacement = await replacementTask;

        Assert.NotEqual(firstGeneration, replacement.GenerationId);
        Assert.Equal(2, context.Harness.LaunchCount);
        Assert.Equal(1, context.Harness.Processes.Count(process => !process.HasExited));
        Assert.Equal(0, context.Harness.KillTreeCalls);
    }

    [Fact]
    public async Task ConcurrentStopIsIdempotentAndGraceful()
    {
        var context = CreateManager();
        await using var manager = context.Manager;
        await manager.EnsureReadyAsync(CancellationToken.None);

        await Task.WhenAll(Enumerable.Range(0, 3)
            .Select(_ => manager.StopAsync(CancellationToken.None)));
        await manager.StopAsync(CancellationToken.None);

        Assert.Equal(1, context.Harness.ShutdownRequests);
        Assert.Equal(0, context.Harness.KillTreeCalls);
        Assert.Equal(DoclingProcessState.Stopped, manager.GetSnapshot().State);
    }

    [Fact]
    public async Task StopRejectsLeaseAlreadyQueuedBehindActiveLease()
    {
        var context = CreateManager(idleTimeout: TimeSpan.FromSeconds(2));
        await using var manager = context.Manager;
        var activeLease = await manager.AcquireAsync(CancellationToken.None);
        var queuedLease = manager.AcquireAsync(CancellationToken.None);
        await Task.Delay(20);

        var stopping = manager.StopAsync(CancellationToken.None);
        var exception = await Assert.ThrowsAsync<DoclingProcessException>(() => queuedLease);
        await stopping;
        await activeLease.DisposeAsync();

        Assert.Equal(DoclingFailureCode.ProcessExited, exception.Code);
        Assert.Equal(1, context.Harness.LaunchCount);
        Assert.Equal(DoclingProcessState.Stopped, manager.GetSnapshot().State);
    }

    [Fact]
    public async Task StopWhileBusyInvalidatesLeaseAndStopsOwnedGeneration()
    {
        var context = CreateManager(idleTimeout: TimeSpan.FromSeconds(2));
        await using var manager = context.Manager;
        await using var lease = await manager.AcquireAsync(CancellationToken.None);

        await manager.StopAsync(CancellationToken.None);

        Assert.False(lease.IsValid);
        Assert.True(context.Harness.Processes[0].HasExited);
        Assert.Equal(DoclingProcessState.Stopped, manager.GetSnapshot().State);
        Assert.Equal(1, context.Harness.LaunchCount);
    }

    [Fact]
    public async Task SynchronousHostDisposalStopsActiveOwnedProcess()
    {
        var context = CreateManager(idleTimeout: TimeSpan.FromSeconds(2));
        await context.Manager.EnsureReadyAsync(CancellationToken.None);

        context.Manager.Dispose();

        Assert.True(context.Harness.Processes[0].HasExited);
        Assert.Equal(1, context.Harness.ShutdownRequests);
        Assert.Equal(DoclingProcessState.Stopped, context.Manager.GetSnapshot().State);
    }

    [Fact]
    public async Task ShutdownUsesOwnedProcessTreeKillOnlyAfterGracefulTimeout()
    {
        var context = CreateManager(
            new FakeProcessBehavior(IgnoreGracefulShutdown: true),
            gracefulTimeout: TimeSpan.FromMilliseconds(30));
        await using var manager = context.Manager;
        await manager.EnsureReadyAsync(CancellationToken.None);

        await manager.StopAsync(CancellationToken.None);

        Assert.Equal(1, context.Harness.ShutdownRequests);
        Assert.Equal(1, context.Harness.KillTreeCalls);
        Assert.Equal(DoclingProcessState.Stopped, manager.GetSnapshot().State);
    }

    [Fact]
    public async Task FailedKillNeverAllowsASecondOwnedProcess()
    {
        var context = CreateManager(
            new FakeProcessBehavior(
                IgnoreGracefulShutdown: true,
                IgnoreKill: true),
            gracefulTimeout: TimeSpan.FromMilliseconds(20),
            forcedKillTimeout: TimeSpan.FromMilliseconds(20));
        var manager = context.Manager;
        await manager.EnsureReadyAsync(CancellationToken.None);

        var stopFailure = await Assert.ThrowsAsync<DoclingProcessException>(
            () => manager.StopAsync(CancellationToken.None));
        var restartFailure = await Assert.ThrowsAsync<DoclingProcessException>(
            () => manager.EnsureReadyAsync(CancellationToken.None));

        Assert.Equal(DoclingFailureCode.ShutdownFailed, stopFailure.Code);
        Assert.Equal(DoclingFailureCode.ShutdownFailed, restartFailure.Code);
        Assert.Equal(DoclingProcessState.Faulted, manager.GetSnapshot().State);
        Assert.Equal(1, context.Harness.LaunchCount);
        Assert.Equal(2, context.Harness.KillTreeCalls);

        context.Harness.Processes[0].Exit(-1);
        var recovered = await manager.EnsureReadyAsync(CancellationToken.None);

        Assert.Equal(2, context.Harness.LaunchCount);
        Assert.Equal(recovered.GenerationId, manager.GetSnapshot().GenerationId);

        await manager.StopAsync(CancellationToken.None);
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task UnexpectedIdleExitFaultsWithoutAutoRestart()
    {
        var context = CreateManager(idleTimeout: TimeSpan.FromSeconds(2));
        await using var manager = context.Manager;
        await manager.EnsureReadyAsync(CancellationToken.None);
        context.Harness.Processes[0].Exit(23);

        await WaitUntilAsync(() => manager.GetSnapshot().State == DoclingProcessState.Faulted);

        Assert.Equal(DoclingFailureCode.ProcessExited, manager.GetSnapshot().LastFailureCode);
        Assert.Equal(1, context.Harness.LaunchCount);
    }

    [Fact]
    public async Task BusyExitInvalidatesLeaseAndFaultsManager()
    {
        var context = CreateManager(idleTimeout: TimeSpan.FromSeconds(2));
        await using var manager = context.Manager;
        await using var lease = await manager.AcquireAsync(CancellationToken.None);
        context.Harness.Processes[0].Exit(42);

        await WaitUntilAsync(() => manager.GetSnapshot().State == DoclingProcessState.Faulted);

        Assert.False(lease.IsValid);
        Assert.Equal(1, context.Harness.LaunchCount);
    }

    [Fact]
    public async Task ManagedEndpointAndCommandAreAlwaysExplicitLoopback()
    {
        var context = CreateManager();
        await using var manager = context.Manager;

        var endpoint = await manager.EnsureReadyAsync(CancellationToken.None);
        var command = Assert.Single(context.Harness.StartSpecs);

        Assert.Equal("127.0.0.1", endpoint.Endpoint.Host);
        Assert.NotEqual(0, endpoint.Endpoint.Port);
        Assert.Contains("127.0.0.1", command.Arguments);
        Assert.DoesNotContain("0.0.0.0", command.Arguments);
        Assert.DoesNotContain("*", command.Arguments);
    }

    [Fact]
    public async Task ExplicitDiagnosticPortIsUsedWithoutChangingLoopbackHost()
    {
        const int ConfiguredPort = 43127;
        var context = CreateManager(configuredPort: ConfiguredPort);
        await using var manager = context.Manager;

        var endpoint = await manager.EnsureReadyAsync(CancellationToken.None);

        Assert.Equal(ConfiguredPort, endpoint.Endpoint.Port);
        Assert.Equal("127.0.0.1", endpoint.Endpoint.Host);
        Assert.Equal(0, context.Harness.PortAllocationCount);
    }

    private static ManagerContext CreateManager(
        FakeProcessBehavior? firstBehavior = null,
        FakeProcessBehavior? secondBehavior = null,
        DoclingMode mode = DoclingMode.ManagedLocal,
        bool packPresent = true,
        TimeSpan? startupTimeout = null,
        TimeSpan? idleTimeout = null,
        TimeSpan? gracefulTimeout = null,
        TimeSpan? forcedKillTimeout = null,
        int configuredPort = 0)
    {
        var behaviors = new[] { firstBehavior, secondBehavior }
            .Where(behavior => behavior is not null)
            .Cast<FakeProcessBehavior>()
            .ToArray();
        var harness = new FakeDoclingProcessHarness(behaviors);
        var location = packPresent ? new DoclingPackLocation(Path.GetTempPath()) : null;
        var locator = new StubPackLocator(location);
        var validator = new StubPackValidator();
        var options = new DoclingSupervisorOptions
        {
            StartupTimeout = startupTimeout ?? TimeSpan.FromSeconds(5),
            ReadinessProbeTimeout = TimeSpan.FromMilliseconds(30),
            ReadinessPollInterval = TimeSpan.FromMilliseconds(5),
            IdleTimeout = idleTimeout ?? TimeSpan.FromSeconds(1),
            GracefulShutdownTimeout = gracefulTimeout ?? TimeSpan.FromMilliseconds(50),
            ForcedKillTimeout = forcedKillTimeout ?? TimeSpan.FromMilliseconds(100),
        };
        var manager = new DoclingProcessManager(
            new DoclingConfiguration { Mode = mode, Port = configuredPort },
            options,
            locator,
            validator,
            harness,
            harness,
            harness,
            harness,
            harness);
        return new(manager, harness);
    }

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        while (true)
        {
            var observed = Volatile.Read(ref maximum);
            if (candidate <= observed || Interlocked.CompareExchange(ref maximum, candidate, observed) == observed)
            {
                return;
            }
        }
    }

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(1));
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected Docling test state was not observed.");
            }

            await Task.Delay(5);
        }
    }

    private sealed record ManagerContext(
        DoclingProcessManager Manager,
        FakeDoclingProcessHarness Harness);

    private sealed record FakeProcessBehavior(
        TimeSpan? ReadyDelay = null,
        bool NeverReady = false,
        bool CrashImmediately = false,
        bool IgnoreGracefulShutdown = false,
        bool IgnoreKill = false,
        bool ManualGracefulShutdown = false,
        TimeSpan? GracefulShutdownDelay = null);

    private sealed class StubPackLocator(DoclingPackLocation? location) : IDoclingPackLocator
    {
        public Task<DoclingPackLocation?> LocateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(location);
    }

    private sealed class StubPackValidator : IDoclingPackValidator
    {
        private static readonly DoclingProcessingPackManifest Manifest = new(
            SchemaVersion: 1,
            CommandContractVersion: 1,
            PackVersion: "test-1.0.0",
            PythonVersion: "3.12.0",
            DoclingVersion: "test",
            DoclingServeVersion: "test",
            RuntimeIdentifier: "test",
            EntryPoint: "fake-docling",
            RequiredFiles: ["fake-docling"]);

        public Task<DoclingPackValidationResult> ValidateAsync(
            DoclingPackLocation location,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DoclingPackValidationResult(
                DoclingPackAvailability.Present,
                location,
                Manifest,
                "pack-valid"));
    }

    private sealed class FakeDoclingProcessHarness :
        IChildProcessLauncher,
        IDoclingCommandBuilder,
        ILoopbackPortAllocator,
        IDoclingReadinessProbe,
        IDoclingShutdownSignal
    {
        private readonly ConcurrentQueue<FakeProcessBehavior> _behaviors;
        private readonly ConcurrentDictionary<int, FakeChildProcess> _processesByPort = new();
        private int _nextPort = 41000;
        private int _portAllocationCount;
        private int _launchCount;
        private int _shutdownRequests;
        private int _killTreeCalls;

        internal FakeDoclingProcessHarness(IEnumerable<FakeProcessBehavior> behaviors)
        {
            _behaviors = new(behaviors);
        }

        internal int LaunchCount => Volatile.Read(ref _launchCount);

        internal int ShutdownRequests => Volatile.Read(ref _shutdownRequests);

        internal int KillTreeCalls => Volatile.Read(ref _killTreeCalls);

        internal int PortAllocationCount => Volatile.Read(ref _portAllocationCount);

        internal List<FakeChildProcess> Processes { get; } = [];

        internal List<DoclingProcessStartSpec> StartSpecs { get; } = [];

        internal Action? AfterProcessStarted { get; set; }

        public int Allocate()
        {
            Interlocked.Increment(ref _portAllocationCount);
            return Interlocked.Increment(ref _nextPort);
        }

        public DoclingProcessStartSpec Build(
            DoclingPackLocation location,
            DoclingProcessingPackManifest manifest,
            int port) =>
            new(
                "fake-docling",
                location.RootPath,
                ["--host", "127.0.0.1", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture)]);

        public IChildProcess Start(DoclingProcessStartSpec startSpec)
        {
            Interlocked.Increment(ref _launchCount);
            var portIndex = startSpec.Arguments.ToList().IndexOf("--port");
            var port = int.Parse(
                startSpec.Arguments[portIndex + 1],
                System.Globalization.CultureInfo.InvariantCulture);
            _behaviors.TryDequeue(out var behavior);
            behavior ??= new FakeProcessBehavior();
            var process = new FakeChildProcess(
                id: 1000 + LaunchCount,
                behavior,
                () => Interlocked.Increment(ref _killTreeCalls));
            lock (Processes)
            {
                Processes.Add(process);
                StartSpecs.Add(startSpec);
            }

            _processesByPort[port] = process;
            if (behavior.CrashImmediately)
            {
                process.Exit(19);
            }

            AfterProcessStarted?.Invoke();

            return process;
        }

        public Task<bool> IsReadyAsync(Uri endpoint, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_processesByPort.TryGetValue(endpoint.Port, out var process) || process.HasExited)
            {
                return Task.FromResult(false);
            }

            return Task.FromResult(process.IsReady);
        }

        public async Task<bool> RequestShutdownAsync(Uri endpoint, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _shutdownRequests);
            if (!_processesByPort.TryGetValue(endpoint.Port, out var process) || process.HasExited)
            {
                return true;
            }

            if (!process.Behavior.IgnoreGracefulShutdown)
            {
                if (process.Behavior.ManualGracefulShutdown)
                {
                    await process.WaitForGracefulShutdownSignalAsync(cancellationToken);
                }

                if (process.Behavior.GracefulShutdownDelay is { } delay)
                {
                    await Task.Delay(delay, cancellationToken);
                }

                process.Exit(0);
            }

            return true;
        }
    }

    private sealed class FakeChildProcess : IChildProcess
    {
        private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
        private readonly TaskCompletionSource<int> _exit = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gracefulShutdown = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Action _onKill;
        private int _readySignaled;

        internal FakeChildProcess(int id, FakeProcessBehavior behavior, Action onKill)
        {
            Id = id;
            Behavior = behavior;
            _onKill = onKill;
        }

        public int Id { get; }

        public bool HasExited => _exit.Task.IsCompleted;

        public Task<int> ExitTask => _exit.Task;

        internal FakeProcessBehavior Behavior { get; }

        internal bool IsReady =>
            !HasExited &&
            (Volatile.Read(ref _readySignaled) != 0 ||
                (!Behavior.NeverReady &&
                    DateTimeOffset.UtcNow - _startedAt >= (Behavior.ReadyDelay ?? TimeSpan.Zero)));

        public ChildProcessDiagnostics GetDiagnostics() => new("fake stdout", "fake stderr");

        public void KillTree()
        {
            _onKill();
            if (!Behavior.IgnoreKill)
            {
                Exit(-1);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        internal void Exit(int exitCode) => _exit.TrySetResult(exitCode);

        internal void SignalReady() => Volatile.Write(ref _readySignaled, 1);

        internal void SignalGracefulShutdown() => _gracefulShutdown.TrySetResult();

        internal Task WaitForGracefulShutdownSignalAsync(CancellationToken cancellationToken) =>
            _gracefulShutdown.Task.WaitAsync(cancellationToken);
    }
}
