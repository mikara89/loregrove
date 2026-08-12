using System.Text;
using Loregrove.Domain.Sources;

namespace Loregrove.Application.Parsing;

public sealed class TextDocumentParser : IDocumentParser
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public ParserDescriptor Descriptor { get; } = ParserDescriptor.Create(
        "text",
        "1.0.0",
        1,
        "paragraphs=blank-lines;line-ending=lf;whitespace=trim-lines;encoding=bom-or-strict-utf8;binary=nul-invalid-scalar-or-control");

    public bool CanParse(ParseSourceDescriptor source) =>
        ParserSelection.Matches(source, "text/plain", ".txt");

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

        var blocks = new List<ParsedBlock>();
        var paragraph = new List<string>();
        var paragraphStart = 0;
        var lineNumber = 0;
        var suspiciousControlCount = 0L;

        try
        {
            using var reader = new StreamReader(
                source,
                StrictUtf8,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 8192,
                leaveOpen: true);

            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                lineNumber++;
                InspectText(line, ref suspiciousControlCount);
                if (string.IsNullOrWhiteSpace(line))
                {
                    AddParagraph(blocks, paragraph, paragraphStart, lineNumber - 1);
                    paragraphStart = 0;
                    continue;
                }

                if (paragraphStart == 0)
                {
                    paragraphStart = lineNumber;
                }

                paragraph.Add(line.Trim());
            }

            AddParagraph(blocks, paragraph, paragraphStart, lineNumber);
            RejectBinaryLikeText(suspiciousControlCount);
        }
        catch (DecoderFallbackException exception)
        {
            throw new DocumentParseException("The source is not valid BOM-identified Unicode or UTF-8 text.", exception);
        }

        return new ParsedDocumentResult(Descriptor, blocks, new Dictionary<string, string>());
    }

    internal static void InspectText(string text, ref long suspiciousControlCount)
    {
        foreach (var character in text)
        {
            if (character == '\0')
            {
                throw new DocumentParseException("The source appears to contain binary data.");
            }

            if (character == '\uFFFD')
            {
                throw new DocumentParseException("The source contains invalid encoded text.");
            }

            if (char.IsControl(character) && character is not ('\t' or '\r' or '\n'))
            {
                suspiciousControlCount++;
            }
        }
    }

    internal static void RejectBinaryLikeText(long suspiciousControlCount)
    {
        if (suspiciousControlCount > 0)
        {
            throw new DocumentParseException("The source appears to contain binary data.");
        }
    }

    private static void AddParagraph(
        List<ParsedBlock> blocks,
        List<string> paragraph,
        int startLine,
        int endLine)
    {
        if (paragraph.Count == 0)
        {
            return;
        }

        var text = string.Join('\n', paragraph);
        blocks.Add(new ParsedBlock(
            blocks.Count,
            ParsedBlockKind.PlainText,
            text,
            new TextSourceLocator(startLine, endLine),
            []));
        paragraph.Clear();
    }
}
