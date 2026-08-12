namespace Loregrove.Application.Storage;

/// <summary>
/// Stores deterministic derived artifacts as immutable content-addressed objects.
/// </summary>
public interface IArtifactStore
{
    Task<StoredArtifact> StoreAsync(Stream content, CancellationToken cancellationToken);

    Task<Stream> OpenReadAsync(string artifactObjectKey, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(string artifactObjectKey, CancellationToken cancellationToken);
}

public sealed record StoredArtifact(string ContentHash, string ObjectKey, long ByteLength);
