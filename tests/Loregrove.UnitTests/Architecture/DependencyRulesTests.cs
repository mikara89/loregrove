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
            .Select(reference => Path.GetFileNameWithoutExtension(reference.Attribute("Include")?.Value))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expectedReferences.Order(StringComparer.Ordinal), actual);
    }

    [Theory]
    [InlineData("Loregrove.Domain")]
    [InlineData("Loregrove.Application")]
    public void CoreProjectsHaveNoPackageDependencies(string projectName)
    {
        var project = LoadProject(projectName);

        Assert.Empty(project.Descendants("PackageReference"));
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

    private static string ProjectPath(string projectName) =>
        Path.Combine(RepositoryRoot, "src", projectName, $"{projectName}.csproj");

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

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
