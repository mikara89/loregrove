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

public enum SourceCoordinateOrigin
{
    TopLeft = 0,
    BottomLeft = 1,
}

public sealed record SourceBoundingBox
{
    public SourceBoundingBox(double left, double top, double right, double bottom, SourceCoordinateOrigin origin)
    {
        if (!double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(right) || !double.IsFinite(bottom) ||
            right < left || (origin == SourceCoordinateOrigin.TopLeft ? bottom < top : top < bottom))
        {
            throw new ArgumentException("Source bounding-box coordinates are invalid.");
        }

        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
        Origin = origin;
    }

    public double Left { get; }
    public double Top { get; }
    public double Right { get; }
    public double Bottom { get; }
    public SourceCoordinateOrigin Origin { get; }
}

public sealed record SourceCharacterSpan
{
    public SourceCharacterSpan(int start, int end)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        if (end < start)
        {
            throw new ArgumentException("The character span end cannot precede its start.", nameof(end));
        }

        Start = start;
        End = end;
    }

    public int Start { get; }
    public int End { get; }
}

public sealed record SourceProvenanceRegion
{
    public SourceProvenanceRegion(
        int pageNumber,
        SourceBoundingBox? boundingBox = null,
        SourceCharacterSpan? characterSpan = null,
        double? pageWidth = null,
        double? pageHeight = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        ValidatePositiveFinite(pageWidth, nameof(pageWidth));
        ValidatePositiveFinite(pageHeight, nameof(pageHeight));
        PageNumber = pageNumber;
        BoundingBox = boundingBox;
        CharacterSpan = characterSpan;
        PageWidth = pageWidth;
        PageHeight = pageHeight;
    }

    public int PageNumber { get; }
    public SourceBoundingBox? BoundingBox { get; }
    public SourceCharacterSpan? CharacterSpan { get; }
    public double? PageWidth { get; }
    public double? PageHeight { get; }

    private static void ValidatePositiveFinite(double? value, string name)
    {
        if (value is { } actual && (!double.IsFinite(actual) || actual <= 0))
        {
            throw new ArgumentOutOfRangeException(name, "Page dimensions must be finite and positive.");
        }
    }
}

public sealed record PagedRegionSourceLocator : SourceLocator
{
    public PagedRegionSourceLocator(
        int pageNumber,
        string itemReference,
        int documentOrdinal,
        SourceBoundingBox? boundingBox = null,
        SourceCharacterSpan? characterSpan = null,
        double? pageWidth = null,
        double? pageHeight = null)
        : this(
            itemReference,
            documentOrdinal,
            [new SourceProvenanceRegion(pageNumber, boundingBox, characterSpan, pageWidth, pageHeight)])
    {
    }

    public PagedRegionSourceLocator(
        string itemReference,
        int documentOrdinal,
        IReadOnlyList<SourceProvenanceRegion> regions)
        : base(SourceLocatorKind.PagedRegion, 2)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemReference);
        ArgumentOutOfRangeException.ThrowIfNegative(documentOrdinal);
        ArgumentNullException.ThrowIfNull(regions);
        if (regions.Count == 0)
        {
            throw new ArgumentException("At least one paged provenance region is required.", nameof(regions));
        }

        if (regions.Any(region => region is null))
        {
            throw new ArgumentException("Paged provenance regions cannot contain null values.", nameof(regions));
        }

        ItemReference = itemReference;
        DocumentOrdinal = documentOrdinal;
        Regions = regions.ToArray();
    }

    public int PageNumber => Regions[0].PageNumber;
    public string ItemReference { get; }
    public int DocumentOrdinal { get; }
    public IReadOnlyList<SourceProvenanceRegion> Regions { get; }
    public SourceBoundingBox? BoundingBox => Regions[0].BoundingBox;
    public SourceCharacterSpan? CharacterSpan => Regions[0].CharacterSpan;
    public double? PageWidth => Regions[0].PageWidth;
    public double? PageHeight => Regions[0].PageHeight;
}

