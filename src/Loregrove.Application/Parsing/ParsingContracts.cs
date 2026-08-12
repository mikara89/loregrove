using System.Security.Cryptography;
using System.Text;
using Loregrove.Domain.Sources;

namespace Loregrove.Application.Parsing;

public sealed record ParserDescriptor(
    string Id,
    string Version,
    int OutputSchemaVersion,
    string ConfigurationFingerprint,
    string Fingerprint)
{
    public static ParserDescriptor Create(
        string id,
        string version,
        int outputSchemaVersion,
        string configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentOutOfRangeException.ThrowIfLessThan(outputSchemaVersion, 1);
        ArgumentNullException.ThrowIfNull(configuration);

        var configurationFingerprint = Sha256(configuration);
        var fingerprint = Sha256(string.Join('\n', id, version, outputSchemaVersion, configurationFingerprint));
        return new ParserDescriptor(id, version, outputSchemaVersion, configurationFingerprint, fingerprint);
    }

    internal static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed record ParseSourceDescriptor(
    SourceDocumentVersionId DocumentVersionId,
    string ContentHash,
    string OriginalFileName,
    string? MediaType);

public abstract record SourceLocator(SourceLocatorKind Kind, int SchemaVersion);

public sealed record TextSourceLocator : SourceLocator
{
    public TextSourceLocator(
        int startLine,
        int endLine,
        int? startCharacter = null,
        int? endCharacter = null)
        : base(SourceLocatorKind.Text, 1)
    {
        ValidateLines(startLine, endLine);
        if (startCharacter < 0 || endCharacter < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startCharacter), "Character offsets cannot be negative.");
        }

        if (startCharacter.HasValue && endCharacter.HasValue && startCharacter > endCharacter)
        {
            throw new ArgumentException("The end character cannot precede the start character.");
        }

        StartLine = startLine;
        EndLine = endLine;
        StartCharacter = startCharacter;
        EndCharacter = endCharacter;
    }

    public int StartLine { get; }

    public int EndLine { get; }

    public int? StartCharacter { get; }

    public int? EndCharacter { get; }

    private static void ValidateLines(int startLine, int endLine)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(startLine, 1);
        if (endLine < startLine)
        {
            throw new ArgumentException("The end line cannot precede the start line.", nameof(endLine));
        }
    }
}

public sealed record MarkdownSourceLocator : SourceLocator
{
    public MarkdownSourceLocator(
        int startLine,
        int endLine,
        int blockOrdinal,
        IReadOnlyList<string> headingPath)
        : base(SourceLocatorKind.Markdown, 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(startLine, 1);
        if (endLine < startLine)
        {
            throw new ArgumentException("The end line cannot precede the start line.", nameof(endLine));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(blockOrdinal);
        ArgumentNullException.ThrowIfNull(headingPath);
        StartLine = startLine;
        EndLine = endLine;
        BlockOrdinal = blockOrdinal;
        HeadingPath = headingPath.ToArray();
    }

    public int StartLine { get; }

    public int EndLine { get; }

    public int BlockOrdinal { get; }

    public IReadOnlyList<string> HeadingPath { get; }
}

public sealed record ParsedBlock(
    int Ordinal,
    ParsedBlockKind Kind,
    string Text,
    SourceLocator Locator,
    IReadOnlyList<string> HeadingPath);

public sealed record ParsedDocumentResult(
    ParserDescriptor Parser,
    IReadOnlyList<ParsedBlock> Blocks,
    IReadOnlyDictionary<string, string> Metadata);

public interface IDocumentParser
{
    ParserDescriptor Descriptor { get; }

    bool CanParse(ParseSourceDescriptor source);

    Task<ParsedDocumentResult> ParseAsync(
        Stream source,
        ParseSourceDescriptor descriptor,
        CancellationToken cancellationToken);
}

public interface IDocumentParserResolver
{
    IDocumentParser? Resolve(ParseSourceDescriptor source);
}

public sealed class DocumentParseException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public enum ParseSourceDisposition
{
    Parsed = 0,
    AlreadyParsed = 1,
    Unsupported = 2,
    Busy = 3,
    Failed = 4,
    Cancelled = 5,
    NotFound = 6,
}

public sealed record ParseSourceResult(
    ParseSourceDisposition Disposition,
    ParsedArtifactId? ArtifactId = null,
    string? Message = null);
