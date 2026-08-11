namespace Loregrove.Application.Platform;

/// <summary>
/// Host-neutral reference to a folder selected through a platform adapter.
/// </summary>
/// <param name="Handle">Opaque adapter-owned handle; callers must not interpret it as a filesystem path.</param>
/// <param name="DisplayName">Safe name suitable for presentation.</param>
public sealed record PickedFolder(string Handle, string DisplayName);
