using Loregrove.Domain.Sources;

namespace Loregrove.Application.Parsing;

/// <summary>
/// Converts known typed source locators to and from their persistence representation.
/// Implementations must reject unknown kinds, schemas, and malformed payloads.
/// </summary>
public interface ISourceLocatorCodec
{
    string Serialize(SourceLocator locator);

    SourceLocator Deserialize(SourceLocatorKind kind, int schemaVersion, string value);
}
