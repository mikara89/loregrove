namespace Loregrove.Application.Chunking;

public enum ChunkTransactionStage
{
    AfterChunkGeneration = 0,
    AfterRelationalEntitiesAdded = 1,
    BeforeCommit = 2,
}

public interface IChunkTransactionHook
{
    Task OnStageAsync(ChunkTransactionStage stage, CancellationToken cancellationToken);
}

public sealed class NoOpChunkTransactionHook : IChunkTransactionHook
{
    public static NoOpChunkTransactionHook Instance { get; } = new();
    private NoOpChunkTransactionHook() { }
    public Task OnStageAsync(ChunkTransactionStage stage, CancellationToken cancellationToken) => Task.CompletedTask;
}
