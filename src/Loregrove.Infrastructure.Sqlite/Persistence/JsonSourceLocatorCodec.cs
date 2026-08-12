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
                case PagedRegionSourceLocator paged when paged.SchemaVersion == 1:
                    writer.WriteNumber("pageNumber", paged.PageNumber);
                    writer.WriteString("itemReference", paged.ItemReference);
                    writer.WriteNumber("documentOrdinal", paged.DocumentOrdinal);
                    WriteBoundingBox(writer, paged.BoundingBox);
                    if (paged.CharacterSpan is { } span)
                    {
                        writer.WritePropertyName("characterSpan");
                        writer.WriteStartObject();
                        writer.WriteNumber("start", span.Start);
                        writer.WriteNumber("end", span.End);
                        writer.WriteEndObject();
                    }

                    WriteOptionalDouble(writer, "pageWidth", paged.PageWidth);
                    WriteOptionalDouble(writer, "pageHeight", paged.PageHeight);
                    break;
                case StructuredDocumentSourceLocator structured when structured.SchemaVersion == 1:
                    writer.WriteString("itemReference", structured.ItemReference);
                    writer.WriteNumber("documentOrdinal", structured.DocumentOrdinal);
                    WriteStringArray(writer, "headingPath", structured.HeadingPath);
                    WriteOptionalInt32(writer, "pageNumber", structured.PageNumber);
                    WriteBoundingBox(writer, structured.BoundingBox);
                    break;
                case PresentationSourceLocator presentation when presentation.SchemaVersion == 1:
                    writer.WriteNumber("slideNumber", presentation.SlideNumber);
                    writer.WriteString("itemReference", presentation.ItemReference);
                    writer.WriteNumber("slideOrdinal", presentation.SlideOrdinal);
                    if (presentation.SlideTitle is not null)
                    {
                        writer.WriteString("slideTitle", presentation.SlideTitle);
                    }

                    WriteBoundingBox(writer, presentation.BoundingBox);
                    break;
                case ImageRegionSourceLocator image when image.SchemaVersion == 1:
                    writer.WriteString("itemReference", image.ItemReference);
                    writer.WriteNumber("regionOrdinal", image.RegionOrdinal);
                    WriteBoundingBox(writer, image.BoundingBox);
                    WriteOptionalInt32(writer, "imageWidth", image.ImageWidth);
                    WriteOptionalInt32(writer, "imageHeight", image.ImageHeight);
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
        if (schemaVersion != 1)
        {
            throw new InvalidDataException("The source locator schema is not supported.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        try
        {
            using var document = JsonDocument.Parse(value);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("The source locator payload must be an object.");
            }

            return kind switch
            {
                SourceLocatorKind.Text => ReadText(root),
                SourceLocatorKind.Markdown => ReadMarkdown(root),
                SourceLocatorKind.PagedRegion => ReadPagedRegion(root),
                SourceLocatorKind.StructuredDocument => ReadStructuredDocument(root),
                SourceLocatorKind.Presentation => ReadPresentation(root),
                SourceLocatorKind.ImageRegion => ReadImageRegion(root),
                SourceLocatorKind.Spreadsheet => ReadSpreadsheet(root),
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
        EnsureOnlyProperties(root, "pageNumber", "itemReference", "documentOrdinal", "boundingBox", "characterSpan", "pageWidth", "pageHeight");
        return new PagedRegionSourceLocator(
            RequiredInt32(root, "pageNumber"),
            RequiredString(root, "itemReference"),
            RequiredInt32(root, "documentOrdinal"),
            ReadBoundingBox(root),
            ReadCharacterSpan(root),
            OptionalDouble(root, "pageWidth"),
            OptionalDouble(root, "pageHeight"));
    }

    private static StructuredDocumentSourceLocator ReadStructuredDocument(JsonElement root)
    {
        EnsureOnlyProperties(root, "itemReference", "documentOrdinal", "headingPath", "pageNumber", "boundingBox");
        return new StructuredDocumentSourceLocator(
            RequiredString(root, "itemReference"),
            RequiredInt32(root, "documentOrdinal"),
            RequiredStringArray(root, "headingPath"),
            OptionalInt32(root, "pageNumber"),
            ReadBoundingBox(root));
    }

    private static PresentationSourceLocator ReadPresentation(JsonElement root)
    {
        EnsureOnlyProperties(root, "slideNumber", "itemReference", "slideOrdinal", "slideTitle", "boundingBox");
        return new PresentationSourceLocator(
            RequiredInt32(root, "slideNumber"),
            RequiredString(root, "itemReference"),
            RequiredInt32(root, "slideOrdinal"),
            OptionalString(root, "slideTitle"),
            ReadBoundingBox(root));
    }

    private static ImageRegionSourceLocator ReadImageRegion(JsonElement root)
    {
        EnsureOnlyProperties(root, "itemReference", "regionOrdinal", "boundingBox", "imageWidth", "imageHeight");
        return new ImageRegionSourceLocator(
            RequiredString(root, "itemReference"),
            RequiredInt32(root, "regionOrdinal"),
            ReadBoundingBox(root),
            OptionalInt32(root, "imageWidth"),
            OptionalInt32(root, "imageHeight"));
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
