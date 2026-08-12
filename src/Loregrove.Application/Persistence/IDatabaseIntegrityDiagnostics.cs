namespace Loregrove.Application.Persistence;

public interface IDatabaseIntegrityDiagnostics
{
    Task<DatabaseIntegrityResult> QuickCheckAsync(CancellationToken cancellationToken);
}

public sealed record DatabaseIntegrityResult(bool IsHealthy, string Message);
