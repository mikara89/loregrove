using Loregrove.Application.Client;

namespace Loregrove.UnitTests;

public sealed class LoregroveClientTests
{
    [Fact]
    public void FacadeExposesStableApplicationAreas()
    {
        var client = new LoregroveClient(
            new LibraryClient(),
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
}
