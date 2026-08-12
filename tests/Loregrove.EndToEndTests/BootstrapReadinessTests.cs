using Loregrove.Application.Client;
using Loregrove.Application.Library;
using Loregrove.Application.Platform;
using Loregrove.Domain.Sources;

namespace Loregrove.EndToEndTests;

public sealed class BootstrapReadinessTests
{
    [Fact]
    public void UiFacadeCanBeComposedWithoutHttpOrInfrastructureHandlers()
    {
        var client = new LoregroveClient(
            new StubLibraryClient(),
            new SearchClient(),
            new KnowledgeClient(),
            new ReviewClient(),
            new AskClient());

        Assert.NotNull(client.Library);
        Assert.NotNull(client.Search);
        Assert.NotNull(client.Knowledge);
        Assert.NotNull(client.Review);
        Assert.NotNull(client.Ask);
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
