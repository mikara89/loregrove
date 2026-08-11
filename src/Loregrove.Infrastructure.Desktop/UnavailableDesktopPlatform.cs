using Loregrove.Application.Platform;

namespace Loregrove.Infrastructure.Desktop;

/// <summary>
/// Safe bootstrap placeholder replaced by Windows and Mac Catalyst adapters as capabilities ship.
/// </summary>
public sealed class UnavailableDesktopPlatform : IDesktopPlatform
{
    public Task<IReadOnlyList<PickedFile>> PickFilesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<PickedFile>>([]);
    }

    public Task<PickedFolder?> PickFolderAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<PickedFolder?>(null);
    }

    public Task OpenExternalFileAsync(string path, CancellationToken cancellationToken) =>
        UnsupportedAsync(cancellationToken);

    public Task RevealFileAsync(string path, CancellationToken cancellationToken) =>
        UnsupportedAsync(cancellationToken);

    public Task SetClipboardTextAsync(string text, CancellationToken cancellationToken) =>
        UnsupportedAsync(cancellationToken);

    private static Task UnsupportedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException(
            new NotSupportedException("This desktop capability has not been enabled for the current host."));
    }
}
