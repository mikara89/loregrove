namespace Loregrove.Application.Platform;

/// <summary>
/// Host-neutral reference to a file selected or dropped through a platform adapter.
/// </summary>
/// <param name="Handle">Opaque adapter-owned handle; callers must not interpret it as a filesystem path.</param>
/// <param name="DisplayName">Safe name suitable for presentation.</param>
/// <param name="Size">File size when the host can determine it.</param>
/// <param name="ContentType">Media type when supplied by the host.</param>
public sealed record PickedFile(
    string Handle,
    string DisplayName,
    long? Size = null,
    string? ContentType = null);
