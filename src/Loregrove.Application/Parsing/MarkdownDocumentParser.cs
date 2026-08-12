using System.Text;
using Loregrove.Domain.Sources;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Loregrove.Application.Parsing;

public sealed class MarkdownDocumentParser : IDocumentParser
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder().Build();

    public ParserDescriptor Descriptor { get; } = ParserDescriptor.Create(
        "markdown",
        "1.0.0",
        1,
        "markdig=1.3.2;pipeline=commonmark;html=preserve-as-plain-text;links=text-only;line-ending=lf");

    public bool CanParse(ParseSourceDescriptor source) =>
        ParserSelection.Matches(source, "text/markdown", ".md", ".markdown");

    public async Task<ParsedDocumentResult> ParseAsync(
        Stream source,
        ParseSourceDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("The source stream must be readable.", nameof(source));
        }

        string markdown;
        try
        {
            using var reader = new StreamReader(
                source,
                StrictUtf8,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 8192,
                leaveOpen: true);
            markdown = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DecoderFallbackException exception)
        {
            throw new DocumentParseException("The source is not valid BOM-identified Unicode or UTF-8 text.", exception);
        }

        long controls = 0;
        TextDocumentParser.InspectText(markdown, ref controls);
        TextDocumentParser.RejectBinaryLikeText(controls);
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedSource = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var document = Markdown.Parse(normalizedSource, _pipeline);
        var blocks = new List<ParsedBlock>();
        var headings = new List<string>();
        foreach (var block in document)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddBlock(block, normalizedSource, headings, blocks, cancellationToken);
        }

        return new ParsedDocumentResult(Descriptor, blocks, new Dictionary<string, string>());
    }

    private static void AddBlock(
        Block block,
        string source,
        List<string> headings,
        List<ParsedBlock> blocks,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (block)
        {
            case HeadingBlock heading:
                var headingText = NormalizeProse(InlineText(heading.Inline));
                if (headingText.Length == 0)
                {
                    return;
                }

                while (headings.Count >= heading.Level)
                {
                    headings.RemoveAt(headings.Count - 1);
                }

                while (headings.Count < heading.Level - 1)
                {
                    headings.Add(string.Empty);
                }

                headings.Add(headingText);
                AddObservation(ParsedBlockKind.Heading, headingText, heading, source, headings, blocks);
                break;

            case ParagraphBlock paragraph:
                AddObservation(
                    ParsedBlockKind.Paragraph,
                    NormalizeProse(InlineText(paragraph.Inline)),
                    paragraph,
                    source,
                    headings,
                    blocks);
                break;

            case ListBlock list:
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    AddListItem(item, source, headings, blocks, cancellationToken);
                }

                break;

            case QuoteBlock quote:
                AddObservation(
                    ParsedBlockKind.BlockQuote,
                    NormalizeProse(ExtractContainerText(quote)),
                    quote,
                    source,
                    headings,
                    blocks);
                break;

            case CodeBlock code:
                AddObservation(
                    ParsedBlockKind.Code,
                    NormalizeCode(ExtractCode(code, source)),
                    code,
                    source,
                    headings,
                    blocks);
                break;

            case HtmlBlock html:
                AddObservation(
                    ParsedBlockKind.PlainText,
                    NormalizeProse(Slice(html, source)),
                    html,
                    source,
                    headings,
                    blocks);
                break;

            case ContainerBlock container:
                foreach (var child in container)
                {
                    AddBlock(child, source, headings, blocks, cancellationToken);
                }

                break;
        }
    }

    private static void AddListItem(
        ListItemBlock item,
        string source,
        List<string> headings,
        List<ParsedBlock> blocks,
        CancellationToken cancellationToken)
    {
        var directText = string.Join(
            "\n",
            item.Where(child => child is not ListBlock)
                .Select(ExtractBlockText)
                .Where(text => !string.IsNullOrWhiteSpace(text)));
        AddObservation(
            ParsedBlockKind.ListItem,
            NormalizeProse(directText),
            item,
            source,
            headings,
            blocks);

        foreach (var nested in item.OfType<ListBlock>())
        {
            foreach (var nestedItem in nested.OfType<ListItemBlock>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddListItem(nestedItem, source, headings, blocks, cancellationToken);
            }
        }
    }

    private static void AddObservation(
        ParsedBlockKind kind,
        string text,
        Block sourceBlock,
        string source,
        IReadOnlyList<string> headings,
        List<ParsedBlock> blocks)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var path = headings.Where(value => value.Length > 0).ToArray();
        var ordinal = blocks.Count;
        blocks.Add(new ParsedBlock(
            ordinal,
            kind,
            text,
            new MarkdownSourceLocator(
                sourceBlock.Line + 1,
                LineForOffset(source, sourceBlock.Span.End),
                ordinal,
                path),
            path));
    }

    private static string ExtractContainerText(ContainerBlock container) => string.Join(
        "\n",
        container.Select(ExtractBlockText).Where(text => !string.IsNullOrWhiteSpace(text)));

    private static string ExtractBlockText(Block block) => block switch
    {
        LeafBlock leaf when leaf.Inline is not null => InlineText(leaf.Inline),
        ContainerBlock container => ExtractContainerText(container),
        _ => string.Empty,
    };

    private static string InlineText(ContainerInline? container)
    {
        if (container is null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        AppendInline(container.FirstChild, builder);
        return builder.ToString();
    }

    private static void AppendInline(Inline? inline, StringBuilder builder)
    {
        while (inline is not null)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    builder.Append(literal.Content);
                    break;
                case CodeInline code:
                    builder.Append(code.Content);
                    break;
                case LineBreakInline:
                    builder.Append('\n');
                    break;
                case HtmlInline html:
                    builder.Append(html.Tag);
                    break;
                case AutolinkInline autolink:
                    builder.Append(autolink.Url);
                    break;
                case ContainerInline nested:
                    AppendInline(nested.FirstChild, builder);
                    break;
            }

            inline = inline.NextSibling;
        }
    }

    private static string ExtractCode(CodeBlock block, string source)
    {
        var parsedLines = block.Lines.ToString();
        if (!string.IsNullOrEmpty(parsedLines))
        {
            return parsedLines;
        }

        var raw = Slice(block, source);
        var lines = raw.Split('\n');
        if (block is FencedCodeBlock && lines.Length >= 2)
        {
            return string.Join('\n', lines.Skip(1).Take(lines.Length - 2));
        }

        return string.Join('\n', lines.Select(line => line.StartsWith("    ", StringComparison.Ordinal) ? line[4..] : line));
    }

    private static string Slice(Block block, string source)
    {
        if (block.Span.Start < 0 || block.Span.Start >= source.Length || block.Span.End < block.Span.Start)
        {
            return string.Empty;
        }

        var length = Math.Min(block.Span.End, source.Length - 1) - block.Span.Start + 1;
        return source.Substring(block.Span.Start, length);
    }

    private static int LineForOffset(string source, int offset)
    {
        var bounded = Math.Min(Math.Max(offset, 0), Math.Max(source.Length - 1, 0));
        var line = 1;
        for (var index = 0; index <= bounded && index < source.Length; index++)
        {
            if (source[index] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private static string NormalizeProse(string value) => string.Join(
        '\n',
        value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0));

    private static string NormalizeCode(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim('\n');
}
