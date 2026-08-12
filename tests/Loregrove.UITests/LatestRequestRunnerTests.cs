namespace Loregrove.UITests;

public sealed class LatestRequestRunnerTests
{
    [Fact]
    public async Task CancelledOlderRequestCannotPublishErrorOrEndNewerLoading()
    {
        using var runner = new UI.LatestRequestRunner<int>();
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new List<int>();
        var errors = 0;
        var loading = false;

        var first = RunAsync(async cancellationToken =>
        {
            await firstRelease.Task;
            cancellationToken.ThrowIfCancellationRequested();
            return 1;
        });
        var second = RunAsync(async _ =>
        {
            await secondRelease.Task;
            return 2;
        });

        firstRelease.SetResult();
        await first;

        Assert.True(loading);
        Assert.Empty(published);
        Assert.Equal(0, errors);

        secondRelease.SetResult();
        await second;

        Assert.False(loading);
        Assert.Equal([2], published);
        Assert.Equal(0, errors);

        Task RunAsync(Func<CancellationToken, Task<int>> loadAsync) => runner.RunAsync(
            loadAsync,
            published.Add,
            () => loading = true,
            _ => errors++,
            () => loading = false);
    }

    [Fact]
    public async Task OlderCompletionCannotOverwriteNewerResult()
    {
        using var runner = new UI.LatestRequestRunner<string>();
        var oldRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? published = null;

        var oldRequest = runner.RunAsync(
            async _ =>
            {
                await oldRelease.Task;
                return "old";
            },
            value => published = value,
            () => { },
            _ => { },
            () => { });
        var newRequest = runner.RunAsync(
            async _ =>
            {
                await newRelease.Task;
                return "new";
            },
            value => published = value,
            () => { },
            _ => { },
            () => { });

        newRelease.SetResult();
        await newRequest;
        oldRelease.SetResult();
        await oldRequest;

        Assert.Equal("new", published);
    }
}
