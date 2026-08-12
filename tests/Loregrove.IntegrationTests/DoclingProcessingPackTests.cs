using System.Text.Json;
using Loregrove.Application.Docling;
using Loregrove.Infrastructure.Docling;
using Microsoft.Extensions.DependencyInjection;

namespace Loregrove.IntegrationTests;

public sealed class DoclingProcessingPackTests
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    [Fact]
    public async Task DeveloperOverrideIsTheOnlyCandidateWhenConfigured()
    {
        using var application = new TemporaryDirectory();
        using var developerPack = new TemporaryDirectory();
        var configuration = new DoclingConfiguration
        {
            ApplicationBasePath = application.Path,
            DeveloperPackOverridePath = developerPack.Path,
        };
        var locator = new FileSystemDoclingPackLocator(configuration);

        var location = await locator.LocateAsync(CancellationToken.None);

        Assert.Equal(Path.GetFullPath(developerPack.Path), location?.RootPath);
    }

    [Fact]
    public async Task MissingDeveloperOverrideDoesNotFallBackToAnotherRuntime()
    {
        using var application = new TemporaryDirectory();
        var missingOverride = Path.Combine(application.Path, "missing-override");
        var configuration = new DoclingConfiguration
        {
            ApplicationBasePath = application.Path,
            DeveloperPackOverridePath = missingOverride,
        };
        var locator = new FileSystemDoclingPackLocator(configuration);

        var location = await locator.LocateAsync(CancellationToken.None);

        Assert.Null(location);
    }

    [Fact]
    public async Task InspectorReportsMissingWithoutStartingOrModifyingAnything()
    {
        using var application = new TemporaryDirectory();
        var locator = new FileSystemDoclingPackLocator(new DoclingConfiguration
        {
            ApplicationBasePath = application.Path,
            DeveloperPackOverridePath = Path.Combine(application.Path, "absent"),
        });
        var inspector = new DoclingPackInspector(locator, new FileSystemDoclingPackValidator());

        var result = await inspector.InspectAsync(CancellationToken.None);

        Assert.Equal(DoclingPackAvailability.Missing, result.Availability);
        Assert.Equal("pack-missing", result.DiagnosticCode);
        Assert.Null(result.Location);
        Assert.Null(result.Manifest);
        Assert.Empty(Directory.EnumerateFileSystemEntries(application.Path));
    }

    [Fact]
    public async Task ValidatorRejectsMissingManifestAsCorrupt()
    {
        using var pack = new TemporaryDirectory();
        var validator = new FileSystemDoclingPackValidator();

        var result = await validator.ValidateAsync(
            new DoclingPackLocation(pack.Path),
            CancellationToken.None);

        Assert.Equal(DoclingPackAvailability.Corrupt, result.Availability);
        Assert.Equal("manifest-missing", result.DiagnosticCode);
    }

    [Fact]
    public async Task ValidatorRejectsUnsupportedSchemaBeforeProcessLaunch()
    {
        using var pack = new TemporaryDirectory();
        await WriteManifestAsync(pack.Path, CreateManifest() with { SchemaVersion = 99 });
        var validator = new FileSystemDoclingPackValidator();

        var result = await validator.ValidateAsync(
            new DoclingPackLocation(pack.Path),
            CancellationToken.None);

        Assert.Equal(DoclingPackAvailability.Incompatible, result.Availability);
        Assert.Equal("manifest-schema-unsupported", result.DiagnosticCode);
    }

    [Fact]
    public async Task ValidatorRejectsUnsupportedCommandContract()
    {
        using var pack = new TemporaryDirectory();
        await WriteManifestAsync(pack.Path, CreateManifest() with { CommandContractVersion = 2 });
        var validator = new FileSystemDoclingPackValidator();

        var result = await validator.ValidateAsync(
            new DoclingPackLocation(pack.Path),
            CancellationToken.None);

        Assert.Equal(DoclingPackAvailability.Incompatible, result.Availability);
        Assert.Equal("command-contract-unsupported", result.DiagnosticCode);
    }

    [Fact]
    public async Task ValidatorRejectsMalformedManifest()
    {
        using var pack = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(pack.Path, "manifest.json"), "{not-json");
        var validator = new FileSystemDoclingPackValidator();

        var result = await validator.ValidateAsync(
            new DoclingPackLocation(pack.Path),
            CancellationToken.None);

        Assert.Equal(DoclingPackAvailability.Corrupt, result.Availability);
        Assert.Equal("manifest-json-invalid", result.DiagnosticCode);
    }

    [Fact]
    public async Task ValidatorRejectsInvalidVersionFields()
    {
        using var pack = new TemporaryDirectory();
        await WriteManifestAsync(pack.Path, CreateManifest() with { DoclingVersion = "not valid!" });
        var validator = new FileSystemDoclingPackValidator();

        var result = await validator.ValidateAsync(
            new DoclingPackLocation(pack.Path),
            CancellationToken.None);

        Assert.Equal(DoclingPackAvailability.Corrupt, result.Availability);
        Assert.Equal("version-invalid", result.DiagnosticCode);
    }

    [Fact]
    public async Task ValidatorRejectsMissingRequiredRuntimeFile()
    {
        var currentRuntime = DoclingRuntimeIdentifier.Current;
        if (currentRuntime is null)
        {
            return;
        }

        using var pack = new TemporaryDirectory();
        var entryPoint = OperatingSystem.IsWindows() ? "pack-launcher.exe" : "pack-launcher";
        await File.WriteAllTextAsync(Path.Combine(pack.Path, entryPoint), "test");
        await WriteManifestAsync(
            pack.Path,
            CreateManifest() with
            {
                RuntimeIdentifier = currentRuntime,
                EntryPoint = entryPoint,
                RequiredFiles = ["runtime/missing.file"],
            });
        var validator = new FileSystemDoclingPackValidator();

        var result = await validator.ValidateAsync(
            new DoclingPackLocation(pack.Path),
            CancellationToken.None);

        Assert.Equal(DoclingPackAvailability.Corrupt, result.Availability);
        Assert.Equal("required-file-missing", result.DiagnosticCode);
    }

    [Fact]
    public async Task ValidatorRejectsUnsupportedRuntimeWithoutInspectingPath()
    {
        using var pack = new TemporaryDirectory();
        await WriteManifestAsync(pack.Path, CreateManifest() with { RuntimeIdentifier = "linux-x64" });
        var validator = new FileSystemDoclingPackValidator();

        var result = await validator.ValidateAsync(
            new DoclingPackLocation(pack.Path),
            CancellationToken.None);

        Assert.Equal(DoclingPackAvailability.Incompatible, result.Availability);
        Assert.Equal("runtime-unsupported", result.DiagnosticCode);
    }

    [Fact]
    public async Task ValidatorAcceptsCompletePackForSupportedCurrentRuntime()
    {
        var currentRuntime = DoclingRuntimeIdentifier.Current;
        if (currentRuntime is null)
        {
            Assert.DoesNotContain(
                System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                DoclingRuntimeIdentifier.Supported);
            return;
        }

        using var pack = new TemporaryDirectory();
        var entryPoint = OperatingSystem.IsWindows() ? "bin/pack-launcher.exe" : "bin/pack-launcher";
        var runtimeFile = OperatingSystem.IsWindows() ? "runtime/python.dll" : "runtime/libpython.dylib";
        Directory.CreateDirectory(Path.Combine(pack.Path, "bin"));
        Directory.CreateDirectory(Path.Combine(pack.Path, "runtime"));
        await File.WriteAllTextAsync(Path.Combine(pack.Path, entryPoint), "test");
        await File.WriteAllTextAsync(Path.Combine(pack.Path, runtimeFile), "test");
        await WriteManifestAsync(
            pack.Path,
            CreateManifest() with
            {
                RuntimeIdentifier = currentRuntime,
                EntryPoint = entryPoint,
                RequiredFiles = [runtimeFile],
            });
        var validator = new FileSystemDoclingPackValidator();

        var result = await validator.ValidateAsync(
            new DoclingPackLocation(pack.Path),
            CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("1.0.0", result.Manifest?.PackVersion);
        Assert.Equal(
            new DoclingProcessingPackIdentity(
                1,
                1,
                "1.0.0",
                currentRuntime,
                "2.0.0",
                "1.0.0"),
            result.Identity);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/absolute/path")]
    [InlineData("")]
    public void PackFileResolutionNeverEscapesPackRoot(string relativePath)
    {
        using var pack = new TemporaryDirectory();

        var valid = FileSystemDoclingPackValidator.TryResolvePackFile(
            pack.Path,
            relativePath,
            out _);

        Assert.False(valid);
    }

    [Fact]
    public async Task ProductionRegistrationProvidesOneManagerAcrossScopes()
    {
        var services = new ServiceCollection();
        services.AddLoregroveDocling(configuration => configuration.Mode = DoclingMode.Disabled);
        await using var provider = services.BuildServiceProvider();
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();

        var first = firstScope.ServiceProvider.GetRequiredService<IDoclingProcessManager>();
        var second = secondScope.ServiceProvider.GetRequiredService<IDoclingProcessManager>();

        Assert.Same(first, second);
    }

    private static DoclingProcessingPackManifest CreateManifest() =>
        new(
            SchemaVersion: 1,
            CommandContractVersion: 1,
            PackVersion: "1.0.0",
            PythonVersion: "3.12.0",
            DoclingVersion: "2.0.0",
            DoclingServeVersion: "1.0.0",
            RuntimeIdentifier: "win-x64",
            EntryPoint: "bin/pack-launcher.exe",
            RequiredFiles: ["runtime/python.dll"]);

    private static Task WriteManifestAsync(
        string packPath,
        DoclingProcessingPackManifest manifest) =>
        File.WriteAllTextAsync(
            Path.Combine(packPath, FileSystemDoclingPackValidator.ManifestFileName),
            JsonSerializer.Serialize(manifest, ManifestJsonOptions));

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "loregrove-docling-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
