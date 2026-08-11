namespace Loregrove.Application.Storage;

/// <summary>
/// Stores and retrieves immutable objects without exposing filesystem paths.
/// </summary>
public interface IObjectStore
{
    Task<StoredObject> StoreAsync(Stream content, CancellationToken cancellationToken);

    Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken);
}
