using System.Text;
using System.Text.Json;
using Loregrove.Application.Parsing;
using Loregrove.Domain.Sources;

namespace Loregrove.Infrastructure.Sqlite.Persistence;

public sealed class JsonSourceLocatorCodec : ISourceLocatorCodec
{
    public string Serialize(SourceLocator locator)
    {
        ArgumentNullException.ThrowIfNull(locator);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            switch (locator)
            {
                case TextSourceLocator text when text.SchemaVersion == 1:
                    writer.WriteNumber("startLine", text.StartLine);
                    writer.WriteNumber("endLine", text.EndLine);
                    if (text.StartCharacter is { } startCharacter)
                    {
                        writer.WriteNumber("startCharacter", startCharacter);
                    }

                    if (text.EndCharacter is { } endCharacter)
                    {
                        writer.WriteNumber("endCharacter", endCharacter);
                    }

                    break;
                case MarkdownSourceLocator markdown when markdown.SchemaVersion == 1:
                    writer.WriteNumber("startLine", markdown.StartLine);
                    writer.WriteNumber("endLine", markdown.EndLine);
                    writer.WriteNumber("blockOrdinal", markdown.BlockOrdinal);
                    writer.WritePropertyName("headingPath");
                    writer.WriteStartArray();
                    foreach (var heading in markdown.HeadingPath)
                    {
                        writer.WriteStringValue(heading);
                    }

                    writer.WriteEndArray();
                    break;
                case PagedRegionSourceLocator paged when paged.SchemaVersion == 2:
                    writer.WriteString("itemReference", paged.ItemReference);
                    writer.WriteNumber("documentOrdinal", paged.DocumentOrdinal);
                    WriteRegions(writer, paged.Regions);
                    break;
                case StructuredDocumentSourceLocator structured when structured.SchemaVersion == 2:
                    writer.WriteString("itemReference", structured.ItemReference);
                    writer.WriteNumber("documentOrdinal", structured.DocumentOrdinal);
                    WriteStringArray(writer, "headingPath", structured.HeadingPath);
                    WriteRegions(writer, structured.Regions);
                    break;
                case PresentationSourceLocator presentation when presentation.SchemaVersion == 2:
                    WriteOptionalInt32(writer, "slideNumber", presentation.SlideNumber);
                    writer.WriteString("itemReference", presentation.ItemReference);
                    writer.WriteNumber("slideOrdinal", presentation.SlideOrdinal);
                    if (presentation.SlideTitle is not null)
                    {
                        writer.WriteString("slideTitle", presentation.SlideTitle);
                    }

                    WriteRegions(writer, presentation.Regions);
                    break;
                case ImageRegionSourceLocator image when image.SchemaVersion == 2:
                    writer.WriteString("itemReference", image.ItemReference);
                    writer.WriteNumber("regionOrdinal", image.RegionOrdinal);
                    WriteOptionalInt32(writer, "imageWidth", image.ImageWidth);
                    WriteOptionalInt32(writer, "imageHeight", image.ImageHeight);
                    WriteRegions(writer, image.Regions);
                    break;
                case SpreadsheetSourceLocator spreadsheet when spreadsheet.SchemaVersion == 1:
                    writer.WriteString("sheetName", spreadsheet.SheetName);
                    writer.WriteNumber("sheetIndex", spreadsheet.SheetIndex);
                    writer.WriteString("range", spreadsheet.Range);
                    if (spreadsheet.TableName is not null)
                    {
                        writer.WriteString("tableName", spreadsheet.TableName);
                    }

                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(locator), "Unknown source locator type or schema.");
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public SourceLocator Deserialize(SourceLocatorKind kind, int schemaVersion, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        try
        {
            using var document = JsonDocument.Parse(value);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("The source locator payload must be an object.");
            }

            return (kind, schemaVersion) switch
            {
                (SourceLocatorKind.Text, 1) => ReadText(root),
                (SourceLocatorKind.Markdown, 1) => ReadMarkdown(root),
                (SourceLocatorKind.PagedRegion, 2) => ReadPagedRegion(root),
                (SourceLocatorKind.StructuredDocument, 2) => ReadStructuredDocument(root),
                (SourceLocatorKind.Presentation, 2) => ReadPresentation(root),
                (SourceLocatorKind.ImageRegion, 2) => ReadImageRegion(root),
                (SourceLocatorKind.Spreadsheet, 1) => ReadSpreadsheet(root),
                _ => throw new InvalidDataException("The source locator kind is not supported."),
            };
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The source locator payload is invalid.", exception);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("The source locator payload is invalid.", exception);
        }
    }

