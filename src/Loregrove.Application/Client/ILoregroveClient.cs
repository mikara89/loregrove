using Loregrove.Application.Library;
using Loregrove.Application.Platform;
using Loregrove.Application.Search;
using Loregrove.Domain.Sources;

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

public interface ILibraryClient : IApplicationAreaClient
{
    Task<LibraryPage> GetSourcesAsync(
        LibraryQuery query,
        CancellationToken cancellationToken);

    Task<LibrarySourceDetails?> GetSourceAsync(
        SourceDocumentId documentId,
        CancellationToken cancellationToken);

    Task<ImportFilesResult> PickAndImportFilesAsync(
        IProgress<ImportProgress>? progress,
        CancellationToken cancellationToken);

    Task<ImportFilesResult> ImportFilesAsync(
        IReadOnlyList<PickedFile> files,
        IProgress<ImportProgress>? progress,
        CancellationToken cancellationToken);
}

public interface ISearchClient : IApplicationAreaClient
{
    Task<LexicalSearchPage> SearchAsync(LexicalSearchQuery query, CancellationToken cancellationToken);
}

public interface IKnowledgeClient : IApplicationAreaClient;

public interface IReviewClient : IApplicationAreaClient;

public interface IAskClient : IApplicationAreaClient;
