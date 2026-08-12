using Loregrove.Application.Library;
using Loregrove.Domain.Sources;

namespace Loregrove.UITests;

public sealed class LibraryPresentationTests
{
    [Theory]
    [InlineData("paper.pdf", null, "PDF")]
    [InlineData("proposal.docx", "application/octet-stream", "Word")]
    [InlineData("budget.xlsx", null, "Excel")]
    [InlineData("scan.bin", "image/png", "Image")]
    [InlineData("notes.md", "text/plain", "Markdown")]
    [InlineData("readme.txt", null, "Text")]
    [InlineData("archive.zip", "application/zip", "Other")]
    public void SourceTypeUsesPresentationMetadataOnly(
        string fileName,
        string? mediaType,
        string expected) =>
        Assert.Equal(expected, UI.LibraryPresentation.SourceType(fileName, mediaType));

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(4_508_877, "4.3 MB")]
    [InlineData(1_288_490_189, "1.2 GB")]
    public void FileSizeIsHumanReadable(long bytes, string expected) =>
        Assert.Equal(expected, UI.LibraryPresentation.FileSize(bytes));

    [Fact]
    public void ImportSummaryDistinguishesDuplicatesFailuresAndCancellation()
    {
        var result = new ImportFilesResult(
        [
            new("new.txt", 1, ImportItemState.Imported),
            new("duplicate.txt", 2, ImportItemState.AlreadyExists),
            new("failed.txt", 3, ImportItemState.Failed),
            new("cancelled.txt", 4, ImportItemState.Cancelled),
        ]);

        var summary = UI.LibraryPresentation.ImportSummary(result);

        Assert.Contains("1 imported", summary, StringComparison.Ordinal);
        Assert.Contains("1 already in library", summary, StringComparison.Ordinal);
        Assert.Contains("1 failed", summary, StringComparison.Ordinal);
        Assert.Contains("1 cancelled", summary, StringComparison.Ordinal);
        Assert.Equal("Pending", UI.LibraryPresentation.ProcessingState(SourceProcessingState.PendingProcessing));
    }

    [Fact]
    public void SharedLibraryComponentsDeclareRequiredStatesAndFluentControls()
    {
        var repositoryRoot = FindRepositoryRoot();
        var library = File.ReadAllText(Path.Combine(repositoryRoot, "src", "Loregrove.UI", "Pages", "Library.razor"));
        var details = File.ReadAllText(Path.Combine(repositoryRoot, "src", "Loregrove.UI", "Pages", "SourceDetails.razor"));

        Assert.Contains("FluentDataGrid", library, StringComparison.Ordinal);
        Assert.Contains("FluentTextInput", library, StringComparison.Ordinal);
        Assert.Contains("Your library is empty", library, StringComparison.Ordinal);
        Assert.Contains("Loading library", library, StringComparison.Ordinal);
        Assert.Contains("ImportSummary", library, StringComparison.Ordinal);
        Assert.Contains("AlreadyExists", library, StringComparison.Ordinal);
        Assert.Contains("role=\"alert\"", library, StringComparison.Ordinal);
        Assert.Contains("Technical details", details, StringComparison.Ordinal);
        Assert.Contains("Content hash", details, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Loregrove.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Loregrove repository root.");
    }
}
