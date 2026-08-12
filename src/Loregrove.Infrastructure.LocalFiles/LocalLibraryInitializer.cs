using Loregrove.Application.Storage;

namespace Loregrove.Infrastructure.LocalFiles;

public sealed class LocalLibraryInitializer(ILibraryPaths paths) : ILibraryDirectoryInitializer
{
    public Task InitializeDirectoriesAsync(CancellationToken cancellationToken)
    {
        var directories = new[]
        {
            paths.Root,
            paths.Objects,
            paths.Artifacts,
            paths.Indexes,
            paths.Backups,
            paths.Logs,
        };

        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory);
        }

        return Task.CompletedTask;
    }

    public Task InitializeAsync(CancellationToken cancellationToken) =>
        InitializeDirectoriesAsync(cancellationToken);
}
