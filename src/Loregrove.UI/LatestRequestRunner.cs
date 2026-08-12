namespace Loregrove.UI;

/// <summary>
/// Runs replaceable UI loads and permits only the newest request to publish component state.
/// </summary>
public sealed class LatestRequestRunner<TResult> : IDisposable
{
    private CancellationTokenSource? _current;

    public async Task RunAsync(
        Func<CancellationToken, Task<TResult>> loadAsync,
        Action<TResult> publishResult,
        Action beginLoading,
        Action<Exception> publishError,
        Action endLoading)
    {
        ArgumentNullException.ThrowIfNull(loadAsync);
        ArgumentNullException.ThrowIfNull(publishResult);
        ArgumentNullException.ThrowIfNull(beginLoading);
        ArgumentNullException.ThrowIfNull(publishError);
        ArgumentNullException.ThrowIfNull(endLoading);

        var source = new CancellationTokenSource();
        var cancellationToken = source.Token;
        var previous = Interlocked.Exchange(ref _current, source);
        previous?.Cancel();
        previous?.Dispose();
        beginLoading();

        try
        {
            var result = await loadAsync(cancellationToken);
            if (IsCurrent(source))
            {
                publishResult(result);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (IsCurrent(source))
            {
                publishError(exception);
            }
        }
        finally
        {
            if (Interlocked.CompareExchange(ref _current, null, source) == source)
            {
                endLoading();
                source.Dispose();
            }
        }
    }

    public void Dispose()
    {
        var current = Interlocked.Exchange(ref _current, null);
        current?.Cancel();
        current?.Dispose();
    }

    private bool IsCurrent(CancellationTokenSource source) =>
        ReferenceEquals(Volatile.Read(ref _current), source);
}
