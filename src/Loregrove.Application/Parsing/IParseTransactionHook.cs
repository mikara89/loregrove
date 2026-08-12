namespace Loregrove.Application.Parsing;

public enum ParseTransactionStage
{
    AfterParserSuccess = 0,
    AfterArtifactFinalized = 1,
    AfterRelationalEntitiesAdded = 2,
    BeforeCommit = 3,
}

public interface IParseTransactionHook
{
    Task OnStageAsync(ParseTransactionStage stage, CancellationToken cancellationToken);
}

public sealed class NoOpParseTransactionHook : IParseTransactionHook
{
    public static NoOpParseTransactionHook Instance { get; } = new();

    private NoOpParseTransactionHook()
    {
    }

    public Task OnStageAsync(ParseTransactionStage stage, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