public sealed record StructuredDocumentSourceLocator : SourceLocator
{
    public StructuredDocumentSourceLocator(
        string itemReference,
        int documentOrdinal,
        IReadOnlyList<string> headingPath,
        int? pageNumber = null,
        SourceBoundingBox? boundingBox = null,
        IReadOnlyList<SourceProvenanceRegion>? regions = null)
        : base(SourceLocatorKind.StructuredDocument, 2)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemReference);
        ArgumentOutOfRangeException.ThrowIfNegative(documentOrdinal);
        if (pageNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber));
        }

        ArgumentNullException.ThrowIfNull(headingPath);
        if (regions?.Any(region => region is null) == true)
        {
            throw new ArgumentException("Structured provenance regions cannot contain null values.", nameof(regions));
        }

        ItemReference = itemReference;
        DocumentOrdinal = documentOrdinal;
        HeadingPath = headingPath.ToArray();
        Regions = regions?.ToArray() ?? (pageNumber is { } page
            ? [new SourceProvenanceRegion(page, boundingBox)]
            : []);
    }

    public string ItemReference { get; }
    public int DocumentOrdinal { get; }
    public IReadOnlyList<string> HeadingPath { get; }
    public IReadOnlyList<SourceProvenanceRegion> Regions { get; }
    public int? PageNumber => Regions.Count == 0 ? null : Regions[0].PageNumber;
    public SourceBoundingBox? BoundingBox => Regions.Count == 0 ? null : Regions[0].BoundingBox;
}

public sealed record PresentationSourceLocator : SourceLocator
{
    public PresentationSourceLocator(
        int? slideNumber,
        string itemReference,
        int slideOrdinal,
        string? slideTitle = null,
        SourceBoundingBox? boundingBox = null,
        IReadOnlyList<SourceProvenanceRegion>? regions = null)
        : base(SourceLocatorKind.Presentation, 2)
    {
        if (slideNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(slideNumber));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(itemReference);
        ArgumentOutOfRangeException.ThrowIfNegative(slideOrdinal);
        if (regions?.Any(region => region is null) == true)
        {
            throw new ArgumentException("Presentation provenance regions cannot contain null values.", nameof(regions));
        }

        SlideNumber = slideNumber;
        ItemReference = itemReference;
        SlideOrdinal = slideOrdinal;
        SlideTitle = slideTitle;
        Regions = regions?.ToArray() ?? (slideNumber is { } slide && boundingBox is not null
            ? [new SourceProvenanceRegion(slide, boundingBox)]
            : []);
    }

    public int? SlideNumber { get; }
    public string ItemReference { get; }
    public int SlideOrdinal { get; }
    public string? SlideTitle { get; }
    public IReadOnlyList<SourceProvenanceRegion> Regions { get; }
    public SourceBoundingBox? BoundingBox => Regions.Count == 0 ? null : Regions[0].BoundingBox;
}

public sealed record ImageRegionSourceLocator : SourceLocator
{
    public ImageRegionSourceLocator(
        string itemReference,
        int regionOrdinal,
        SourceBoundingBox? boundingBox = null,
        int? imageWidth = null,
        int? imageHeight = null,
        IReadOnlyList<SourceProvenanceRegion>? regions = null)
        : base(SourceLocatorKind.ImageRegion, 2)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemReference);
        ArgumentOutOfRangeException.ThrowIfNegative(regionOrdinal);
        if (imageWidth < 1 || imageHeight < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(imageWidth));
        }

        if (regions?.Any(region => region is null) == true)
        {
            throw new ArgumentException("Image provenance regions cannot contain null values.", nameof(regions));
        }

        ItemReference = itemReference;
        RegionOrdinal = regionOrdinal;
        ImageWidth = imageWidth;
        ImageHeight = imageHeight;
        Regions = regions?.ToArray() ?? (boundingBox is not null
            ? [new SourceProvenanceRegion(1, boundingBox)]
            : []);
    }

    public string ItemReference { get; }
    public int RegionOrdinal { get; }
    public IReadOnlyList<SourceProvenanceRegion> Regions { get; }
    public SourceBoundingBox? BoundingBox => Regions.Count == 0 ? null : Regions[0].BoundingBox;
    public int? ImageWidth { get; }
    public int? ImageHeight { get; }
}

