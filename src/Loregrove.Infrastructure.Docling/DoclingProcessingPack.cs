using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Loregrove.Application.Docling;

namespace Loregrove.Infrastructure.Docling;

public sealed record DoclingProcessingPackManifest(
    int SchemaVersion,
    int CommandContractVersion,
    string PackVersion,
    string PythonVersion,
    string DoclingVersion,
    string DoclingServeVersion,
    string RuntimeIdentifier,
    string EntryPoint,
    IReadOnlyList<string> RequiredFiles);

public sealed record DoclingProcessingPackIdentity(
    int ManifestSchemaVersion,
    int CommandContractVersion,
    string PackVersion,
    string RuntimeIdentifier,
    string DoclingVersion,
    string DoclingServeVersion)
{
    public static DoclingProcessingPackIdentity FromManifest(
        DoclingProcessingPackManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return new(
            manifest.SchemaVersion,
            manifest.CommandContractVersion,
            manifest.PackVersion,
            manifest.RuntimeIdentifier,
            manifest.DoclingVersion,
            manifest.DoclingServeVersion);
    }
}

public sealed record DoclingPackLocation(string RootPath);

public enum DoclingPackAvailability
{
    Present,
    Missing,
    Incompatible,
    Corrupt,
}

public sealed record DoclingPackValidationResult(
    DoclingPackAvailability Availability,
    DoclingPackLocation? Location,
    DoclingProcessingPackManifest? Manifest,
    string DiagnosticCode)
{
    public bool IsValid => Availability == DoclingPackAvailability.Present;

    public DoclingProcessingPackIdentity? Identity =>
        Manifest is null ? null : DoclingProcessingPackIdentity.FromManifest(Manifest);
}

public interface IDoclingPackLocator
{
    Task<DoclingPackLocation?> LocateAsync(CancellationToken cancellationToken);
}

public interface IDoclingPackValidator
{
    Task<DoclingPackValidationResult> ValidateAsync(
        DoclingPackLocation location,
        CancellationToken cancellationToken);
}

public interface IDoclingPackInspector
{
    Task<DoclingPackValidationResult> InspectAsync(CancellationToken cancellationToken);
}

public sealed class DoclingPackInspector : IDoclingPackInspector
{
    private readonly IDoclingPackLocator _locator;
    private readonly IDoclingPackValidator _validator;

    public DoclingPackInspector(
        IDoclingPackLocator locator,
        IDoclingPackValidator validator)
    {
        _locator = locator;
        _validator = validator;
    }

    public async Task<DoclingPackValidationResult> InspectAsync(
        CancellationToken cancellationToken)
    {
        var location = await _locator.LocateAsync(cancellationToken);
        return location is null
            ? new(
                DoclingPackAvailability.Missing,
                Location: null,
                Manifest: null,
                DiagnosticCode: "pack-missing")
            : await _validator.ValidateAsync(location, cancellationToken);
    }
}

internal static class DoclingRuntimeIdentifier
{
    internal static readonly string[] Supported = ["win-x64", "osx-x64", "osx-arm64"];

    internal static string? Current
    {
        get
        {
            if (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
            {
                return "win-x64";
            }

            if ((OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst()) &&
                RuntimeInformation.ProcessArchitecture == Architecture.X64)
            {
                return "osx-x64";
            }

            if ((OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst()) &&
                RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            {
                return "osx-arm64";
            }

            return null;
        }
    }
}

public sealed class FileSystemDoclingPackLocator : IDoclingPackLocator
{
    private readonly DoclingConfiguration _configuration;

    public FileSystemDoclingPackLocator(DoclingConfiguration configuration)
    {
        _configuration = configuration;
    }

    public Task<DoclingPackLocation?> LocateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(_configuration.DeveloperPackOverridePath))
        {
            var overridePath = Path.GetFullPath(_configuration.DeveloperPackOverridePath);
            return Task.FromResult<DoclingPackLocation?>(
                Directory.Exists(overridePath) ? new(overridePath) : null);
        }

        var runtimeIdentifier = DoclingRuntimeIdentifier.Current;
        if (runtimeIdentifier is null)
        {
            return Task.FromResult<DoclingPackLocation?>(null);
        }

        var packagedPath = Path.GetFullPath(Path.Combine(
            _configuration.ApplicationBasePath,
            "processing-packs",
            "docling",
            runtimeIdentifier));
        return Task.FromResult<DoclingPackLocation?>(
            Directory.Exists(packagedPath) ? new(packagedPath) : null);
    }
}

