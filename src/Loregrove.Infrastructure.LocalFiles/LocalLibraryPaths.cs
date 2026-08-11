using Loregrove.Application.Storage;

namespace Loregrove.Infrastructure.LocalFiles;

public sealed class LocalLibraryPaths : ILibraryPaths
{
    public LocalLibraryPaths(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("A library root is required.", nameof(root));
        }

        Root = Path.GetFullPath(root);
        Database = Path.Combine(Root, "library.db");
        Objects = Path.Combine(Root, "objects");
        Artifacts = Path.Combine(Root, "artifacts");
        Indexes = Path.Combine(Root, "indexes");
        Backups = Path.Combine(Root, "backups");
        Logs = Path.Combine(Root, "logs");
    }

    public string Root { get; }

    public string Database { get; }

    public string Objects { get; }

    public string Artifacts { get; }

    public string Indexes { get; }

    public string Backups { get; }

    public string Logs { get; }
}
