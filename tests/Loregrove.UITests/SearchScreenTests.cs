namespace Loregrove.UITests;

public sealed class SearchScreenTests
{
    [Fact]
    public void SearchScreenUsesFluentWidgetsPlainTextAndExistingSourceRoute()
    {
        var source = File.ReadAllText(FindSearchScreen());

        Assert.Contains("<FluentTextInput", source, StringComparison.Ordinal);
        Assert.Contains("ImmediateDelay=\"300\"", source, StringComparison.Ordinal);
        Assert.Contains("LatestRequestRunner<LexicalSearchPage>", source, StringComparison.Ordinal);
        Assert.Contains("/library/{result.SourceDocumentId.Value:D}", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MarkupString", source, StringComparison.Ordinal);
    }

    private static string FindSearchScreen()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "src", "Loregrove.UI", "Pages", "Search.razor");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException("Could not locate Search.razor.");
    }
}
