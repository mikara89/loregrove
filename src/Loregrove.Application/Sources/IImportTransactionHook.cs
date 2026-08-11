namespace Loregrove.Application.Sources;

public enum ImportTransactionStage
{
    AfterDocumentAdded,
    AfterVersionAdded,
    BeforeProcessingJobAdded,
    BeforeCommit,
}

public interface IImportTransactionHook
{
    Task OnStageAsync(ImportTransactionStage stage, CancellationToken cancellationToken);
}

public sealed class NoOpImportTransactionHook : IImportTransactionHook
{
    public static NoOpImportTransactionHook Instance { get; } = new();

    private NoOpImportTransactionHook()
    {
    }

    public Task OnStageAsync(ImportTransactionStage stage, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
