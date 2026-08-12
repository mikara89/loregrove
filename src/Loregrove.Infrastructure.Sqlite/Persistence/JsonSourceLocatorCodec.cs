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

    private static void EnsureOnlyProperties(JsonElement root, params string[] expected)
    {
        var allowed = expected.ToHashSet(StringComparer.Ordinal);
        if (root.EnumerateObject().Any(property => !allowed.Contains(property.Name)))
        {
            throw new InvalidDataException("The source locator payload contains unknown properties.");
        }
    }
}
