namespace Loregrove.PlatformSpike.Services;

public sealed class PlatformSpikeService : IPlatformSpikeService
{
    private static readonly string[] SourceTypes = ["PDF", "Markdown", "Spreadsheet", "Text", "Image"];
    private static readonly string[] States = ["Ready", "Processing", "Needs review", "Imported"];
    private static readonly string[] Categories = ["Project", "Research", "Finance", "Reference", "Personal"];
    private readonly IReadOnlyList<DemoDocument> _documents = CreateDocuments();

    public ReviewDecision CurrentReviewDecision { get; private set; }

    public Task<IReadOnlyList<DemoDocument>> GetDocumentsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_documents);
    }

    public async Task<string> RunDemoOperationAsync(IProgress<int> progress, CancellationToken cancellationToken)
    {
        for (var completed = 0; completed <= 100; completed += 5)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(completed);
            await Task.Delay(60, cancellationToken);
        }
        return "Demo processing completed in the in-process C# service.";
    }

    public Task<ReviewDecision> SetReviewDecisionAsync(ReviewDecision decision, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CurrentReviewDecision = decision;
        return Task.FromResult(decision);
    }

    public GraphViewModel GetGraph()
    {
        var nodes = Enumerable.Range(1, 100)
            .Select(index => new GraphNode($"node-{index}", index % 10 == 0 ? $"Project {index / 10}" : $"Source {index:000}", Categories[index % Categories.Length]))
            .ToArray();
        var edges = new List<GraphEdge>(300);
        for (var index = 1; index <= 100; index++)
        {
            for (var offset = 1; offset <= 3; offset++)
            {
                var target = ((index + (offset * 7) - 1) % 100) + 1;
                edges.Add(new GraphEdge($"edge-{index}-{target}", $"node-{index}", $"node-{target}"));
            }
        }
        return new GraphViewModel(nodes, edges);
    }

    private static IReadOnlyList<DemoDocument> CreateDocuments() =>
        Enumerable.Range(1, 10_000)
            .Select(index => new DemoDocument(index, $"Loregrove source {index:00000}", SourceTypes[index % SourceTypes.Length], States[index % States.Length], new DateOnly(2026, 8, 11).AddDays(-(index % 730)), Categories[index % Categories.Length]))
            .ToArray();
}
