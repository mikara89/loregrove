namespace Loregrove.Application.Storage;

/// <summary>
/// Host-selected local library layout. Platform root selection occurs at composition time.
/// </summary>
public interface ILibraryPaths
{
    string Root { get; }

    string Database { get; }

    string Objects { get; }

    string Artifacts { get; }

    string Indexes { get; }

    string Backups { get; }

    string Logs { get; }
}
