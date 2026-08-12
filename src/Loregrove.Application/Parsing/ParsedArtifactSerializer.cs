using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Loregrove.Domain.Sources;

namespace Loregrove.Application.Parsing;

public sealed record SerializedParsedArtifact(byte[] Bytes, string ContentHash);

public static class ParsedArtifactSerializer
{
    public static SerializedParsedArtifact Serialize(
        ParseSourceDescriptor source,
        ParsedDocumentResult result)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(result);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", result.Parser.OutputSchemaVersion);
            writer.WritePropertyName("parser");
            writer.WriteStartObject();
            writer.WriteString("id", result.Parser.Id);
            writer.WriteString("version", result.Parser.Version);
            writer.WriteString("configurationFingerprint", result.Parser.ConfigurationFingerprint);
            writer.WriteString("fingerprint", result.Parser.Fingerprint);
            writer.WriteEndObject();
            writer.WritePropertyName("source");
            writer.WriteStartObject();
            writer.WriteString("documentVersionId", source.DocumentVersionId.Value.ToString("D"));
            writer.WriteString("contentHash", source.ContentHash);
            writer.WriteEndObject();
            writer.WritePropertyName("metadata");
            writer.WriteStartObject();
            foreach (var metadata in result.Metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WriteString(metadata.Key, metadata.Value);
            }

            writer.WriteEndObject();
            if (result.Parser.OutputSchemaVersion >= 2 && result.Representations is { Count: > 0 })
            {
                writer.WritePropertyName("representations");
                writer.WriteStartObject();
                foreach (var representation in result.Representations.OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    ArgumentException.ThrowIfNullOrWhiteSpace(representation.Name);
                    writer.WritePropertyName(representation.Name);
                    if (representation.Kind == ParsedRepresentationKind.Markdown)
                    {
                        writer.WriteStringValue(representation.Content);
                    }
                    else
                    {
                        using var json = JsonDocument.Parse(representation.Content);
                        WriteCanonicalJson(writer, json.RootElement);
                    }
                }

                writer.WriteEndObject();
            }

            if (result.Parser.OutputSchemaVersion >= 2)
            {
                writer.WriteString("completeness", result.Completeness.ToString());
                writer.WriteNumber("warningCount", result.WarningCount);
                if (result.SafeDiagnosticCode is not null)
                {
                    writer.WriteString("safeDiagnosticCode", result.SafeDiagnosticCode);
                }
            }

            writer.WritePropertyName("blocks");
            writer.WriteStartArray();
            foreach (var block in result.Blocks.OrderBy(block => block.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteNumber("ordinal", block.Ordinal);
                writer.WriteString("kind", block.Kind.ToString());
                writer.WriteString("text", block.Text);
                writer.WriteString("textHash", HashText(block.Text));
                writer.WritePropertyName("headingPath");
                writer.WriteStartArray();
                foreach (var heading in block.HeadingPath)
                {
                    writer.WriteStringValue(heading);
                }

                writer.WriteEndArray();
                writer.WritePropertyName("locator");
                WriteLocator(writer, block.Locator);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var bytes = stream.ToArray();
        return new SerializedParsedArtifact(bytes, HashBytes(bytes));
    }

    public static string HashText(string text) => HashBytes(Encoding.UTF8.GetBytes(text));

    public static string HashBytes(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void WriteLocator(Utf8JsonWriter writer, SourceLocator locator)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", locator.Kind.ToString().ToLowerInvariant());
        writer.WriteNumber("schemaVersion", locator.SchemaVersion);
        switch (locator)
        {
            case TextSourceLocator text:
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
            case MarkdownSourceLocator markdown:
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
            case PagedRegionSourceLocator paged:
                writer.WriteString("itemReference", paged.ItemReference);
                writer.WriteNumber("documentOrdinal", paged.DocumentOrdinal);
                WriteRegions(writer, paged.Regions);
                break;
            case StructuredDocumentSourceLocator structured:
                writer.WriteString("itemReference", structured.ItemReference);
                writer.WriteNumber("documentOrdinal", structured.DocumentOrdinal);
                WriteHeadingPath(writer, structured.HeadingPath);
                WriteRegions(writer, structured.Regions);
                break;
            case PresentationSourceLocator presentation:
                if (presentation.SlideNumber is { } slideNumber)
                {
                    writer.WriteNumber("slideNumber", slideNumber);
                }

                writer.WriteString("itemReference", presentation.ItemReference);
                writer.WriteNumber("slideOrdinal", presentation.SlideOrdinal);
                if (presentation.SlideTitle is not null)
                {
                    writer.WriteString("slideTitle", presentation.SlideTitle);
                }

                WriteRegions(writer, presentation.Regions);
                break;
            case ImageRegionSourceLocator image:
                writer.WriteString("itemReference", image.ItemReference);
                writer.WriteNumber("regionOrdinal", image.RegionOrdinal);
                if (image.ImageWidth is { } imageWidth)
                {
                    writer.WriteNumber("imageWidth", imageWidth);
                }

                if (image.ImageHeight is { } imageHeight)
                {
                    writer.WriteNumber("imageHeight", imageHeight);
                }

                WriteRegions(writer, image.Regions);
                break;
            case SpreadsheetSourceLocator spreadsheet:
                writer.WriteString("sheetName", spreadsheet.SheetName);
                writer.WriteNumber("sheetIndex", spreadsheet.SheetIndex);
                writer.WriteString("range", spreadsheet.Range);
                if (spreadsheet.TableName is not null)
                {
                    writer.WriteString("tableName", spreadsheet.TableName);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(locator), "Unknown source locator type.");
        }

        writer.WriteEndObject();
    }

    private static void WriteHeadingPath(Utf8JsonWriter writer, IReadOnlyList<string> headingPath)
    {
        writer.WritePropertyName("headingPath");
        writer.WriteStartArray();
        foreach (var heading in headingPath)
        {
            writer.WriteStringValue(heading);
        }

        writer.WriteEndArray();
    }

    private static void WriteBoundingBox(Utf8JsonWriter writer, SourceBoundingBox? boundingBox)
    {
        if (boundingBox is null)
        {
            return;
        }

        writer.WritePropertyName("boundingBox");
        writer.WriteStartObject();
        writer.WriteNumber("left", boundingBox.Left);
        writer.WriteNumber("top", boundingBox.Top);
        writer.WriteNumber("right", boundingBox.Right);
        writer.WriteNumber("bottom", boundingBox.Bottom);
        writer.WriteString("origin", boundingBox.Origin.ToString());
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
            if (region.CharacterSpan is { } characterSpan)
            {
                writer.WritePropertyName("characterSpan");
                writer.WriteStartObject();
                writer.WriteNumber("start", characterSpan.Start);
                writer.WriteNumber("end", characterSpan.End);
                writer.WriteEndObject();
            }

            WriteOptionalNumber(writer, "pageWidth", region.PageWidth);
            WriteOptionalNumber(writer, "pageHeight", region.PageHeight);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteOptionalNumber(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
    }

    internal static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalJson(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                element.WriteTo(writer);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidDataException("Unsupported JSON token in parsed representation.");
        }
    }
}
