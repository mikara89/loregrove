namespace Loregrove.Application.Storage;

public interface ILibraryInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken);
}

public interface ILibraryDirectoryInitializer
{
    Task InitializeDirectoriesAsync(CancellationToken cancellationToken);
}
