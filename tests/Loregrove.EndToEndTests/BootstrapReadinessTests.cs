using Loregrove.Application.Client;

namespace Loregrove.EndToEndTests;

public sealed class BootstrapReadinessTests
{
    [Fact]
    public void UiFacadeCanBeComposedWithoutHttpOrInfrastructureHandlers()
    {
        var client = new LoregroveClient(
            new LibraryClient(),
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
}
