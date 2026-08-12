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
            default:
                throw new ArgumentOutOfRangeException(nameof(locator), "Unknown source locator type.");
        }

        writer.WriteEndObject();
    }
}