public sealed class FileSystemDoclingPackValidator : IDoclingPackValidator
{
    internal const int SupportedSchemaVersion = 1;
    internal const int SupportedCommandContractVersion = 1;
    internal const string ManifestFileName = "manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public async Task<DoclingPackValidationResult> ValidateAsync(
        DoclingPackLocation location,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        var rootPath = Path.GetFullPath(location.RootPath);
        var manifestPath = Path.Combine(rootPath, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return Invalid(location, "manifest-missing");
        }

        DoclingProcessingPackManifest? manifest;
        try
        {
            await using var stream = new FileStream(
                manifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            manifest = await JsonSerializer.DeserializeAsync<DoclingProcessingPackManifest>(
                stream,
                JsonOptions,
                cancellationToken);
        }
        catch (JsonException)
        {
            return Invalid(location, "manifest-json-invalid");
        }
        catch (IOException)
        {
            return Invalid(location, "manifest-unreadable");
        }
        catch (UnauthorizedAccessException)
        {
            return Invalid(location, "manifest-unreadable");
        }

        if (manifest is null || manifest.SchemaVersion != SupportedSchemaVersion)
        {
            return Incompatible(location, manifest, "manifest-schema-unsupported");
        }

        if (manifest.CommandContractVersion != SupportedCommandContractVersion)
        {
            return Incompatible(location, manifest, "command-contract-unsupported");
        }

        if (!IsSaneVersion(manifest.PackVersion) ||
            !IsSaneVersion(manifest.PythonVersion) ||
            !IsSaneVersion(manifest.DoclingVersion) ||
            !IsSaneVersion(manifest.DoclingServeVersion))
        {
            return Invalid(location, "version-invalid", manifest);
        }

        if (!TryResolvePackFile(rootPath, manifest.EntryPoint, out _) ||
            manifest.RequiredFiles is null ||
            manifest.RequiredFiles.Count == 0)
        {
            return Invalid(location, "layout-invalid", manifest);
        }

        foreach (var relativePath in manifest.RequiredFiles.Prepend(manifest.EntryPoint))
        {
            if (!TryResolvePackFile(rootPath, relativePath, out var resolvedPath) || !File.Exists(resolvedPath))
            {
                return Invalid(location, "required-file-missing", manifest);
            }
        }

        var currentRuntime = DoclingRuntimeIdentifier.Current;
        if (currentRuntime is null ||
            !DoclingRuntimeIdentifier.Supported.Contains(manifest.RuntimeIdentifier, StringComparer.Ordinal) ||
            !string.Equals(manifest.RuntimeIdentifier, currentRuntime, StringComparison.Ordinal))
        {
            return Incompatible(location, manifest, "runtime-unsupported");
        }

        return new(DoclingPackAvailability.Present, location, manifest, "pack-valid");
    }

    internal static bool TryResolvePackFile(string rootPath, string relativePath, out string resolvedPath)
    {
        resolvedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            return false;
        }

        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var candidate = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, PathComparison))
        {
            return false;
        }

        resolvedPath = candidate;
        return true;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool IsSaneVersion(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 64 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '+' or '_');

    private static DoclingPackValidationResult Invalid(
        DoclingPackLocation location,
        string diagnosticCode,
        DoclingProcessingPackManifest? manifest = null) =>
        new(DoclingPackAvailability.Corrupt, location, manifest, diagnosticCode);

    private static DoclingPackValidationResult Incompatible(
        DoclingPackLocation location,
        DoclingProcessingPackManifest? manifest,
        string diagnosticCode) =>
        new(DoclingPackAvailability.Incompatible, location, manifest, diagnosticCode);
}
