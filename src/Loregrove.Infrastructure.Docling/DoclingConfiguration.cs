using Loregrove.Application.Docling;

namespace Loregrove.Infrastructure.Docling;

public sealed class DoclingConfiguration
{
    public DoclingMode Mode { get; set; } = DoclingMode.ManagedLocal;

    public string? DeveloperPackOverridePath { get; set; }

    public string ApplicationBasePath { get; set; } = AppContext.BaseDirectory;

    public int Port { get; set; }

    public Uri? RemoteEndpoint { get; set; }

    public string? RemoteCredentialKey { get; set; }

    public bool AllowRemoteDocumentUpload { get; set; }

    public bool AllowInsecureRemoteEndpoint { get; set; }
}

public sealed class DoclingConversionOptions
{
    public TimeSpan SubmitTimeout { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan PollRequestTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan OverallTimeout { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan ResultTimeout { get; set; } = TimeSpan.FromMinutes(2);
    public long MaximumResponseBytes { get; set; } = 128L * 1024 * 1024;

    internal void Validate()
    {
        RequirePositive(SubmitTimeout, nameof(SubmitTimeout));
        RequirePositive(PollRequestTimeout, nameof(PollRequestTimeout));
        RequirePositive(PollInterval, nameof(PollInterval));
        RequirePositive(OverallTimeout, nameof(OverallTimeout));
        RequirePositive(ResultTimeout, nameof(ResultTimeout));
        if (MaximumResponseBytes is < 1024 or > 1024L * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumResponseBytes));
        }
    }

    private static void RequirePositive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }
}

public sealed class DoclingSupervisorOptions
{
    public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan ReadinessProbeTimeout { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan ReadinessPollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(3);

    public TimeSpan GracefulShutdownTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan ForcedKillTimeout { get; set; } = TimeSpan.FromSeconds(5);

    internal void Validate()
    {
        RequirePositive(StartupTimeout, nameof(StartupTimeout));
        RequirePositive(ReadinessProbeTimeout, nameof(ReadinessProbeTimeout));
        RequirePositive(ReadinessPollInterval, nameof(ReadinessPollInterval));
        RequirePositive(IdleTimeout, nameof(IdleTimeout));
        RequirePositive(GracefulShutdownTimeout, nameof(GracefulShutdownTimeout));
        RequirePositive(ForcedKillTimeout, nameof(ForcedKillTimeout));
    }

    private static void RequirePositive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(name, "Docling supervisor durations must be positive.");
        }
    }
}
