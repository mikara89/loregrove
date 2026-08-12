using System.Net.Sockets;
using Loregrove.Application.Docling;

namespace Loregrove.Infrastructure.Docling;

internal sealed class DoclingProcessManager : IDoclingProcessManager, IDisposable, IAsyncDisposable
{
    private const int MaximumLaunchAttempts = 2;

    private readonly DoclingConfiguration _configuration;
    private readonly DoclingSupervisorOptions _options;
    private readonly IDoclingPackLocator _packLocator;
    private readonly IDoclingPackValidator _packValidator;
    private readonly IDoclingCommandBuilder _commandBuilder;
    private readonly ILoopbackPortAllocator _portAllocator;
    private readonly IChildProcessLauncher _processLauncher;
    private readonly IDoclingReadinessProbe _readinessProbe;
    private readonly IDoclingShutdownSignal _shutdownSignal;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _leaseGate = new(1, 1);
    private readonly CancellationTokenSource _disposalCancellation = new();
    private DoclingProcessSnapshot _snapshot = new(
        DoclingProcessState.Stopped,
        Endpoint: null,
        ProcessId: null,
        GenerationId: null,
        StartedAt: null,
        PackVersion: null,
        LastFailureCode: null);
    private OwnedProcess? _ownedProcess;
    private Task<DoclingReadyEndpoint>? _startupTask;
    private Task? _stopTask;
    private CancellationTokenSource? _runCancellation;
    private CancellationTokenSource? _idleCancellation;
    private CancellationTokenSource _leaseAdmissionCancellation = new();
    private ChildProcessDiagnostics? _lastFailureDiagnostics;
    private long _nextGeneration;
    private int _disposed;

