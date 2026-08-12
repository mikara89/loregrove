using Loregrove.Application.Docling;

namespace Loregrove.Infrastructure.Docling;

public sealed class DoclingConfiguration
{
    public DoclingMode Mode { get; set; } = DoclingMode.ManagedLocal;

    public string? DeveloperPackOverridePath { get; set; }

    public string ApplicationBasePath { get; set; } = AppContext.BaseDirectory;

    public int Port { get; set; }
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
