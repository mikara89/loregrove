using Loregrove.Application.Client;
using Loregrove.Application.Library;
using Loregrove.Application.Platform;
using Loregrove.Domain.Sources;

namespace Loregrove.UnitTests;

public sealed class LoregroveClientTests
{
    [Fact]
    public void FacadeExposesStableApplicationAreas()
    {
        var client = new LoregroveClient(
            new StubLibraryClient(),
            new SearchClient(),
            new KnowledgeClient(),
            new ReviewClient(),
            new AskClient());

        Assert.Equal("Library", client.Library.Name);
        Assert.Equal("Search", client.Search.Name);
        Assert.Equal("Knowledge", client.Knowledge.Name);
        Assert.Equal("Review", client.Review.Name);
        Assert.Equal("Ask", client.Ask.Name);
    }

    private sealed class StubLibraryClient : ILibraryClient
    {
        public string Name => "Library";

        public Task<LibraryPage> GetSourcesAsync(LibraryQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<LibrarySourceDetails?> GetSourceAsync(
            SourceDocumentId documentId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ImportFilesResult> PickAndImportFilesAsync(
            IProgress<ImportProgress>? progress,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ImportFilesResult> ImportFilesAsync(
            IReadOnlyList<PickedFile> files,
            IProgress<ImportProgress>? progress,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
