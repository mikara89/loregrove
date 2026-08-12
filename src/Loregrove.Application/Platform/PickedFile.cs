namespace Loregrove.Application.Platform;

/// <summary>
/// Host-neutral reference to a file selected or dropped through a platform adapter.
/// </summary>
/// <param name="DisplayName">Safe name suitable for presentation.</param>
/// <param name="OriginalFileName">Untrusted source metadata; never use it to construct a path.</param>
/// <param name="ContentType">Media type when supplied by the host.</param>
/// <param name="Size">File size when the host can determine it.</param>
/// <param name="OpenReadAsync">Opens a new readable stream owned by the caller.</param>
public sealed record PickedFile(
    string DisplayName,
    string OriginalFileName,
    string? ContentType,
    long? Size,
    Func<CancellationToken, Task<Stream>> OpenReadAsync);
