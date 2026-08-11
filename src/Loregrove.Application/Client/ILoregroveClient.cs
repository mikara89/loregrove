namespace Loregrove.Application.Client;

/// <summary>
/// Stable in-process facade consumed by the shared Razor UI.
/// </summary>
public interface ILoregroveClient
{
    ILibraryClient Library { get; }

    ISearchClient Search { get; }

    IKnowledgeClient Knowledge { get; }

    IReviewClient Review { get; }

    IAskClient Ask { get; }
}

public interface ILibraryClient : IApplicationAreaClient;

public interface ISearchClient : IApplicationAreaClient;

public interface IKnowledgeClient : IApplicationAreaClient;

public interface IReviewClient : IApplicationAreaClient;

public interface IAskClient : IApplicationAreaClient;
