namespace Loregrove.PlatformSpike.Services;

public interface IDesktopPlatformService
{
    string PlatformName { get; }
    Task<IReadOnlyList<PickedFile>> PickFilesAsync(CancellationToken cancellationToken);
    Task<PickedFolder?> PickFolderAsync(CancellationToken cancellationToken);
    Task CopyTextAsync(string text, CancellationToken cancellationToken);
    Task WriteSecretAsync(string key, string secret, CancellationToken cancellationToken);
    Task<string?> ReadSecretAsync(string key, CancellationToken cancellationToken);
    Task DeleteSecretAsync(string key, CancellationToken cancellationToken);
    Task OpenFileAsync(string path, CancellationToken cancellationToken);
    Task RevealFileAsync(string path, CancellationToken cancellationToken);
}
