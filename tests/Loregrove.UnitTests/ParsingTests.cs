using System.Text;
using Loregrove.Application.Parsing;
using Loregrove.Domain.Sources;

namespace Loregrove.UnitTests;

public sealed class ParsingTests
{
    private static readonly SourceDocumentVersionId VersionId = SourceDocumentVersionId.New();
    private const string SourceHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void ParserFingerprintIncludesIdentitySchemaAndConfigurationDeterministically()
    {
        var first = ParserDescriptor.Create("text", "1.0.0", 1, "option=a");
        var same = ParserDescriptor.Create("text", "1.0.0", 1, "option=a");
        var changed = ParserDescriptor.Create("text", "1.0.1", 1, "option=a");

        Assert.Equal(first, same);
        Assert.NotEqual(first.Fingerprint, changed.Fingerprint);
        Assert.Equal(64, first.ConfigurationFingerprint.Length);
        Assert.Equal(64, first.Fingerprint.Length);
    }

    [Theory]
    [InlineData("text/plain", "conflict.md", "text")]
    [InlineData("text/markdown", "conflict.txt", "markdown")]
    [InlineData("application/octet-stream", "notes.markdown", "markdown")]
    [InlineData(null, "notes.txt", "text")]
    public void ResolverUsesSpecificSupportedMediaTypeThenExtensionFallback(
        string? mediaType,
        string filename,
        string expectedParser)
    {
        var resolver = Resolver();

        var parser = resolver.Resolve(Descriptor(filename, mediaType));

        Assert.NotNull(parser);
        Assert.Equal(expectedParser, parser.Descriptor.Id);
    }

    [Theory]
    [InlineData("application/pdf", "source.txt")]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document", "source.md")]
    [InlineData(null, "source.pdf")]
    public void ResolverDefersUnsupportedFormats(string? mediaType, string filename)
    {
        Assert.Null(Resolver().Resolve(Descriptor(filename, mediaType)));
    }

    [Theory]
    [InlineData("First paragraph.\nStill first paragraph.\n\nSecond paragraph.\n")]
    [InlineData("First paragraph.\r\nStill first paragraph.\r\n\r\nSecond paragraph.\r\n")]
    public async Task TextParserCreatesParagraphAnchorsWithOriginalLineRanges(string text)
    {
        var result = await ParseTextAsync(text);

        Assert.Collection(
            result.Blocks,
            first =>
            {
                Assert.Equal(0, first.Ordinal);
                Assert.Equal(ParsedBlockKind.PlainText, first.Kind);
                Assert.Equal("First paragraph.\nStill first paragraph.", first.Text);
                Assert.Equal(new TextSourceLocator(1, 2), first.Locator);
            },
            second =>
            {
                Assert.Equal(1, second.Ordinal);
                Assert.Equal("Second paragraph.", second.Text);
                Assert.Equal(new TextSourceLocator(4, 4), second.Locator);
            });
    }

