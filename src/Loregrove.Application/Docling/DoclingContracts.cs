namespace Loregrove.Application.Docling;

public enum DoclingMode
{
    Disabled,
    ManagedLocal,
    OneShot,
    Remote,
}

public enum DoclingProcessState
{
    Stopped,
    Starting,
    Ready,
    Busy,
    Idle,
    Stopping,
    Faulted,
}

public enum DoclingFailureCode
{
    Disabled,
    LocalProcessUnavailableForMode,
    PackMissing,
    PackInvalid,
    UnsupportedRuntime,
    ProcessLaunchFailed,
    ReadinessTimeout,
    ProcessExited,
    ShutdownFailed,
    PortUnavailable,
}

public sealed record DoclingReadyEndpoint(Uri Endpoint, long GenerationId);

public sealed record DoclingProcessSnapshot(
    DoclingProcessState State,
    Uri? Endpoint,
    int? ProcessId,
    long? GenerationId,
    DateTimeOffset? StartedAt,
    string? PackVersion,
    DoclingFailureCode? LastFailureCode);

public interface IDoclingProcessLease : IAsyncDisposable
{
    Uri Endpoint { get; }

    long GenerationId { get; }

    bool IsValid { get; }
}

public interface IDoclingProcessManager
{
    Task<DoclingReadyEndpoint> EnsureReadyAsync(CancellationToken cancellationToken);

    Task<IDoclingProcessLease> AcquireAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);

    DoclingProcessSnapshot GetSnapshot();
}

public sealed class DoclingProcessException : Exception
{
    public DoclingProcessException(DoclingFailureCode code, string message)
        : base(message)
    {
        Code = code;
    }

    public DoclingFailureCode Code { get; }
}
