namespace Loregrove.Application.Security;

/// <summary>
/// Boundary for host-backed secret storage. Secrets never belong in the library database.
/// </summary>
public interface ISecretStore
{
    Task SetAsync(string key, string value, CancellationToken cancellationToken);

    Task<string?> GetAsync(string key, CancellationToken cancellationToken);

    Task RemoveAsync(string key, CancellationToken cancellationToken);
}