public sealed record SpreadsheetSourceLocator : SourceLocator
{
    public SpreadsheetSourceLocator(string sheetName, int sheetIndex, string range, string? tableName = null)
        : base(SourceLocatorKind.Spreadsheet, 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sheetName);
        ArgumentOutOfRangeException.ThrowIfNegative(sheetIndex);
        ArgumentException.ThrowIfNullOrWhiteSpace(range);
        SheetName = sheetName;
        SheetIndex = sheetIndex;
        Range = range;
        TableName = tableName;
    }

    public string SheetName { get; }
    public int SheetIndex { get; }
    public string Range { get; }
    public string? TableName { get; }
}

public sealed record ParsedBlock(
    int Ordinal,
    ParsedBlockKind Kind,
    string Text,
    SourceLocator Locator,
    IReadOnlyList<string> HeadingPath);

public enum ParsedRepresentationKind
{
    Markdown = 0,
    Json = 1,
}

public sealed record ParsedRepresentation(string Name, ParsedRepresentationKind Kind, string Content);

public sealed record ParsedDocumentResult(
    ParserDescriptor Parser,
    IReadOnlyList<ParsedBlock> Blocks,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyList<ParsedRepresentation>? Representations = null,
    ParsedArtifactCompleteness Completeness = ParsedArtifactCompleteness.Complete,
    int WarningCount = 0,
    string? SafeDiagnosticCode = null);

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

public enum ParserAvailabilityState
{
    Available = 0,
    Deferred = 1,
}

public enum ParserAvailabilityReason
{
    None = 0,
    DoclingDisabled = 1,
    DoclingPackMissing = 2,
    DoclingPackInvalid = 3,
    DoclingRuntimeUnsupported = 4,
    RemoteEndpointMissing = 5,
    RemoteConsentRequired = 6,
    RemoteCredentialUnavailable = 7,
    DoclingOneShotDeferred = 8,
    DoclingApiIncompatible = 9,
    RemoteEndpointInvalid = 10,
}

public sealed record ParserAvailability(ParserAvailabilityState State, ParserAvailabilityReason Reason)
{
    public static ParserAvailability Available { get; } = new(ParserAvailabilityState.Available, ParserAvailabilityReason.None);

    public static ParserAvailability Deferred(ParserAvailabilityReason reason) =>
        new(ParserAvailabilityState.Deferred, reason);
}

public interface IDocumentParserAvailability
{
    Task<ParserAvailability> GetAvailabilityAsync(
        ParseSourceDescriptor source,
        CancellationToken cancellationToken);
}

public interface IDocumentParserDescriptorProvider
{
    Task<ParserDescriptor> GetDescriptorAsync(
        ParseSourceDescriptor source,
        CancellationToken cancellationToken);
}

public sealed class DocumentParseException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public enum ParserInfrastructureFailureCode
{
    TransportFailure = 0,
    RuntimeFailure = 1,
    ApiIncompatible = 2,
    ResponseTooLarge = 3,
    ConversionTimedOut = 4,
}

public sealed class ParserInfrastructureException(
    ParserInfrastructureFailureCode code,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public ParserInfrastructureFailureCode Code { get; } = code;
}

public enum ParseSourceDisposition
{
    Parsed = 0,
    AlreadyParsed = 1,
    Unsupported = 2,
    Busy = 3,
    Failed = 4,
    Cancelled = 5,
    NotFound = 6,
    Deferred = 7,
    RetryableFailure = 8,
}

public sealed record ParseSourceResult(
    ParseSourceDisposition Disposition,
    ParsedArtifactId? ArtifactId = null,
    string? Message = null,
    ParserAvailabilityReason? DeferredReason = null,
    ParserInfrastructureFailureCode? InfrastructureFailureCode = null);
