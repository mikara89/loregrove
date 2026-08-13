using Loregrove.Application.Chunking;
using Loregrove.Application.Parsing;
using Loregrove.Domain.Sources;

namespace Loregrove.UnitTests;

public sealed class EvidenceAwareChunkerTests
{
    [Fact]
    public void IdenticalEvidenceProducesIdenticalChunksAndEvidenceSpans()
    {
        var chunker = new EvidenceAwareChunker(new EvidenceAwareChunkerOptions(120, 200, 20, 0));
        var document = Document(
            Observation(0, "First paragraph", ["Architecture"]),
            Observation(1, "Second paragraph", ["Architecture"]),
            Observation(2, "Security paragraph", ["Security"]));

        var first = chunker.Chunk(document, CancellationToken.None);
        var second = chunker.Chunk(document, CancellationToken.None);

        Assert.Equal(
            first.Select(chunk => (chunk.Ordinal, chunk.Text, chunk.ContextText, chunk.ContentHash, chunk.ChunkKey)),
            second.Select(chunk => (chunk.Ordinal, chunk.Text, chunk.ContextText, chunk.ContentHash, chunk.ChunkKey)));
        Assert.Equal(
            first.SelectMany(chunk => chunk.EvidenceSpans),
            second.SelectMany(chunk => chunk.EvidenceSpans));
        Assert.Equal(2, first.Count);
        Assert.Equal("Architecture", first[0].ContextText);
        Assert.Equal("First paragraph\n\nSecond paragraph", first[0].Text);
        Assert.Equal("Security", first[1].ContextText);
        Assert.Equal(2, first[0].EvidenceSpans.Count);
        Assert.Equal((0, 15), (first[0].EvidenceSpans[0].ChunkStart, first[0].EvidenceSpans[0].ChunkEnd));
        Assert.Equal((17, 33), (first[0].EvidenceSpans[1].ChunkStart, first[0].EvidenceSpans[1].ChunkEnd));
    }

    [Fact]
    public void OversizedAnchorIsSplitWithoutOverlapGapOrTextLoss()
    {
        var text = string.Join(' ', Enumerable.Repeat("evidence", 800));
        var chunker = new EvidenceAwareChunker(new EvidenceAwareChunkerOptions(1200, 2000, 200, 0));

        var chunks = chunker.Chunk(Document(Observation(0, text, ["Large section"])), CancellationToken.None);

        Assert.True(chunks.Count >= 3);
        Assert.All(chunks, chunk => Assert.InRange(chunk.Text.Length, 1, 2000));
        var spans = chunks.SelectMany(chunk => chunk.EvidenceSpans).OrderBy(span => span.AnchorStart).ToArray();
        Assert.Equal(0, spans[0].AnchorStart);
        Assert.Equal(text.Length, spans[^1].AnchorEnd);
        for (var index = 1; index < spans.Length; index++)
        {
            Assert.Equal(spans[index - 1].AnchorEnd, spans[index].AnchorStart);
        }

        Assert.Equal(text, string.Concat(spans.Select(span => text[span.AnchorStart..span.AnchorEnd])));
    }

    [Fact]
    public void HardSplitDoesNotBisectUtf16SurrogatePairs()
    {
        var text = new string('a', 1999) + "😀" + new string('b', 2500);
        var chunker = new EvidenceAwareChunker(new EvidenceAwareChunkerOptions(1200, 2000, 200, 0));

        var first = chunker.Chunk(Document(Observation(0, text, ["Unicode"])), CancellationToken.None);
        var second = chunker.Chunk(Document(Observation(0, text, ["Unicode"])), CancellationToken.None);

        Assert.All(first, chunk => Assert.True(HasOnlyValidSurrogatePairs(chunk.Text)));
        Assert.Equal(
            first.Select(chunk => (chunk.Text, chunk.ContentHash)),
            second.Select(chunk => (chunk.Text, chunk.ContentHash)));
        var spans = first.SelectMany(chunk => chunk.EvidenceSpans).OrderBy(span => span.AnchorStart);
        Assert.Equal(text, string.Concat(spans.Select(span => text[span.AnchorStart..span.AnchorEnd])));
    }

    [Theory]
    [InlineData(ParsedBlockKind.Table)]
    [InlineData(ParsedBlockKind.Code)]
    [InlineData(ParsedBlockKind.Formula)]
    public void SmallStructuredBlocksRemainAtomic(ParsedBlockKind kind)
    {
        var chunker = new EvidenceAwareChunker(new EvidenceAwareChunkerOptions(100, 200, 20, 0));
        var document = Document(
            Observation(0, "surrounding prose", ["Data"]),
            Observation(1, "structured unit", ["Data"], kind),
            Observation(2, "following prose", ["Data"]));

        var chunks = chunker.Chunk(document, CancellationToken.None);

        Assert.Equal(3, chunks.Count);
        Assert.Equal("structured unit", chunks[1].Text);
        Assert.Single(chunks[1].EvidenceSpans);
    }

    [Fact]
    public void DescriptorFingerprintIncludesEverySizingOption()
    {
        var first = new EvidenceAwareChunker(new EvidenceAwareChunkerOptions(100, 200, 20, 0));
        var changed = new EvidenceAwareChunker(new EvidenceAwareChunkerOptions(101, 200, 20, 0));

        Assert.NotEqual(first.Descriptor.ConfigurationFingerprint, changed.Descriptor.ConfigurationFingerprint);
        Assert.NotEqual(first.Descriptor.Fingerprint, changed.Descriptor.Fingerprint);
    }

    [Fact]
    public void ThousandsOfAnchorsAreProcessedInOneOrderedPass()
    {
        const int anchorCount = 5000;
        var observations = Enumerable.Range(0, anchorCount)
            .Select(index => Observation(index, $"evidence-{index:D4}", ["Bulk"]))
            .ToArray();
        var chunker = new EvidenceAwareChunker();

        var chunks = chunker.Chunk(Document(observations), CancellationToken.None);

        Assert.Equal(anchorCount, chunks.Sum(chunk => chunk.EvidenceSpans.Count));
        Assert.Equal(Enumerable.Range(0, anchorCount),
            chunks.SelectMany(chunk => chunk.EvidenceSpans).Select(span => span.AnchorOrdinal));
    }

    private static ChunkingDocument Document(params ChunkingObservation[] observations) => new(
        SourceDocumentVersionId.New(),
        ParsedArtifactId.New(),
        Hash("source"),
        Hash("artifact"),
        observations);

    private static ChunkingObservation Observation(
        int ordinal,
        string text,
        IReadOnlyList<string> headings,
        ParsedBlockKind kind = ParsedBlockKind.Paragraph) => new(
            SourceAnchorId.New(),
            ordinal,
            kind,
            text,
            ParsedArtifactSerializer.HashText(text),
            headings,
            new TextSourceLocator(ordinal + 1, ordinal + 1),
            Hash($"locator-{ordinal}"));

    private static string Hash(string value) => ParsedArtifactSerializer.HashText(value);

    private static bool HasOnlyValidSurrogatePairs(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]))
            {
                if (++index >= text.Length || !char.IsLowSurrogate(text[index]))
                {
                    return false;
                }
            }
            else if (char.IsLowSurrogate(text[index]))
            {
                return false;
            }
        }

        return true;
    }
}
