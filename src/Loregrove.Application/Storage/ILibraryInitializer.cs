namespace Loregrove.Application.Storage;

public interface ILibraryInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken);
}
