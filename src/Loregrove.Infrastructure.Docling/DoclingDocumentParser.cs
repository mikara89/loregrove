using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Loregrove.Application.Docling;
using Loregrove.Application.Parsing;
using Loregrove.Application.Security;
using Loregrove.Domain.Sources;

namespace Loregrove.Infrastructure.Docling;

internal sealed class DoclingDocumentParser :
    IDocumentParser,
    IDocumentParserAvailability,
    IDocumentParserDescriptorProvider
{
    private const string ParserId = "loregrove.docling";
    private const string ParserVersion = "1.0.0";
    private const int ArtifactSchemaVersion = 2;

    private static readonly IReadOnlyDictionary<string, (string Format, string MediaType)> Extensions =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            [".pdf"] = ("pdf", "application/pdf"),
            [".docx"] = ("docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
            [".pptx"] = ("pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation"),
            [".xlsx"] = ("xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
            [".png"] = ("png", "image/png"),
            [".jpg"] = ("jpeg", "image/jpeg"),
            [".jpeg"] = ("jpeg", "image/jpeg"),
            [".tif"] = ("tiff", "image/tiff"),
            [".tiff"] = ("tiff", "image/tiff"),
            [".bmp"] = ("bmp", "image/bmp"),
            [".webp"] = ("webp", "image/webp"),
        };

    private static readonly Dictionary<string, (string Format, string MediaType)> MediaTypes =
        Extensions.Values.Distinct().ToDictionary(value => value.MediaType, value => value, StringComparer.OrdinalIgnoreCase);

    private readonly DoclingConfiguration _configuration;
    private readonly DoclingConversionProfile _profile;
    private readonly IDoclingPackInspector _packInspector;
    private readonly IDoclingProcessManager _processManager;
    private readonly IDoclingConversionClient _conversionClient;
    private readonly IXlsxStructureReader _xlsxReader;
    private readonly ISecretStore? _secretStore;

    public DoclingDocumentParser(
        DoclingConfiguration configuration,
        DoclingConversionProfile profile,
        IDoclingPackInspector packInspector,
        IDoclingProcessManager processManager,
        IDoclingConversionClient conversionClient,
        IXlsxStructureReader xlsxReader,
        ISecretStore? secretStore = null)
    {
        _configuration = configuration;
        _profile = profile;
        _packInspector = packInspector;
        _processManager = processManager;
        _conversionClient = conversionClient;
        _xlsxReader = xlsxReader;
        _secretStore = secretStore;
    }

    public ParserDescriptor Descriptor { get; } = ParserDescriptor.Create(
        ParserId,
        ParserVersion,
        ArtifactSchemaVersion,
        "dynamic-docling-processing-profile");

    public bool CanParse(ParseSourceDescriptor source) => TryGetFormat(source, out _);

    public async Task<ParserAvailability> GetAvailabilityAsync(
        ParseSourceDescriptor source,
        CancellationToken cancellationToken)
    {
        if (!CanParse(source))
        {
            return ParserAvailability.Deferred(ParserAvailabilityReason.DoclingApiIncompatible);
        }

        switch (_configuration.Mode)
        {
            case DoclingMode.Disabled:
                return ParserAvailability.Deferred(ParserAvailabilityReason.DoclingDisabled);
            case DoclingMode.OneShot:
                return ParserAvailability.Deferred(ParserAvailabilityReason.DoclingOneShotDeferred);
            case DoclingMode.ManagedLocal:
                var pack = await _packInspector.InspectAsync(cancellationToken).ConfigureAwait(false);
                return pack.Availability switch
                {
                    DoclingPackAvailability.Present => ParserAvailability.Available,
                    DoclingPackAvailability.Missing => ParserAvailability.Deferred(ParserAvailabilityReason.DoclingPackMissing),
                    DoclingPackAvailability.Incompatible when pack.DiagnosticCode == "runtime-unsupported" =>
                        ParserAvailability.Deferred(ParserAvailabilityReason.DoclingRuntimeUnsupported),
                    _ => ParserAvailability.Deferred(ParserAvailabilityReason.DoclingPackInvalid),
                };
            case DoclingMode.Remote:
                if (_configuration.RemoteEndpoint is null)
                {
                    return ParserAvailability.Deferred(ParserAvailabilityReason.RemoteEndpointMissing);
                }

                if (!IsAllowedRemoteEndpoint(_configuration.RemoteEndpoint))
                {
                    return ParserAvailability.Deferred(ParserAvailabilityReason.RemoteEndpointInvalid);
                }

                if (!_configuration.AllowRemoteDocumentUpload)
                {
                    return ParserAvailability.Deferred(ParserAvailabilityReason.RemoteConsentRequired);
                }

                if (!string.IsNullOrWhiteSpace(_configuration.RemoteCredentialKey) &&
                    await ReadApiKeyAsync(cancellationToken).ConfigureAwait(false) is null)
                {
                    return ParserAvailability.Deferred(ParserAvailabilityReason.RemoteCredentialUnavailable);
                }

                return ParserAvailability.Available;
            default:
                throw new InvalidOperationException("The configured Docling mode is invalid.");
        }
    }

    public async Task<ParserDescriptor> GetDescriptorAsync(
        ParseSourceDescriptor source,
        CancellationToken cancellationToken)
    {
        if (!TryGetFormat(source, out var format))
        {
            throw new InvalidOperationException("The Docling parser does not support this source.");
        }

        string processingIdentity;
        if (_configuration.Mode == DoclingMode.ManagedLocal)
        {
            var pack = await _packInspector.InspectAsync(cancellationToken).ConfigureAwait(false);
            var identity = pack.Identity ?? throw new ParserInfrastructureException(
                ParserInfrastructureFailureCode.ApiIncompatible,
                "A valid Docling Processing Pack identity is unavailable.");
            processingIdentity = string.Join('|',
                "managed",
                identity.ManifestSchemaVersion,
                identity.CommandContractVersion,
                identity.PackVersion,
                identity.RuntimeIdentifier,
                identity.DoclingVersion,
                identity.DoclingServeVersion);
        }
        else
        {
            processingIdentity = "remote|" + Hash(NormalizeEndpoint(_configuration.RemoteEndpoint!));
        }

        return ParserDescriptor.Create(
            ParserId,
            ParserVersion,
            ArtifactSchemaVersion,
            string.Join('\n', format.Format, _profile.CanonicalValue, processingIdentity));
    }

    public async Task<ParsedDocumentResult> ParseAsync(
        Stream source,
        ParseSourceDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        if (!TryGetFormat(descriptor, out var format))
        {
            throw new DocumentParseException("The source format is not supported by Docling.");
        }

        var parserDescriptor = await GetDescriptorAsync(descriptor, cancellationToken).ConfigureAwait(false);
        await using var rewindable = await RewindableConversionSource.CreateAsync(source, cancellationToken)
            .ConfigureAwait(false);
        var safeFileName = SafeFileName(descriptor.OriginalFileName, format.Format);
        var apiKey = _configuration.Mode == DoclingMode.Remote
            ? await ReadApiKeyAsync(cancellationToken).ConfigureAwait(false)
            : null;
        DoclingConversionResult conversion;
        if (_configuration.Mode == DoclingMode.ManagedLocal)
        {
            conversion = await ConvertManagedAsync(
                rewindable,
                safeFileName,
                format,
                cancellationToken).ConfigureAwait(false);
        }
        else if (_configuration.Mode == DoclingMode.Remote)
        {
            await using var upload = await rewindable.OpenReadAsync(cancellationToken).ConfigureAwait(false);
            conversion = await _conversionClient.ConvertAsync(
                _configuration.RemoteEndpoint!,
                new DoclingConversionRequest(upload, safeFileName, format.MediaType, format.Format, _profile, apiKey),
                isLeaseValid: null,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new ParserInfrastructureException(
                ParserInfrastructureFailureCode.RuntimeFailure,
                "Docling conversion was invoked while its mode was unavailable.");
        }

        if (conversion.Status == DoclingConversionStatus.DocumentFailure ||
            string.IsNullOrWhiteSpace(conversion.StructuredJson))
        {
            throw new DocumentParseException("Docling could not convert the source document.");
        }

        DoclingMappedDocument mapped;
        try
        {
            mapped = DoclingDocumentMapper.Map(conversion.StructuredJson, format.Format);
        }
        catch (Exception exception) when (exception is JsonException or DoclingSchemaException)
        {
            throw new ParserInfrastructureException(
                ParserInfrastructureFailureCode.ApiIncompatible,
                "Docling returned incompatible structured JSON.",
                exception);
        }
        var blocks = mapped.Blocks.ToList();
        var representations = new List<ParsedRepresentation>
        {
            new("doclingDocument", ParsedRepresentationKind.Json, mapped.CanonicalStructuredJson),
            new("markdown", ParsedRepresentationKind.Markdown, conversion.Markdown ?? string.Empty),
        };
        if (format.Format == "xlsx")
        {
            await using var workbookSource = await rewindable.OpenReadAsync(cancellationToken).ConfigureAwait(false);
            var workbook = await _xlsxReader.ReadAsync(workbookSource, cancellationToken).ConfigureAwait(false);
            foreach (var block in workbook.Blocks)
            {
                blocks.Add(block with { Ordinal = blocks.Count });
            }

            representations.Add(new("workbookStructure", ParsedRepresentationKind.Json, workbook.CanonicalJson));
        }

        if (blocks.Count == 0)
        {
            throw new DocumentParseException("The conversion contained no usable evidence.");
        }

        return new ParsedDocumentResult(
            parserDescriptor,
            blocks,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["apiContract"] = _profile.ApiContractVersion,
                ["conversionProfileFingerprint"] = _profile.Fingerprint,
                ["inputFormat"] = format.Format,
            },
            representations,
            conversion.Status == DoclingConversionStatus.PartialSuccess
                ? ParsedArtifactCompleteness.Partial
                : ParsedArtifactCompleteness.Complete,
            conversion.WarningCount,
            conversion.SafeDiagnosticCode);
    }

    private async Task<DoclingConversionResult> ConvertManagedAsync(
        IRewindableConversionSource source,
        string safeFileName,
        (string Format, string MediaType) format,
        CancellationToken cancellationToken)
    {
        for (var submission = 0; submission < 2; submission++)
        {
            await using var lease = await _processManager.AcquireAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var upload = await source.OpenReadAsync(cancellationToken).ConfigureAwait(false);
                var result = await _conversionClient.ConvertAsync(
                    lease.Endpoint,
                    new DoclingConversionRequest(upload, safeFileName, format.MediaType, format.Format, _profile, ApiKey: null),
                    () => lease.IsValid,
                    cancellationToken).ConfigureAwait(false);
                if (!lease.IsValid)
                {
                    throw new ParserInfrastructureException(
                        ParserInfrastructureFailureCode.RuntimeFailure,
                        "The managed Docling generation exited before conversion completed.");
                }

                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await _processManager.StopAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (ParserInfrastructureException) when (!lease.IsValid && submission == 0)
            {
                continue;
            }
        }

        throw new ParserInfrastructureException(
            ParserInfrastructureFailureCode.RuntimeFailure,
            "The managed Docling process failed twice during conversion.");
    }

    private async Task<string?> ReadApiKeyAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_configuration.RemoteCredentialKey) || _secretStore is null)
        {
            return null;
        }

        try
        {
            var value = await _secretStore.GetAsync(_configuration.RemoteCredentialKey, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(value) || value.Length > 4096 ? null : value;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private bool IsAllowedRemoteEndpoint(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            return false;
        }

        if (string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
               (IsLoopback(endpoint) || _configuration.AllowInsecureRemoteEndpoint);
    }

    private static bool IsLoopback(Uri endpoint) =>
        string.Equals(endpoint.DnsSafeHost, "localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(endpoint.DnsSafeHost, out var address) && IPAddress.IsLoopback(address));

    private static string NormalizeEndpoint(Uri endpoint)
    {
        var builder = new UriBuilder(endpoint)
        {
            Host = endpoint.IdnHost.ToLowerInvariant(),
            Path = endpoint.AbsolutePath.TrimEnd('/') + "/",
            Query = string.Empty,
            Fragment = string.Empty,
            UserName = string.Empty,
            Password = string.Empty,
        };
        return builder.Uri.AbsoluteUri;
    }

    private static bool TryGetFormat(
        ParseSourceDescriptor source,
        out (string Format, string MediaType) format)
    {
        var mediaType = source.MediaType?.Split(';', 2)[0].Trim();
        if (!string.IsNullOrWhiteSpace(mediaType) &&
            !string.Equals(mediaType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            return MediaTypes.TryGetValue(mediaType, out format);
        }

        return Extensions.TryGetValue(Path.GetExtension(source.OriginalFileName), out format);
    }

    private static string SafeFileName(string originalFileName, string format)
    {
        var basename = originalFileName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(basename))
        {
            return $"document.{DefaultExtension(format)}";
        }

        var sanitized = new string(basename.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '_').ToArray());
        sanitized = sanitized.Trim('.', ' ');
        if (string.IsNullOrWhiteSpace(sanitized) || sanitized.Length > 120)
        {
            return $"document.{DefaultExtension(format)}";
        }

        return sanitized;
    }

    private static string DefaultExtension(string format) => format == "jpeg" ? "jpg" : format;

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
