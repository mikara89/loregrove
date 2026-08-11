namespace Loregrove.PlatformSpike.Services;

public sealed record DemoDocument(int Id, string Title, string SourceType, string ProcessingState, DateOnly ImportedDate, string Category);
public sealed record PickedFile(string Name, string FullPath, long? Size);
public sealed record PickedFolder(string Name, string FullPath);

public enum ReviewDecision { Unresolved, Same, Different, NotSure }

public sealed record GraphNode(string Id, string Label, string Category);
public sealed record GraphEdge(string Id, string Source, string Target);
public sealed record GraphViewModel(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges);