    private static TextSourceLocator ReadText(JsonElement root)
    {
        EnsureOnlyProperties(root, "startLine", "endLine", "startCharacter", "endCharacter");
        return new TextSourceLocator(
            RequiredInt32(root, "startLine"),
            RequiredInt32(root, "endLine"),
            OptionalInt32(root, "startCharacter"),
            OptionalInt32(root, "endCharacter"));
    }

    private static MarkdownSourceLocator ReadMarkdown(JsonElement root)
    {
        EnsureOnlyProperties(root, "startLine", "endLine", "blockOrdinal", "headingPath");
        if (!root.TryGetProperty("headingPath", out var path) || path.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The Markdown locator heading path is required.");
        }

        var headings = path.EnumerateArray().Select(item => item.GetString()
            ?? throw new InvalidDataException("Markdown heading path values must be strings.")).ToArray();
        return new MarkdownSourceLocator(
            RequiredInt32(root, "startLine"),
            RequiredInt32(root, "endLine"),
            RequiredInt32(root, "blockOrdinal"),
            headings);
    }

    private static PagedRegionSourceLocator ReadPagedRegion(JsonElement root)
    {
        EnsureOnlyProperties(root, "itemReference", "documentOrdinal", "regions");
        return new PagedRegionSourceLocator(
            RequiredString(root, "itemReference"),
            RequiredInt32(root, "documentOrdinal"),
            ReadRegions(root));
    }

    private static StructuredDocumentSourceLocator ReadStructuredDocument(JsonElement root)
    {
        EnsureOnlyProperties(root, "itemReference", "documentOrdinal", "headingPath", "regions");
        return new StructuredDocumentSourceLocator(
            RequiredString(root, "itemReference"),
            RequiredInt32(root, "documentOrdinal"),
            RequiredStringArray(root, "headingPath"),
            regions: ReadRegions(root));
    }

    private static PresentationSourceLocator ReadPresentation(JsonElement root)
    {
        EnsureOnlyProperties(root, "slideNumber", "itemReference", "slideOrdinal", "slideTitle", "regions");
        return new PresentationSourceLocator(
            OptionalInt32(root, "slideNumber"),
            RequiredString(root, "itemReference"),
            RequiredInt32(root, "slideOrdinal"),
            OptionalString(root, "slideTitle"),
            regions: ReadRegions(root));
    }

    private static ImageRegionSourceLocator ReadImageRegion(JsonElement root)
    {
        EnsureOnlyProperties(root, "itemReference", "regionOrdinal", "imageWidth", "imageHeight", "regions");
        return new ImageRegionSourceLocator(
            RequiredString(root, "itemReference"),
            RequiredInt32(root, "regionOrdinal"),
            boundingBox: null,
            imageWidth: OptionalInt32(root, "imageWidth"),
            imageHeight: OptionalInt32(root, "imageHeight"),
            regions: ReadRegions(root));
    }

    private static SpreadsheetSourceLocator ReadSpreadsheet(JsonElement root)
    {
        EnsureOnlyProperties(root, "sheetName", "sheetIndex", "range", "tableName");
        return new SpreadsheetSourceLocator(
            RequiredString(root, "sheetName"),
            RequiredInt32(root, "sheetIndex"),
            RequiredString(root, "range"),
            OptionalString(root, "tableName"));
    }

    private static int RequiredInt32(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || !value.TryGetInt32(out var result))
        {
            throw new InvalidDataException($"The source locator property '{name}' is required.");
        }

