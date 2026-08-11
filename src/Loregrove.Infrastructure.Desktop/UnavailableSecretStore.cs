using Loregrove.Application.Security;

namespace Loregrove.Infrastructure.Desktop;

/// <summary>
/// Non-persisting bootstrap placeholder. It deliberately refuses to accept secrets.
/// </summary>
public sealed class UnavailableSecretStore : ISecretStore
{
    public Task SetAsync(string key, string value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException(
            new NotSupportedException("Secure storage has not been enabled for the current host."));
    }

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(null);
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
