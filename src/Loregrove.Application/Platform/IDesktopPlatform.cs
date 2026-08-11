namespace Loregrove.Application.Platform;

/// <summary>
/// Host-neutral boundary for desktop capabilities used by application and UI workflows.
/// </summary>
public interface IDesktopPlatform
{
    Task<IReadOnlyList<PickedFile>> PickFilesAsync(CancellationToken cancellationToken);

    Task<PickedFolder?> PickFolderAsync(CancellationToken cancellationToken);

    Task OpenExternalFileAsync(string path, CancellationToken cancellationToken);

    Task RevealFileAsync(string path, CancellationToken cancellationToken);

    Task SetClipboardTextAsync(string text, CancellationToken cancellationToken);
}