        return result;
    }

    private static int? OptionalInt32(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
            ? value.TryGetInt32(out var result)
                ? result
                : throw new InvalidDataException($"The source locator property '{name}' must be an integer.")
            : null;

    private static string RequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"The source locator property '{name}' is required.");
        }

        return value.GetString()!;
    }

    private static string? OptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : throw new InvalidDataException($"The source locator property '{name}' must be a string.")
            : null;

    private static double? OptionalDouble(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
            ? value.TryGetDouble(out var result) && double.IsFinite(result)
                ? result
                : throw new InvalidDataException($"The source locator property '{name}' must be a finite number.")
            : null;

    private static string[] RequiredStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"The source locator property '{name}' is required.");
        }

        return value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String
            ? item.GetString()!
            : throw new InvalidDataException($"The source locator property '{name}' must contain strings.")).ToArray();
    }

    private static SourceBoundingBox? ReadBoundingBox(JsonElement root)
    {
        if (!root.TryGetProperty("boundingBox", out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The bounding box must be an object.");
        }

        EnsureOnlyProperties(value, "left", "top", "right", "bottom", "origin");
        var origin = Enum.TryParse<SourceCoordinateOrigin>(RequiredString(value, "origin"), ignoreCase: false, out var parsedOrigin)
            ? parsedOrigin
            : throw new InvalidDataException("The bounding-box origin is invalid.");
        return new SourceBoundingBox(
            RequiredDouble(value, "left"),
            RequiredDouble(value, "top"),
            RequiredDouble(value, "right"),
            RequiredDouble(value, "bottom"),
            origin);
    }

    private static SourceCharacterSpan? ReadCharacterSpan(JsonElement root)
    {
        if (!root.TryGetProperty("characterSpan", out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The character span must be an object.");
        }

        EnsureOnlyProperties(value, "start", "end");
        return new SourceCharacterSpan(RequiredInt32(value, "start"), RequiredInt32(value, "end"));
    }

    private static SourceProvenanceRegion[] ReadRegions(JsonElement root)
    {
        if (!root.TryGetProperty("regions", out var value) || value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The source locator regions are required.");
        }

        return value.EnumerateArray().Select(region =>
        {
            if (region.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("A source locator region must be an object.");
            }

            EnsureOnlyProperties(region, "pageNumber", "boundingBox", "characterSpan", "pageWidth", "pageHeight");
            return new SourceProvenanceRegion(
                RequiredInt32(region, "pageNumber"),
                ReadBoundingBox(region),
                ReadCharacterSpan(region),
                OptionalDouble(region, "pageWidth"),
                OptionalDouble(region, "pageHeight"));
        }).ToArray();
    }

    private static double RequiredDouble(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetDouble(out var result) && double.IsFinite(result)
            ? result
            : throw new InvalidDataException($"The source locator property '{name}' must be a finite number.");

    private static void WriteStringArray(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
    {
        writer.WritePropertyName(name);
        writer.WriteStartArray();
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static void WriteBoundingBox(Utf8JsonWriter writer, SourceBoundingBox? box)
    {
        if (box is null)
        {
            return;
        }

        writer.WritePropertyName("boundingBox");
        writer.WriteStartObject();
        writer.WriteNumber("left", box.Left);
        writer.WriteNumber("top", box.Top);
        writer.WriteNumber("right", box.Right);
        writer.WriteNumber("bottom", box.Bottom);
        writer.WriteString("origin", box.Origin.ToString());
        writer.WriteEndObject();
    }

    private static void WriteRegions(Utf8JsonWriter writer, IReadOnlyList<SourceProvenanceRegion> regions)
    {
        writer.WritePropertyName("regions");
        writer.WriteStartArray();
        foreach (var region in regions)
        {
            writer.WriteStartObject();
            writer.WriteNumber("pageNumber", region.PageNumber);
            WriteBoundingBox(writer, region.BoundingBox);
            if (region.CharacterSpan is { } span)
            {
                writer.WritePropertyName("characterSpan");
                writer.WriteStartObject();
                writer.WriteNumber("start", span.Start);
                writer.WriteNumber("end", span.End);
                writer.WriteEndObject();
            }

            WriteOptionalDouble(writer, "pageWidth", region.PageWidth);
            WriteOptionalDouble(writer, "pageHeight", region.PageHeight);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteOptionalInt32(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is { } actual)
        {
            writer.WriteNumber(name, actual);
        }
    }

    private static void WriteOptionalDouble(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is { } actual)
        {
            writer.WriteNumber(name, actual);
        }
    }

    private static void EnsureOnlyProperties(JsonElement root, params string[] expected)
    {
        var allowed = expected.ToHashSet(StringComparer.Ordinal);
        if (root.EnumerateObject().Any(property => !allowed.Contains(property.Name)))
        {
            throw new InvalidDataException("The source locator payload contains unknown properties.");
        }
    }
}
