using Loregrove.PlatformSpike.Services;

namespace Loregrove.PlatformSpike.Tests;

public sealed class PlatformSpikeServiceTests
{
    [Fact]
    public async Task Documents_are_deterministic_and_contain_ten_thousand_records()
    {
        var service = new PlatformSpikeService();
        var documents = await service.GetDocumentsAsync(CancellationToken.None);
        Assert.Equal(10_000, documents.Count);
        Assert.Equal("Loregrove source 00001", documents[0].Title);
        Assert.Equal(10_000, documents.Select(document => document.Id).Distinct().Count());
    }

    [Fact]
    public void Graph_has_one_hundred_nodes_and_three_hundred_edges()
    {
        var graph = new PlatformSpikeService().GetGraph();
        Assert.Equal(100, graph.Nodes.Count);
        Assert.Equal(300, graph.Edges.Count);
        Assert.All(graph.Edges, edge =>
        {
            Assert.Contains(graph.Nodes, node => node.Id == edge.Source);
            Assert.Contains(graph.Nodes, node => node.Id == edge.Target);
        });
    }

    [Fact]
    public async Task Review_decision_is_kept_in_the_in_process_service()
    {
        var service = new PlatformSpikeService();
        await service.SetReviewDecisionAsync(ReviewDecision.Different, CancellationToken.None);
        Assert.Equal(ReviewDecision.Different, service.CurrentReviewDecision);
    }

    [Fact]
    public async Task Demo_operation_reports_completion()
    {
        var service = new PlatformSpikeService();
        var values = new List<int>();
        var result = await service.RunDemoOperationAsync(new SynchronousProgress(values.Add), CancellationToken.None);
        Assert.Equal(100, values[^1]);
        Assert.Contains("completed", result, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class SynchronousProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