    [Fact]
    public async Task TextParserPreservesUnicodeAndAcceptsUtf8Bom()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("Žirafa 🌳\n")).ToArray();
        await using var stream = new MemoryStream(bytes, writable: false);

        var result = await new TextDocumentParser().ParseAsync(
            stream,
            Descriptor("unicode.txt", "text/plain"),
            CancellationToken.None);

        Assert.Equal("Žirafa 🌳", Assert.Single(result.Blocks).Text);
    }

    [Fact]
    public async Task TextParserUsesUnicodeBomWhenPresent()
    {
        var encoding = Encoding.Unicode;
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes("BOM evidence\n")).ToArray();
        await using var stream = new MemoryStream(bytes, writable: false);

        var result = await new TextDocumentParser().ParseAsync(
            stream,
            Descriptor("unicode.txt", "text/plain"),
            CancellationToken.None);

        Assert.Equal("BOM evidence", Assert.Single(result.Blocks).Text);
    }

    [Fact]
    public async Task EmptyTextIsAValidZeroBlockArtifact()
    {
        var result = await ParseTextAsync(string.Empty);

        Assert.Empty(result.Blocks);
    }

    [Fact]
    public async Task TextParserHandlesLongLinesAndLargeLineCountsWithoutChangingOrder()
    {
        var longLine = new string('x', 3_000_000);
        var source = longLine + "\n\n" +
            string.Join("\n\n", Enumerable.Range(0, 5000).Select(index => $"{index}:evidence"));

        var result = await ParseTextAsync(source);

        Assert.Equal(5001, result.Blocks.Count);
        Assert.Equal(3_000_000, result.Blocks[0].Text.Length);
        Assert.Equal("4999:evidence", result.Blocks[^1].Text);
    }

    [Fact]
    public async Task TextParserRejectsInvalidUtf8AndBinaryLikeText()
    {
        await using var invalidUtf8 = new MemoryStream([0xC3, 0x28], writable: false);
        await Assert.ThrowsAsync<DocumentParseException>(() => new TextDocumentParser().ParseAsync(
            invalidUtf8,
            Descriptor("bad.txt", "text/plain"),
            CancellationToken.None));

        await using var binary = new MemoryStream(Encoding.UTF8.GetBytes("hello\0world"), writable: false);
        await Assert.ThrowsAsync<DocumentParseException>(() => new TextDocumentParser().ParseAsync(
            binary,
            Descriptor("bad.txt", "text/plain"),
            CancellationToken.None));

        await using var controls = new MemoryStream([0x01, 0x02, 0x03], writable: false);
        await Assert.ThrowsAsync<DocumentParseException>(() => new TextDocumentParser().ParseAsync(
            controls,
            Descriptor("bad.txt", "text/plain"),
            CancellationToken.None));
    }

    [Fact]
    public async Task TextParserHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("text"), writable: false);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TextDocumentParser().ParseAsync(
            stream,
            Descriptor("cancelled.txt", "text/plain"),
            cancellation.Token));
    }

    [Fact]
    public async Task MarkdownParserEmitsOrderedAstBlocksHeadingPathsAndSourceLines()
    {
        const string markdown = """
            # Architecture

            Paragraph ž.

            ## Persistence

            - item one
            - item two

            > quote

            ```text
            code
            ```
            """;

        var result = await ParseMarkdownAsync(markdown);

        Assert.Equal(
            [
                ParsedBlockKind.Heading,
                ParsedBlockKind.Paragraph,
                ParsedBlockKind.Heading,
                ParsedBlockKind.ListItem,
                ParsedBlockKind.ListItem,
                ParsedBlockKind.BlockQuote,
                ParsedBlockKind.Code,
            ],
            result.Blocks.Select(block => block.Kind));
        Assert.Equal(["Architecture", "Persistence"], result.Blocks[3].HeadingPath);
        Assert.Equal("item one", result.Blocks[3].Text);
        Assert.Equal("quote", result.Blocks[5].Text);
        Assert.Equal("code", result.Blocks[6].Text);

        var heading = Assert.IsType<MarkdownSourceLocator>(result.Blocks[0].Locator);
        var paragraph = Assert.IsType<MarkdownSourceLocator>(result.Blocks[1].Locator);
        var listItem = Assert.IsType<MarkdownSourceLocator>(result.Blocks[3].Locator);
        var code = Assert.IsType<MarkdownSourceLocator>(result.Blocks[6].Locator);
        Assert.Equal((1, 1), (heading.StartLine, heading.EndLine));
        Assert.Equal((3, 3), (paragraph.StartLine, paragraph.EndLine));
        Assert.Equal((7, 7), (listItem.StartLine, listItem.EndLine));
        Assert.Equal((12, 14), (code.StartLine, code.EndLine));
    }

    [Fact]
    public async Task MarkdownParserMapsThousandsOfBlocksWithPrecomputedLinePositions()
    {
        const int blockCount = 5_000;
        var markdown = string.Join(
            "\n\n",
            Enumerable.Range(1, blockCount).Select(index => $"Paragraph {index}."));

        var result = await ParseMarkdownAsync(markdown);

        Assert.Equal(blockCount, result.Blocks.Count);
        Assert.Equal("Paragraph 1.", result.Blocks[0].Text);
        Assert.Equal($"Paragraph {blockCount}.", result.Blocks[^1].Text);
        var first = Assert.IsType<MarkdownSourceLocator>(result.Blocks[0].Locator);
        var last = Assert.IsType<MarkdownSourceLocator>(result.Blocks[^1].Locator);
        Assert.Equal((1, 1), (first.StartLine, first.EndLine));
        Assert.Equal((blockCount * 2 - 1, blockCount * 2 - 1), (last.StartLine, last.EndLine));
    }

    [Fact]
    public async Task MarkdownLinksImagesAndRawHtmlStayLocalSourceObservations()
    {
        const string markdown = """
            [OpenAI](https://example.com)

            ![diagram](https://example.com/image.png)

            <script src="https://example.com/evil.js"></script>
            """;

        var result = await ParseMarkdownAsync(markdown);

        Assert.Equal("OpenAI", result.Blocks[0].Text);
        Assert.Equal("diagram", result.Blocks[1].Text);
        Assert.Contains("<script", result.Blocks[2].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("https://example.com/image.png", result.Blocks[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MarkdownParserPreservesIndentedAndUnclosedFencedCode()
    {
        const string markdown = """
                indented code

            ```text
            unclosed code
            """;

        var result = await ParseMarkdownAsync(markdown);

        Assert.Equal([ParsedBlockKind.Code, ParsedBlockKind.Code], result.Blocks.Select(block => block.Kind));
        Assert.Equal("indented code", result.Blocks[0].Text);
        Assert.Equal("unclosed code", result.Blocks[1].Text);
    }

    [Fact]
    public async Task ParsedArtifactSerializationAndLocatorHashesAreDeterministic()
    {
        var source = Descriptor("stable.md", "text/markdown");
        var parser = new MarkdownDocumentParser();
        var bytes = Encoding.UTF8.GetBytes("# Stable\n\nObservation.\n");
        await using var firstStream = new MemoryStream(bytes, writable: false);
        await using var secondStream = new MemoryStream(bytes, writable: false);
        var first = await parser.ParseAsync(firstStream, source, CancellationToken.None);
        var second = await parser.ParseAsync(secondStream, source, CancellationToken.None);

        var firstArtifact = ParsedArtifactSerializer.Serialize(source, first);
        var secondArtifact = ParsedArtifactSerializer.Serialize(source, second);

        Assert.Equal(first.Blocks.Select(block => (block.Ordinal, block.Kind, block.Text)),
            second.Blocks.Select(block => (block.Ordinal, block.Kind, block.Text)));
        Assert.Equal(firstArtifact.ContentHash, secondArtifact.ContentHash);
        Assert.Equal(firstArtifact.Bytes, secondArtifact.Bytes);
        var firstLocator = Assert.IsType<MarkdownSourceLocator>(first.Blocks[1].Locator);
        var secondLocator = Assert.IsType<MarkdownSourceLocator>(second.Blocks[1].Locator);
        Assert.Equal(
            (firstLocator.StartLine, firstLocator.EndLine, firstLocator.BlockOrdinal),
            (secondLocator.StartLine, secondLocator.EndLine, secondLocator.BlockOrdinal));
        Assert.Equal(firstLocator.HeadingPath, secondLocator.HeadingPath);
    }

    private static DocumentParserResolver Resolver() => new(
        [new TextDocumentParser(), new MarkdownDocumentParser()]);

    private static ParseSourceDescriptor Descriptor(string filename, string? mediaType) =>
        new(VersionId, SourceHash, filename, mediaType);

    private static async Task<ParsedDocumentResult> ParseTextAsync(string text)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text), writable: false);
        return await new TextDocumentParser().ParseAsync(
            stream,
            Descriptor("source.txt", "text/plain"),
            CancellationToken.None);
    }

    private static async Task<ParsedDocumentResult> ParseMarkdownAsync(string markdown)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(markdown), writable: false);
        return await new MarkdownDocumentParser().ParseAsync(
            stream,
            Descriptor("source.md", "text/markdown"),
            CancellationToken.None);
    }
}