    public DoclingProcessManager(
        DoclingConfiguration configuration,
        DoclingSupervisorOptions options,
        IDoclingPackLocator packLocator,
        IDoclingPackValidator packValidator,
        IDoclingCommandBuilder commandBuilder,
        ILoopbackPortAllocator portAllocator,
        IChildProcessLauncher processLauncher,
        IDoclingReadinessProbe readinessProbe,
        IDoclingShutdownSignal shutdownSignal)
    {
        _configuration = configuration;
        _options = options;
        _packLocator = packLocator;
        _packValidator = packValidator;
        _commandBuilder = commandBuilder;
        _portAllocator = portAllocator;
        _processLauncher = processLauncher;
        _readinessProbe = readinessProbe;
        _shutdownSignal = shutdownSignal;
        _options.Validate();

        if (_configuration.Port is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuration),
                "The configured Docling port must be zero or a valid TCP port.");
        }
    }

    public Task<DoclingReadyEndpoint> EnsureReadyAsync(CancellationToken cancellationToken) =>
        EnsureReadyCoreAsync(forLease: false, cancellationToken);

    public async Task<IDoclingProcessLease> AcquireAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateManagedLocalMode();
        var admissionToken = await WaitForLeaseAdmissionAsync(cancellationToken);
        using var leaseWaitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            admissionToken);
        try
        {
            await _leaseGate.WaitAsync(leaseWaitCancellation.Token);
        }
        catch (OperationCanceledException) when (
            admissionToken.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            throw Failure(
                DoclingFailureCode.ProcessExited,
                "The local document processor is stopping.");
        }

        var releaseGate = true;
        try
        {
            DoclingReadyEndpoint ready;
            try
            {
                ready = await EnsureReadyCoreAsync(forLease: true, leaseWaitCancellation.Token);
            }
            catch (OperationCanceledException) when (
                admissionToken.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
            {
                throw Failure(
                    DoclingFailureCode.ProcessExited,
                    "The local document processor is stopping.");
            }

            await _lifecycleGate.WaitAsync(leaseWaitCancellation.Token);
            try
            {
                if (_ownedProcess is null ||
                    _ownedProcess.GenerationId != ready.GenerationId ||
                    _ownedProcess.Process.HasExited ||
                    _snapshot.State is DoclingProcessState.Stopping or DoclingProcessState.Faulted)
                {
                    throw Failure(
                        DoclingFailureCode.ProcessExited,
                        "The local document processor stopped unexpectedly.");
                }

                CancelIdleCountdownLocked();
                TransitionLocked(DoclingProcessState.Busy, _ownedProcess);
                releaseGate = false;
                return new DoclingProcessLease(this, ready);
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }
        finally
        {
            if (releaseGate)
            {
                _leaseGate.Release();
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        StopCoreAsync(rejectQueuedLeases: true, cancellationToken);

    private async Task StopCoreAsync(
        bool rejectQueuedLeases,
        CancellationToken cancellationToken)
    {
        Task stopTask;
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_stopTask is { IsCompleted: false })
            {
                stopTask = _stopTask;
            }
            else if (_snapshot.State == DoclingProcessState.Stopped &&
                _ownedProcess is null &&
                (_startupTask is null || _startupTask.IsCompleted))
            {
                return;
            }
            else
            {
                TransitionLocked(DoclingProcessState.Stopping, _ownedProcess);
                CancelIdleCountdownLocked();
                if (rejectQueuedLeases)
                {
                    _leaseAdmissionCancellation.Cancel();
                }

                _runCancellation?.Cancel();

                var completion = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _stopTask = completion.Task;
                stopTask = completion.Task;
                _ = RunStopAsync(completion);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }

        await stopTask.WaitAsync(cancellationToken);
    }

    public DoclingProcessSnapshot GetSnapshot() => Volatile.Read(ref _snapshot);

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await StopAsync(CancellationToken.None);
        }
        finally
        {
            _disposalCancellation.Cancel();
            _disposalCancellation.Dispose();
            _leaseAdmissionCancellation.Dispose();
        }
    }

    internal ChildProcessDiagnostics? GetLastFailureDiagnostics() => _lastFailureDiagnostics;

    internal bool IsGenerationValid(long generationId)
    {
        var snapshot = GetSnapshot();
        return snapshot.GenerationId == generationId &&
            snapshot.State is DoclingProcessState.Ready or DoclingProcessState.Busy or DoclingProcessState.Idle;
    }

    private async Task<DoclingReadyEndpoint> EnsureReadyCoreAsync(
        bool forLease,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateManagedLocalMode();

        while (true)
        {
            Task? stopTask = null;
            Task<DoclingReadyEndpoint>? startupTask = null;
            DoclingReadyEndpoint? ready = null;
            var requiresOwnedProcessCleanup = false;

            await _lifecycleGate.WaitAsync(cancellationToken);
            try
            {
                if (_snapshot.State == DoclingProcessState.Stopping && _stopTask is not null)
                {
                    stopTask = _stopTask;
                }
                else if (_ownedProcess is not null &&
                    !_ownedProcess.Process.HasExited &&
                    _snapshot.State is DoclingProcessState.Ready or
                        DoclingProcessState.Busy or
                        DoclingProcessState.Idle)
                {
                    ready = _ownedProcess.ReadyEndpoint;
                    if (_snapshot.State == DoclingProcessState.Idle)
                    {
                        CancelIdleCountdownLocked();
                        TransitionLocked(
                            forLease ? DoclingProcessState.Ready : DoclingProcessState.Idle,
                            _ownedProcess);
                        if (!forLease)
                        {
                            ScheduleIdleCountdownLocked(_ownedProcess);
                        }
                    }
                }
                else
                {
                    if (_startupTask is { IsCompleted: false })
                    {
                        startupTask = _startupTask;
                    }
                    else if (_ownedProcess is not null)
                    {
                        requiresOwnedProcessCleanup = true;
                    }
                    else
                    {
                        _runCancellation?.Dispose();
                        _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                            _disposalCancellation.Token);
                        TransitionLocked(DoclingProcessState.Starting, ownedProcess: null);
                        _startupTask = StartWithRestartAsync(_runCancellation.Token);
                        _ = ObserveStartupCompletionAsync(_startupTask);
                        startupTask = _startupTask;
                    }

                }
            }
            finally
            {
                _lifecycleGate.Release();
            }

            if (stopTask is not null)
            {
                await stopTask.WaitAsync(cancellationToken);
                continue;
            }

            if (ready is not null)
            {
                return ready;
            }

            if (requiresOwnedProcessCleanup)
            {
                await StopCoreAsync(
                    rejectQueuedLeases: false,
                    cancellationToken);
                continue;
            }

            return await startupTask!.WaitAsync(cancellationToken);
        }
    }

    private async Task<CancellationToken> WaitForLeaseAdmissionAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            Task? stopTask = null;
            CancellationToken admissionToken = default;
            await _lifecycleGate.WaitAsync(cancellationToken);
            try
            {
                if (_snapshot.State == DoclingProcessState.Stopping && _stopTask is not null)
                {
                    stopTask = _stopTask;
                }
                else
                {
                    admissionToken = _leaseAdmissionCancellation.Token;
                }
            }
            finally
            {
                _lifecycleGate.Release();
            }

            if (stopTask is null)
            {
                return admissionToken;
            }

            await stopTask.WaitAsync(cancellationToken);
        }
    }

    private async Task<DoclingReadyEndpoint> StartWithRestartAsync(CancellationToken cancellationToken)
    {
        var location = await _packLocator.LocateAsync(cancellationToken);
        if (location is null)
        {
            await SetFaultedAsync(DoclingFailureCode.PackMissing);
            throw Failure(
                DoclingFailureCode.PackMissing,
                "Docling Processing Pack is not installed.");
        }

        var validation = await _packValidator.ValidateAsync(location, cancellationToken);
        if (!validation.IsValid || validation.Manifest is null)
        {
            var failureCode = validation.DiagnosticCode == "runtime-unsupported"
                ? DoclingFailureCode.UnsupportedRuntime
                : DoclingFailureCode.PackInvalid;
            await SetFaultedAsync(failureCode);
            throw Failure(
                failureCode,
                failureCode == DoclingFailureCode.UnsupportedRuntime
                    ? "The Docling Processing Pack is not compatible with this computer."
                    : "The Docling Processing Pack is invalid.");
        }

        DoclingProcessException? lastFailure = null;
        for (var attempt = 1; attempt <= MaximumLaunchAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OwnedProcess? ownedProcess = null;
            try
            {
                var port = _configuration.Port == 0 ? _portAllocator.Allocate() : _configuration.Port;
                var endpoint = CreateLoopbackEndpoint(port);
                var startSpec = _commandBuilder.Build(location, validation.Manifest, port);
                var childProcess = _processLauncher.Start(startSpec);
                ownedProcess = new(
                    Interlocked.Increment(ref _nextGeneration),
                    childProcess,
                    endpoint,
                    DateTimeOffset.UtcNow,
                    validation.Manifest.PackVersion);

                await _lifecycleGate.WaitAsync(CancellationToken.None);
                try
                {
                    if (cancellationToken.IsCancellationRequested ||
                        _snapshot.State == DoclingProcessState.Stopping)
                    {
                        ownedProcess.ExpectedExit = true;
                    }
                    else
                    {
                        _ownedProcess = ownedProcess;
                        TransitionLocked(DoclingProcessState.Starting, ownedProcess);
                    }
                }
                finally
                {
                    _lifecycleGate.Release();
                }

                if (ownedProcess.ExpectedExit)
                {
                    await TerminateProcessAsync(ownedProcess);
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new OperationCanceledException("Docling startup was stopped.");
                }

                _ = ObserveUnexpectedExitAsync(ownedProcess);
                await WaitForReadinessAsync(ownedProcess, cancellationToken);

                await _lifecycleGate.WaitAsync(cancellationToken);
                try
                {
                    if (!ReferenceEquals(_ownedProcess, ownedProcess) ||
                        ownedProcess.Process.HasExited ||
                        _snapshot.State == DoclingProcessState.Stopping)
                    {
                        throw Failure(
                            DoclingFailureCode.ProcessExited,
                            "The local document processor stopped unexpectedly.");
                    }

                    TransitionLocked(DoclingProcessState.Ready, ownedProcess);
                    _lastFailureDiagnostics = null;
                    return ownedProcess.ReadyEndpoint;
                }
                finally
                {
                    _lifecycleGate.Release();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (ownedProcess is not null)
                {
                    await CleanupFailedAttemptAsync(ownedProcess);
                }

                throw;
            }
            catch (DoclingProcessException exception)
            {
                lastFailure = exception;
                if (ownedProcess is not null)
                {
                    await CleanupFailedAttemptAsync(ownedProcess);
                }
            }
            catch (SocketException)
            {
                lastFailure = Failure(
                    DoclingFailureCode.PortUnavailable,
                    "A local port was not available for Docling.");
                if (ownedProcess is not null)
                {
                    await CleanupFailedAttemptAsync(ownedProcess);
                }
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or
                    IOException or
                    UnauthorizedAccessException or
                    System.ComponentModel.Win32Exception)
            {
                lastFailure = Failure(
                    DoclingFailureCode.ProcessLaunchFailed,
                    "Docling could not be started.");
                if (ownedProcess is not null)
                {
                    await CleanupFailedAttemptAsync(ownedProcess);
                }
            }

            if (attempt < MaximumLaunchAttempts)
            {
                await _lifecycleGate.WaitAsync(cancellationToken);
                try
                {
                    TransitionLocked(DoclingProcessState.Starting, ownedProcess: null, lastFailure?.Code);
                }
                finally
                {
                    _lifecycleGate.Release();
                }
            }
        }

        var exhaustedFailure = lastFailure ?? Failure(
            DoclingFailureCode.ProcessLaunchFailed,
            "Docling could not be started.");
        await SetFaultedAsync(exhaustedFailure.Code);
        throw exhaustedFailure;
    }

    private async Task WaitForReadinessAsync(
        OwnedProcess ownedProcess,
        CancellationToken managerCancellation)
    {
        using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(managerCancellation);
        startupCancellation.CancelAfter(_options.StartupTimeout);

        while (true)
        {
            if (ownedProcess.Process.HasExited || ownedProcess.Process.ExitTask.IsCompleted)
            {
                throw Failure(
                    DoclingFailureCode.ProcessExited,
                    "The local document processor stopped unexpectedly.");
            }

            using var probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                startupCancellation.Token);
            probeCancellation.CancelAfter(_options.ReadinessProbeTimeout);

            bool ready;
            try
            {
                var probeTask = _readinessProbe.IsReadyAsync(
                    ownedProcess.Endpoint,
                    probeCancellation.Token);
                var completed = await Task.WhenAny(probeTask, ownedProcess.Process.ExitTask);
                if (completed == ownedProcess.Process.ExitTask)
                {
                    throw Failure(
                        DoclingFailureCode.ProcessExited,
                        "The local document processor stopped unexpectedly.");
                }

                ready = await probeTask;
            }
            catch (OperationCanceledException) when (
                !managerCancellation.IsCancellationRequested &&
                !startupCancellation.IsCancellationRequested)
            {
                ready = false;
            }
            catch (OperationCanceledException) when (
                !managerCancellation.IsCancellationRequested &&
                startupCancellation.IsCancellationRequested)
            {
                throw Failure(
                    DoclingFailureCode.ReadinessTimeout,
                    "Docling did not become ready in time.");
            }

            if (ready)
            {
                return;
            }

            try
            {
                var delayTask = Task.Delay(
                    _options.ReadinessPollInterval,
                    startupCancellation.Token);
                var completed = await Task.WhenAny(delayTask, ownedProcess.Process.ExitTask);
                if (completed == ownedProcess.Process.ExitTask)
                {
                    throw Failure(
                        DoclingFailureCode.ProcessExited,
                        "The local document processor stopped unexpectedly.");
                }

                await delayTask;
            }
            catch (OperationCanceledException) when (
                !managerCancellation.IsCancellationRequested &&
                startupCancellation.IsCancellationRequested)
            {
                throw Failure(
                    DoclingFailureCode.ReadinessTimeout,
                    "Docling did not become ready in time.");
            }
        }
    }

    private async Task ObserveStartupCompletionAsync(Task<DoclingReadyEndpoint> startupTask)
    {
        try
        {
            await startupTask;
        }
        catch (Exception)
        {
            // The initiating caller receives the typed failure. This observer owns only cleanup/state.
        }

        try
        {
            await _lifecycleGate.WaitAsync(_disposalCancellation.Token);
            try
            {
                if (ReferenceEquals(_startupTask, startupTask))
                {
                    _startupTask = null;
                    if (_ownedProcess is not null && _snapshot.State == DoclingProcessState.Ready)
                    {
                        TransitionLocked(DoclingProcessState.Idle, _ownedProcess);
                        ScheduleIdleCountdownLocked(_ownedProcess);
                    }
                }
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ObserveUnexpectedExitAsync(OwnedProcess ownedProcess)
    {
        try
        {
            await ownedProcess.Process.ExitTask;
            var dispose = false;
            await _lifecycleGate.WaitAsync(_disposalCancellation.Token);
            try
            {
                if (ReferenceEquals(_ownedProcess, ownedProcess) && !ownedProcess.ExpectedExit)
                {
                    _lastFailureDiagnostics = ownedProcess.Process.GetDiagnostics();
                    _ownedProcess = null;
                    CancelIdleCountdownLocked();
                    TransitionLocked(
                        DoclingProcessState.Faulted,
                        ownedProcess: null,
                        DoclingFailureCode.ProcessExited);
                    dispose = true;
                }
            }
            finally
            {
                _lifecycleGate.Release();
            }

            if (dispose)
            {
                await ownedProcess.Process.DisposeAsync();
            }
        }
        catch (Exception)
        {
            // Exit observation is fire-and-forget infrastructure and must never escape to the UI thread.
        }
    }

    private async Task CleanupFailedAttemptAsync(OwnedProcess ownedProcess)
    {
        await _lifecycleGate.WaitAsync(CancellationToken.None);
        try
        {
            ownedProcess.ExpectedExit = true;
            if (ReferenceEquals(_ownedProcess, ownedProcess))
            {
                _ownedProcess = null;
            }

            _lastFailureDiagnostics = ownedProcess.Process.GetDiagnostics();
        }
        finally
        {
            _lifecycleGate.Release();
        }

        await TerminateProcessAsync(ownedProcess);
    }

    private async Task<bool> TerminateProcessAsync(OwnedProcess ownedProcess)
    {
        ownedProcess.ExpectedExit = true;
        if (ownedProcess.Process.HasExited)
        {
            await ownedProcess.Process.DisposeAsync();
            return true;
        }

        using (var gracefulCancellation = new CancellationTokenSource(
            _options.GracefulShutdownTimeout))
        {
            try
            {
                await _shutdownSignal.RequestShutdownAsync(
                    ownedProcess.Endpoint,
                    gracefulCancellation.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (await WaitForExitAsync(ownedProcess.Process, _options.GracefulShutdownTimeout))
        {
            await ownedProcess.Process.DisposeAsync();
            return true;
        }

        try
        {
            ownedProcess.Process.KillTree();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
                NotSupportedException or
                System.ComponentModel.Win32Exception or
                UnauthorizedAccessException)
        {
            return false;
        }

        var exited = await WaitForExitAsync(ownedProcess.Process, _options.ForcedKillTimeout);
        if (exited)
        {
            await ownedProcess.Process.DisposeAsync();
        }

        return exited;
    }

    private async Task RunStopAsync(TaskCompletionSource completion)
    {
        DoclingProcessException? failure = null;
        try
        {
            Task<DoclingReadyEndpoint>? startupTask;
            await _lifecycleGate.WaitAsync(CancellationToken.None);
            try
            {
                startupTask = _startupTask;
            }
            finally
            {
                _lifecycleGate.Release();
            }

            if (startupTask is not null)
            {
                try
                {
                    await startupTask.WaitAsync(_options.StartupTimeout + _options.ForcedKillTimeout);
                }
                catch (Exception exception) when (
                    exception is OperationCanceledException or TimeoutException or DoclingProcessException)
                {
                }
            }

            OwnedProcess? ownedProcess;
            await _lifecycleGate.WaitAsync(CancellationToken.None);
            try
            {
                ownedProcess = _ownedProcess;
                if (ownedProcess is not null)
                {
                    ownedProcess.ExpectedExit = true;
                }
            }
            finally
            {
                _lifecycleGate.Release();
            }

            if (ownedProcess is not null && !await TerminateProcessAsync(ownedProcess))
            {
                failure = Failure(
                    DoclingFailureCode.ShutdownFailed,
                    "The local document processor could not be stopped cleanly.");
            }

            await _lifecycleGate.WaitAsync(CancellationToken.None);
            try
            {
                if (ReferenceEquals(_ownedProcess, ownedProcess) && failure is null)
                {
                    _ownedProcess = null;
                }

                _startupTask = null;
                _runCancellation?.Dispose();
                _runCancellation = null;
                var previousAdmission = _leaseAdmissionCancellation;
                _leaseAdmissionCancellation = new CancellationTokenSource();
                previousAdmission.Dispose();
                TransitionLocked(
                    failure is null ? DoclingProcessState.Stopped : DoclingProcessState.Faulted,
                    failure is null ? null : _ownedProcess,
                    failure?.Code);
                _stopTask = null;
            }
            finally
            {
                _lifecycleGate.Release();
            }

            if (failure is null)
            {
                completion.SetResult();
            }
            else
            {
                completion.SetException(failure);
            }
        }
        catch (Exception exception)
        {
            try
            {
                await _lifecycleGate.WaitAsync(CancellationToken.None);
                try
                {
                    var previousAdmission = _leaseAdmissionCancellation;
                    _leaseAdmissionCancellation = new CancellationTokenSource();
                    previousAdmission.Dispose();
                    TransitionLocked(
                        DoclingProcessState.Faulted,
                        _ownedProcess,
                        DoclingFailureCode.ShutdownFailed);
                    _stopTask = null;
                }
                finally
                {
                    _lifecycleGate.Release();
                }
            }
            catch (Exception)
            {
                // Preserve the original bounded shutdown failure.
            }

            completion.TrySetException(
                exception is DoclingProcessException
                    ? exception
                    : Failure(
                        DoclingFailureCode.ShutdownFailed,
                        "The local document processor could not be stopped cleanly."));
        }
    }

    private async ValueTask ReleaseLeaseAsync(long generationId)
    {
        try
        {
            await _lifecycleGate.WaitAsync(CancellationToken.None);
            try
            {
                if (_ownedProcess?.GenerationId == generationId &&
                    _snapshot.State == DoclingProcessState.Busy &&
                    !_ownedProcess.Process.HasExited)
                {
                    TransitionLocked(DoclingProcessState.Idle, _ownedProcess);
                    ScheduleIdleCountdownLocked(_ownedProcess);
                }
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }
        finally
        {
            _leaseGate.Release();
        }
    }

    private void ScheduleIdleCountdownLocked(OwnedProcess ownedProcess)
    {
        CancelIdleCountdownLocked();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _disposalCancellation.Token);
        _idleCancellation = cancellation;
        _ = IdleCountdownAsync(ownedProcess.GenerationId, cancellation);
    }

    private async Task IdleCountdownAsync(
        long generationId,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(_options.IdleTimeout, cancellation.Token);
            var shouldStop = false;
            await _lifecycleGate.WaitAsync(cancellation.Token);
            try
            {
                shouldStop = ReferenceEquals(_idleCancellation, cancellation) &&
                    _ownedProcess?.GenerationId == generationId &&
                    _snapshot.State == DoclingProcessState.Idle;
            }
            finally
            {
                _lifecycleGate.Release();
            }

            if (shouldStop)
            {
                await StopCoreAsync(
                    rejectQueuedLeases: false,
                    CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_idleCancellation, cancellation))
            {
                _idleCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void CancelIdleCountdownLocked()
    {
        var cancellation = _idleCancellation;
        _idleCancellation = null;
        cancellation?.Cancel();
    }

    private async Task SetFaultedAsync(DoclingFailureCode failureCode)
    {
        await _lifecycleGate.WaitAsync(CancellationToken.None);
        try
        {
            if (_snapshot.State != DoclingProcessState.Stopping)
            {
                TransitionLocked(DoclingProcessState.Faulted, ownedProcess: null, failureCode);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private void TransitionLocked(
        DoclingProcessState state,
        OwnedProcess? ownedProcess,
        DoclingFailureCode? failureCode = null)
    {
        Volatile.Write(
            ref _snapshot,
            new(
                state,
                ownedProcess?.Endpoint,
                ownedProcess?.Process.Id,
                ownedProcess?.GenerationId,
                ownedProcess?.StartedAt,
                ownedProcess?.PackVersion,
                failureCode));
    }

    private void ValidateManagedLocalMode()
    {
        if (_configuration.Mode == DoclingMode.Disabled)
        {
            throw Failure(DoclingFailureCode.Disabled, "Docling is disabled.");
        }

        if (_configuration.Mode != DoclingMode.ManagedLocal)
        {
            throw Failure(
                DoclingFailureCode.LocalProcessUnavailableForMode,
                "The configured Docling mode does not own a local reusable process.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private static Uri CreateLoopbackEndpoint(int port) =>
        new UriBuilder(Uri.UriSchemeHttp, DoclingCommandBuilder.LoopbackAddress, port, "/").Uri;

    private static async Task<bool> WaitForExitAsync(IChildProcess process, TimeSpan timeout)
    {
        try
        {
            await process.ExitTask.WaitAsync(timeout);
            return true;
        }
        catch (TimeoutException)
        {
            return process.HasExited;
        }
    }

    private static DoclingProcessException Failure(DoclingFailureCode code, string message) =>
        new(code, message);

    private sealed class OwnedProcess(
        long generationId,
        IChildProcess process,
        Uri endpoint,
        DateTimeOffset startedAt,
        string packVersion)
    {
        internal long GenerationId { get; } = generationId;

        internal IChildProcess Process { get; } = process;

        internal Uri Endpoint { get; } = endpoint;

        internal DateTimeOffset StartedAt { get; } = startedAt;

        internal string PackVersion { get; } = packVersion;

        internal bool ExpectedExit { get; set; }

        internal DoclingReadyEndpoint ReadyEndpoint => new(Endpoint, GenerationId);
    }

    private sealed class DoclingProcessLease(
        DoclingProcessManager manager,
        DoclingReadyEndpoint readyEndpoint) : IDoclingProcessLease
    {
        private int _released;

        public Uri Endpoint => readyEndpoint.Endpoint;

        public long GenerationId => readyEndpoint.GenerationId;

        public bool IsValid =>
            Volatile.Read(ref _released) == 0 && manager.IsGenerationValid(GenerationId);

        public ValueTask DisposeAsync() =>
            Interlocked.Exchange(ref _released, 1) == 0
                ? manager.ReleaseLeaseAsync(GenerationId)
                : ValueTask.CompletedTask;
    }
}
