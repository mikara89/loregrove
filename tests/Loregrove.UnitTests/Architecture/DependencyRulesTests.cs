using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Loregrove.UnitTests.Architecture;

public sealed class DependencyRulesTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    public static TheoryData<string, string[]> ExpectedProjectReferences => new()
    {
        { "Loregrove.Domain", [] },
        { "Loregrove.Application", ["Loregrove.Domain"] },
        { "Loregrove.UI", ["Loregrove.Application"] },
        { "Loregrove.Infrastructure.Sqlite", ["Loregrove.Application"] },
        { "Loregrove.Infrastructure.LocalFiles", ["Loregrove.Application"] },
        { "Loregrove.Infrastructure.Search", ["Loregrove.Application"] },
        { "Loregrove.Infrastructure.AI", ["Loregrove.Application"] },
        { "Loregrove.Infrastructure.Docling", ["Loregrove.Application"] },
        { "Loregrove.Infrastructure.Desktop", ["Loregrove.Application"] },
        {
            "Loregrove.Desktop",
            [
                "Loregrove.Infrastructure.AI",
                "Loregrove.Infrastructure.Desktop",
                "Loregrove.Infrastructure.Docling",
                "Loregrove.Infrastructure.LocalFiles",
                "Loregrove.Infrastructure.Search",
                "Loregrove.Infrastructure.Sqlite",
                "Loregrove.UI",
            ]
        },
    };

    [Theory]
    [MemberData(nameof(ExpectedProjectReferences))]
    public void ProductionProjectsHaveOnlyApprovedReferences(
        string projectName,
        string[] expectedReferences)
    {
        var project = LoadProject(projectName);
        var actual = project
            .Descendants("ProjectReference")
            .Select(reference => ProjectNameFromReference(reference.Attribute("Include")?.Value))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expectedReferences.Order(StringComparer.Ordinal), actual);
    }

    [Theory]
    [InlineData("..\\Loregrove.Application\\Loregrove.Application.csproj")]
    [InlineData("../Loregrove.Application/Loregrove.Application.csproj")]
    public void ProjectReferenceParsingIsIndependentOfDirectorySeparator(string projectReference)
    {
        Assert.Equal("Loregrove.Application", ProjectNameFromReference(projectReference));
    }

    [Fact]
    public void DomainHasNoPackageDependencies()
    {
        var project = LoadProject("Loregrove.Domain");

        Assert.Empty(project.Descendants("PackageReference"));
    }

    [Fact]
    public void ApplicationReferencesEfCoreButNotSqliteProvider()
    {
        var packages = PackageNames("Loregrove.Application");

        Assert.Contains("Microsoft.EntityFrameworkCore", packages);
        Assert.DoesNotContain(packages, name => name.Contains("Sqlite", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DomainAndUiSourceDoNotUseEntityFrameworkCore()
    {
        AssertSourceDoesNotContain("Loregrove.Domain", "Microsoft.EntityFrameworkCore");
        AssertSourceDoesNotContain("Loregrove.UI", "Microsoft.EntityFrameworkCore");
    }

    [Fact]
    public void SharedUiDoesNotResolvePersistenceOrOpenFiles()
    {
        foreach (var token in new[]
        {
            "ILoregroveDbContext",
            "DbContext",
            "DbSet<",
            "FileStream",
            "File.Open",
            "Directory.",
            "CreateAsyncScope",
            "IDocumentParser",
            "IArtifactStore",
            "TextDocumentParser",
            "MarkdownDocumentParser",
        })
        {
            AssertSourceDoesNotContain("Loregrove.UI", token);
        }
    }

    [Fact]
    public void ParserImplementationsDoNotDependOnStorageProvidersOrPresentationFrameworks()
    {
        var parserRoot = Path.Combine(RepositoryRoot, "src", "Loregrove.Application", "Parsing");
        var implementationFiles = new[]
        {
            Path.Combine(parserRoot, "TextDocumentParser.cs"),
            Path.Combine(parserRoot, "MarkdownDocumentParser.cs"),
        };
        var forbiddenTokens = new[]
        {
            "Microsoft.EntityFrameworkCore",
            "Microsoft.Data.Sqlite",
            "Loregrove.Infrastructure",
            "Microsoft.Maui",
            "Microsoft.FluentUI",
            "IObjectStore",
            "IArtifactStore",
            "ProcessingJob",
        };

        var violations = implementationFiles.SelectMany(path => forbiddenTokens
            .Where(token => File.ReadAllText(path).Contains(token, StringComparison.Ordinal))
            .Select(token => $"{Path.GetFileName(path)} contains {token}"))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void SharedUiUsesFluentComponentsForApplicationWidgets()
    {
        var uiPath = Path.Combine(RepositoryRoot, "src", "Loregrove.UI");
        var forbiddenElements = new[] { "button", "input", "select", "textarea", "details" };
        var violations = Directory
            .EnumerateFiles(uiPath, "*.razor", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .SelectMany(path =>
            {
                var content = File.ReadAllText(path);
                return forbiddenElements
                    .Where(element => ContainsRawHtmlElement(content, element))
                    .Select(element => $"{Path.GetRelativePath(RepositoryRoot, path)} uses raw <{element}>");
            })
            .ToArray();

        Assert.Empty(violations);
    }

    [Theory]
    [InlineData("<input>", "input", true)]
    [InlineData("<input />", "input", true)]
    [InlineData("<input type=\"text\">", "input", true)]
    [InlineData("<InputFile />", "input", false)]
    [InlineData("<InputText />", "input", false)]
    [InlineData("<ButtonGroup>", "button", false)]
    public void RawHtmlElementMatcherRequiresTagBoundary(
        string content,
        string element,
        bool expected)
    {
        Assert.Equal(expected, ContainsRawHtmlElement(content, element));
    }

    [Fact]
    public void SqliteProviderApisRemainIsolatedToSqliteInfrastructure()
    {
        foreach (var projectName in new[] { "Loregrove.Domain", "Loregrove.Application", "Loregrove.UI" })
        {
            Assert.DoesNotContain(
                PackageNames(projectName),
                name => name.Contains("Sqlite", StringComparison.OrdinalIgnoreCase));
            AssertSourceDoesNotContain(projectName, "Microsoft.Data.Sqlite");
            AssertSourceDoesNotContain(projectName, "SqliteConnection");
            AssertSourceDoesNotContain(projectName, "SqliteException");
        }
    }

    [Fact]
    public void SharedUiHasNoForbiddenInfrastructureOrPlatformDependencies()
    {
        var uiProject = LoadProject("Loregrove.UI");
        var packageNames = uiProject
            .Descendants("PackageReference")
            .Select(reference => reference.Attribute("Include")?.Value ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(packageNames, name => name.Contains("EntityFramework", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(packageNames, name => name.Contains("Sqlite", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(packageNames, name => name.Contains("Docling", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(packageNames, name => name.Contains("OpenAI", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(packageNames, name => name.Contains("Maui", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ProductScreensExistOnlyInSharedUi()
    {
        var hostPath = Path.Combine(RepositoryRoot, "src", "Loregrove.Desktop");
        var hostRazorFiles = Directory
            .EnumerateFiles(hostPath, "*.razor", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path));

        Assert.Empty(hostRazorFiles);
    }

    [Fact]
    public void DesktopTargetsOnlyWindowsAndMacCatalyst()
    {
        var desktopProject = File.ReadAllText(ProjectPath("Loregrove.Desktop"));

        Assert.Contains("net10.0-maccatalyst", desktopProject, StringComparison.Ordinal);
        Assert.Contains("net10.0-windows10.0.19041.0", desktopProject, StringComparison.Ordinal);
        Assert.DoesNotContain("net10.0-android", desktopProject, StringComparison.Ordinal);
        Assert.DoesNotContain("net10.0-ios", desktopProject, StringComparison.Ordinal);
        Assert.DoesNotContain("net10.0-linux", desktopProject, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionSourceHasNoLocalWebServerBoundary()
    {
        var sourceRoot = Path.Combine(RepositoryRoot, "src");
        var forbiddenTokens = new[]
        {
            "WebApplication.CreateBuilder",
            "UseUrls(",
            "localhost:",
            "HttpListener",
        };

        var violations = Directory
            .EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .Where(path => Path.GetExtension(path) is ".cs" or ".razor" or ".csproj")
            .SelectMany(path => forbiddenTokens
                .Where(token => File.ReadAllText(path).Contains(token, StringComparison.Ordinal))
                .Select(token => $"{Path.GetRelativePath(RepositoryRoot, path)} contains {token}"))
            .ToArray();

        Assert.Empty(violations);
    }

    private static XDocument LoadProject(string projectName) => XDocument.Load(ProjectPath(projectName));

    private static string[] PackageNames(string projectName) => LoadProject(projectName)
        .Descendants("PackageReference")
        .Select(reference => reference.Attribute("Include")?.Value ?? string.Empty)
        .ToArray();

    private static void AssertSourceDoesNotContain(string projectName, string token)
    {
        var projectPath = Path.Combine(RepositoryRoot, "src", projectName);
        var violations = Directory.EnumerateFiles(projectPath, "*", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .Where(path => Path.GetExtension(path) is ".cs" or ".razor")
            .Where(path => File.ReadAllText(path).Contains(token, StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(RepositoryRoot, path))
            .ToArray();

        Assert.Empty(violations);
    }

    private static string ProjectNameFromReference(string? projectReference)
    {
        var normalized = (projectReference ?? string.Empty).Replace('\\', '/');
        return Path.GetFileNameWithoutExtension(normalized);
    }

    private static string ProjectPath(string projectName) =>
        Path.Combine(RepositoryRoot, "src", projectName, $"{projectName}.csproj");

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsRawHtmlElement(string content, string element) =>
        Regex.IsMatch(
            content,
            $@"<\s*{Regex.Escape(element)}(?=[\s/>])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Loregrove.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Loregrove repository root.");
    }
}
