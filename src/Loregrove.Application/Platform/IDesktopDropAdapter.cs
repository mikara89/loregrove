namespace Loregrove.Application.Platform;

/// <summary>
/// Host-to-shared-code bridge for native file drops.
/// </summary>
/// <remarks>
/// Implementations own native handles and publish only neutral <see cref="PickedFile"/> values.
/// HTML drag/drop metadata is not a substitute for this boundary.
/// </remarks>
public interface IDesktopDropAdapter
{
    IDisposable Subscribe(
        Func<IReadOnlyList<PickedFile>, CancellationToken, ValueTask> onFilesDropped);
}
