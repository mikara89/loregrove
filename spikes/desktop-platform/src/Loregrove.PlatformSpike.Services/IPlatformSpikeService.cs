namespace Loregrove.PlatformSpike.Services;

public interface IPlatformSpikeService
{
    Task<IReadOnlyList<DemoDocument>> GetDocumentsAsync(CancellationToken cancellationToken);
    Task<string> RunDemoOperationAsync(IProgress<int> progress, CancellationToken cancellationToken);
    Task<ReviewDecision> SetReviewDecisionAsync(ReviewDecision decision, CancellationToken cancellationToken);
    ReviewDecision CurrentReviewDecision { get; }
    GraphViewModel GetGraph();
}
