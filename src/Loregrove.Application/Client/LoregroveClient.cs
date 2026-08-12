namespace Loregrove.Application.Client;

/// <summary>
/// Default facade composition for the production shell. Product operations arrive in later milestones.
/// </summary>
public sealed class LoregroveClient(
    ILibraryClient library,
    ISearchClient search,
    IKnowledgeClient knowledge,
    IReviewClient review,
    IAskClient ask) : ILoregroveClient
{
    public ILibraryClient Library { get; } = library;

    public ISearchClient Search { get; } = search;

    public IKnowledgeClient Knowledge { get; } = knowledge;

    public IReviewClient Review { get; } = review;

    public IAskClient Ask { get; } = ask;
}

public sealed class SearchClient : ISearchClient
{
    public string Name => "Search";
}

public sealed class KnowledgeClient : IKnowledgeClient
{
    public string Name => "Knowledge";
}

public sealed class ReviewClient : IReviewClient
{
    public string Name => "Review";
}

public sealed class AskClient : IAskClient
{
    public string Name => "Ask";
}
