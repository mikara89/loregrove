using System.Globalization;
using System.Text;
using System.Text.Json;
using Loregrove.Application.Parsing;
using Loregrove.Domain.Sources;

namespace Loregrove.Infrastructure.Docling;

internal sealed record DoclingMappedDocument(
    IReadOnlyList<ParsedBlock> Blocks,
    string CanonicalStructuredJson);

internal sealed class DoclingSchemaException(string message, Exception? innerException = null)
    : Exception(message, innerException);

internal sealed class DoclingDocumentMapper
{
    private static readonly HashSet<string> VolatilePropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "created_at",
        "file_path",
        "host",
        "pid",
        "port",
        "processing_time",
        "source_path",
        "task_id",
        "task_position",
        "task_status",
        "timestamp",
        "timings",
        "updated_at",
    };

    internal static DoclingMappedDocument Map(string structuredJson, string inputFormat)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(structuredJson);
        using var document = JsonDocument.Parse(structuredJson, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 128,
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object)
        {
            throw new DoclingSchemaException("The structured Docling document root is invalid.");
        }

        var items = BuildItemIndex(root);
        var pageDimensions = ReadPageDimensions(root);
        var blocks = new List<ParsedBlock>();
        var active = new HashSet<string>(StringComparer.Ordinal);
        var headingPath = new List<string>();
        var slideSequence = 0;
        if (!body.TryGetProperty("children", out var bodyChildren) || bodyChildren.ValueKind != JsonValueKind.Array)
        {
            throw new DoclingSchemaException("The structured Docling body has no reading-order children.");
        }

        foreach (var child in bodyChildren.EnumerateArray())
        {
            Traverse(
                ReadReference(child),
                headingPath,
                items,
                pageDimensions,
                inputFormat,
                blocks,
                active,
                presentationContext: null,
                ref slideSequence);
        }

        if (blocks.Count == 0 && items.Values.Any(IsUsableContent))
        {
            throw new DoclingSchemaException("The Docling reading order did not expose usable evidence.");
        }

        return new DoclingMappedDocument(blocks, Canonicalize(root));
    }

    private static Dictionary<string, JsonElement> BuildItemIndex(JsonElement root)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var collectionName in new[] { "texts", "tables", "pictures", "key_value_items", "groups" })
        {
            if (!root.TryGetProperty(collectionName, out var collection))
            {
                continue;
            }

            if (collection.ValueKind != JsonValueKind.Array)
            {
                throw new DoclingSchemaException($"Docling collection '{collectionName}' is invalid.");
            }

            foreach (var item in collection.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("self_ref", out var selfRef) || selfRef.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(selfRef.GetString()) ||
                    !item.TryGetProperty("label", out var label) || label.ValueKind != JsonValueKind.String)
                {
                    throw new DoclingSchemaException("A Docling document item is missing its reference or label.");
                }

                if (!result.TryAdd(selfRef.GetString()!, item.Clone()))
                {
                    throw new DoclingSchemaException("The Docling document contains duplicate item references.");
                }
            }
        }

        return result;
    }

    private static Dictionary<int, (double Width, double Height)> ReadPageDimensions(JsonElement root)
    {
        var result = new Dictionary<int, (double Width, double Height)>();
        if (!root.TryGetProperty("pages", out var pages))
        {
            return result;
        }

        IEnumerable<JsonElement> values = pages.ValueKind switch
        {
            JsonValueKind.Array => pages.EnumerateArray(),
            JsonValueKind.Object => pages.EnumerateObject().Select(item => item.Value),
            _ => throw new DoclingSchemaException("The Docling pages collection is invalid."),
        };
        foreach (var page in values)
        {
            if (page.ValueKind != JsonValueKind.Object ||
                !TryInt32(page, "page_no", out var pageNumber) || pageNumber < 1 ||
                !page.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Object ||
                !TryFiniteDouble(size, "width", out var width) || width <= 0 ||
                !TryFiniteDouble(size, "height", out var height) || height <= 0)
            {
                throw new DoclingSchemaException("A Docling page has invalid dimensions or identity.");
            }

            result[pageNumber] = (width, height);
        }

        return result;
    }

    private static void Traverse(
        string reference,
        List<string> headingPath,
        IReadOnlyDictionary<string, JsonElement> items,
        IReadOnlyDictionary<int, (double Width, double Height)> pageDimensions,
        string inputFormat,
        List<ParsedBlock> blocks,
        HashSet<string> active,
        PresentationContext? presentationContext,
        ref int slideSequence)
    {
        if (!items.TryGetValue(reference, out var item))
        {
            throw new DoclingSchemaException("The Docling reading order references an unknown item.");
        }

        if (!active.Add(reference))
        {
            throw new DoclingSchemaException("The Docling reading order contains a cycle.");
        }

        try
        {
            var label = RequiredString(item, "label");
            var text = ReadItemText(item, label);
            var isHeading = label is "title" or "section_header" or "field_heading";
            var childPath = headingPath;
            if (inputFormat == "pptx" && label == "slide")
            {
                var slideTitle = FindSlideTitle(item, items);
                _ = ReadProvenance(item, pageDimensions);
                var slideNumber = ++slideSequence;
                presentationContext = new PresentationContext(slideNumber, slideTitle);
                if (slideTitle is not null)
                {
                    childPath = [slideTitle];
                }
            }

            if (!string.IsNullOrWhiteSpace(text) && !IsFurniture(label, item))
            {
                var normalized = NormalizeText(text);
                if (normalized.Length > 0)
                {
                    if (isHeading)
                    {
                        var level = TryInt32(item, "level", out var explicitLevel) && explicitLevel > 0
                            ? explicitLevel
                            : 1;
                        while (childPath.Count >= level)
                        {
                            childPath.RemoveAt(childPath.Count - 1);
                        }

                        while (childPath.Count < level - 1)
                        {
                            childPath.Add(string.Empty);
                        }

                        childPath.Add(normalized);
                    }

                    var blockPath = childPath.Where(value => value.Length > 0).ToArray();
                    var ordinal = blocks.Count;
                    blocks.Add(new ParsedBlock(
                        ordinal,
                        MapKind(label),
                        normalized,
                        CreateLocator(item, inputFormat, ordinal, blockPath, pageDimensions, presentationContext),
                        blockPath));
                }
            }

            if (item.TryGetProperty("children", out var children))
            {
                if (children.ValueKind != JsonValueKind.Array)
                {
                    throw new DoclingSchemaException("A Docling item has invalid children.");
                }

                foreach (var child in children.EnumerateArray())
                {
                    Traverse(
                        ReadReference(child),
                        childPath,
                        items,
                        pageDimensions,
                        inputFormat,
                        blocks,
                        active,
                        presentationContext,
                        ref slideSequence);
                }
            }
        }
        finally
        {
            active.Remove(reference);
        }
    }

    private static string? FindSlideTitle(JsonElement slide, IReadOnlyDictionary<string, JsonElement> items)
    {
        if (!slide.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var child in children.EnumerateArray())
        {
            var reference = ReadReference(child);
            if (items.TryGetValue(reference, out var candidate) &&
                RequiredString(candidate, "label") == "title" &&
                ReadItemText(candidate, "title") is { } title)
            {
                var normalized = NormalizeText(title);
                return normalized.Length == 0 ? null : normalized;
            }
        }

        return null;
    }

    private static SourceLocator CreateLocator(
        JsonElement item,
        string inputFormat,
        int ordinal,
        string[] headingPath,
        IReadOnlyDictionary<int, (double Width, double Height)> pageDimensions,
        PresentationContext? presentationContext)
    {
        var reference = RequiredString(item, "self_ref");
        var provenance = ReadProvenance(item, pageDimensions);
        var first = provenance.Count == 0 ? null : provenance[0];
        return inputFormat switch
        {
            "pdf" when provenance.Count > 0 => new PagedRegionSourceLocator(
                reference,
                ordinal,
                provenance),
            "pptx" => new PresentationSourceLocator(
                presentationContext?.SlideNumber ?? first?.PageNumber,
                reference,
                ordinal,
                presentationContext?.SlideTitle,
                regions: provenance),
            "png" or "jpeg" or "tiff" or "bmp" or "webp" => new ImageRegionSourceLocator(
                reference,
                ordinal,
                imageWidth: ToPixelDimension(first?.PageWidth ?? 0),
                imageHeight: ToPixelDimension(first?.PageHeight ?? 0),
                regions: provenance),
            _ => new StructuredDocumentSourceLocator(
                reference,
                ordinal,
                headingPath,
                regions: provenance),
        };
    }

    private static int? ToPixelDimension(double value)
    {
        if (value <= 0)
        {
            return null;
        }

        var rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        if (rounded < 1 || rounded > int.MaxValue)
        {
            throw new DoclingSchemaException("Docling returned an invalid image dimension.");
        }

        return checked((int)rounded);
    }

    private static List<SourceProvenanceRegion> ReadProvenance(
        JsonElement item,
        IReadOnlyDictionary<int, (double Width, double Height)> pageDimensions)
    {
        if (!item.TryGetProperty("prov", out var provenance))
        {
            return [];
        }

        if (provenance.ValueKind != JsonValueKind.Array)
        {
            throw new DoclingSchemaException("Docling provenance must be an array.");
        }

        var regions = new List<SourceProvenanceRegion>(provenance.GetArrayLength());
        foreach (var value in provenance.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Object ||
                !TryInt32(value, "page_no", out var page) || page < 1)
            {
                throw new DoclingSchemaException("A Docling provenance region has no valid page number.");
            }

            SourceBoundingBox? box = null;
            if (value.TryGetProperty("bbox", out var bbox))
            {
                if (bbox.ValueKind != JsonValueKind.Object ||
                    !TryFiniteDouble(bbox, "l", out var left) ||
                    !TryFiniteDouble(bbox, "t", out var top) ||
                    !TryFiniteDouble(bbox, "r", out var right) ||
                    !TryFiniteDouble(bbox, "b", out var bottom))
                {
                    throw new DoclingSchemaException("Docling returned an invalid provenance bounding box.");
                }

                var origin = SourceCoordinateOrigin.TopLeft;
                if (bbox.TryGetProperty("coord_origin", out var originValue))
                {
                    origin = originValue.ValueKind == JsonValueKind.String
                        ? originValue.GetString()?.ToUpperInvariant() switch
                        {
                            "TOPLEFT" => SourceCoordinateOrigin.TopLeft,
                            "BOTTOMLEFT" => SourceCoordinateOrigin.BottomLeft,
                            _ => throw new DoclingSchemaException("Docling returned an unknown coordinate origin."),
                        }
                        : throw new DoclingSchemaException("Docling returned an invalid coordinate origin.");
                }

                try
                {
                    box = new SourceBoundingBox(left, top, right, bottom, origin);
                }
                catch (ArgumentException exception)
                {
                    throw new DoclingSchemaException("Docling returned invalid provenance geometry.", exception);
                }
            }

            SourceCharacterSpan? span = null;
            if (value.TryGetProperty("charspan", out var charspan))
            {
                var start = 0;
                var end = 0;
                var valid = charspan.ValueKind == JsonValueKind.Array && charspan.GetArrayLength() == 2 &&
                    charspan[0].TryGetInt32(out start) && charspan[1].TryGetInt32(out end);
                valid = valid || (charspan.ValueKind == JsonValueKind.Object &&
                    TryInt32(charspan, "start", out start) && TryInt32(charspan, "end", out end));
                if (!valid)
                {
                    throw new DoclingSchemaException("Docling returned an invalid character-span shape.");
                }

                try
                {
                    span = new SourceCharacterSpan(start, end);
                }
                catch (ArgumentException exception)
                {
                    throw new DoclingSchemaException("Docling returned an invalid character span.", exception);
                }
            }

            pageDimensions.TryGetValue(page, out var dimensions);
            regions.Add(new SourceProvenanceRegion(
                page,
                box,
                span,
                dimensions.Width > 0 ? dimensions.Width : null,
                dimensions.Height > 0 ? dimensions.Height : null));
        }

        return regions;
    }

    private static string? ReadItemText(JsonElement item, string label)
    {
        if (item.TryGetProperty("text", out var text))
        {
            if (text.ValueKind != JsonValueKind.String)
            {
                throw new DoclingSchemaException("A Docling item has an invalid text value.");
            }

            return text.GetString();
        }

        return label is "table" or "document_index" ? ReadTableText(item) : null;
    }

    private static string? ReadTableText(JsonElement item)
    {
        if (!item.TryGetProperty("data", out var data))
        {
            return null;
        }

        if (data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("table_cells", out var cells) || cells.ValueKind != JsonValueKind.Array)
        {
            throw new DoclingSchemaException("A Docling table has invalid cell data.");
        }

        var values = new SortedDictionary<(int Row, int Column), string>();
        foreach (var cell in cells.EnumerateArray())
        {
            if (cell.ValueKind != JsonValueKind.Object ||
                !TryInt32(cell, "start_row_offset_idx", out var row) ||
                !TryInt32(cell, "start_col_offset_idx", out var column))
            {
                throw new DoclingSchemaException("A Docling table cell has invalid coordinates.");
            }

            var text = cell.TryGetProperty("text", out var textValue)
                ? textValue.ValueKind == JsonValueKind.String
                    ? NormalizeText(textValue.GetString() ?? string.Empty)
                    : throw new DoclingSchemaException("A Docling table cell has invalid text.")
                : string.Empty;
            values[(row, column)] = text;
        }

        if (values.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var row in values.GroupBy(pair => pair.Key.Row).OrderBy(group => group.Key))
        {
            var lastColumn = row.Max(pair => pair.Key.Column);
            for (var column = 0; column <= lastColumn; column++)
            {
                if (column > 0)
                {
                    builder.Append('\t');
                }

                if (values.TryGetValue((row.Key, column), out var cellText))
                {
                    builder.Append(cellText.Replace('\t', ' '));
                }
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static bool IsFurniture(string label, JsonElement item)
    {
        if (label is "page_header" or "page_footer")
        {
            return true;
        }

        if (!item.TryGetProperty("content_layer", out var layer))
        {
            return false;
        }

        if (layer.ValueKind != JsonValueKind.String)
        {
            throw new DoclingSchemaException("A Docling item has an invalid content layer.");
        }

        return string.Equals(layer.GetString(), "furniture", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUsableContent(JsonElement item)
    {
        var label = RequiredString(item, "label");
        return label is not null && !IsFurniture(label, item) && !string.IsNullOrWhiteSpace(ReadItemText(item, label));
    }

    private static ParsedBlockKind MapKind(string label) => label switch
    {
        "title" or "section_header" or "field_heading" => ParsedBlockKind.Heading,
        "list_item" => ParsedBlockKind.ListItem,
        "code" => ParsedBlockKind.Code,
        "table" or "document_index" => ParsedBlockKind.Table,
        "formula" => ParsedBlockKind.Formula,
        "caption" => ParsedBlockKind.Caption,
        _ => ParsedBlockKind.Paragraph,
    };

    private static string ReadReference(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("$ref", out var reference) &&
            reference.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(reference.GetString()))
        {
            return reference.GetString()!;
        }

        throw new DoclingSchemaException("A Docling child reference is invalid.");
    }

    private static string RequiredString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()!
            : throw new DoclingSchemaException($"A required Docling property '{name}' is missing.");

    private static bool TryInt32(JsonElement value, string name, out int result)
    {
        result = default;
        return value.TryGetProperty(name, out var property) && property.TryGetInt32(out result);
    }

    private static bool TryFiniteDouble(JsonElement value, string name, out double result)
    {
        result = default;
        return value.TryGetProperty(name, out var property) && property.TryGetDouble(out result) && double.IsFinite(result);
    }

    private static string NormalizeText(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n')
        .Trim()
        .Normalize(NormalizationForm.FormC);

    private static string Canonicalize(JsonElement root)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, root);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    if (VolatilePropertyNames.Contains(property.Name))
                    {
                        continue;
                    }

                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                value.WriteTo(writer);
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
                throw new DoclingSchemaException("The structured Docling JSON contains an unsupported token.");
        }
    }

    private sealed record PresentationContext(int SlideNumber, string? SlideTitle);
}
