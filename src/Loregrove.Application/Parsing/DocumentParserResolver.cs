namespace Loregrove.Application.Parsing;

public sealed class DocumentParserResolver(IEnumerable<IDocumentParser> parsers) : IDocumentParserResolver
{
    private readonly IReadOnlyList<IDocumentParser> _parsers = parsers
        .OrderBy(parser => parser.Descriptor.Id, StringComparer.Ordinal)
        .ToArray();

    public IDocumentParser? Resolve(ParseSourceDescriptor source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var matches = _parsers.Where(parser => parser.CanParse(source)).Take(2).ToArray();
        return matches.Length switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidOperationException("More than one parser accepted the source descriptor."),
        };
    }
}

internal static class ParserSelection
{
    public static bool Matches(
        ParseSourceDescriptor source,
        string mediaType,
        params string[] extensions)
    {
        var normalizedMediaType = source.MediaType?.Split(';', 2)[0].Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(normalizedMediaType) &&
            normalizedMediaType != "application/octet-stream")
        {
            return string.Equals(normalizedMediaType, mediaType, StringComparison.Ordinal);
        }

        var extension = Path.GetExtension(source.OriginalFileName);
        return extensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }
}
