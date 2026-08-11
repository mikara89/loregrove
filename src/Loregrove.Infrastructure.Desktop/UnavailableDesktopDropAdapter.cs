using Loregrove.Application.Platform;

namespace Loregrove.Infrastructure.Desktop;

/// <summary>
/// No-op bootstrap adapter that preserves the future native drop integration point.
/// </summary>
public sealed class UnavailableDesktopDropAdapter : IDesktopDropAdapter
{
    public IDisposable Subscribe(
        Func<IReadOnlyList<PickedFile>, CancellationToken, ValueTask> onFilesDropped)
    {
        ArgumentNullException.ThrowIfNull(onFilesDropped);
        return EmptySubscription.Instance;
    }

    private sealed class EmptySubscription : IDisposable
    {
        public static EmptySubscription Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
