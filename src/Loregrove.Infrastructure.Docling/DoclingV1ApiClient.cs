using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Loregrove.Application.Parsing;

namespace Loregrove.Infrastructure.Docling;

internal sealed class DoclingV1ApiClient : IDoclingConversionClient, IDisposable
{
    internal const string SubmitPath = "v1/convert/file/async";
    internal const string PollPathPrefix = "v1/status/poll/";
    internal const string ResultPathPrefix = "v1/result/";
    internal const string FileFieldName = "files";
    internal const string ApiKeyHeaderName = "X-Api-Key";

    private readonly DoclingConversionOptions _options;
    private readonly HttpClient _httpClient;

    public DoclingV1ApiClient(DoclingConversionOptions options)
    {
        _options = options;
        _httpClient = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public async Task<DoclingConversionResult> ConvertAsync(
        Uri endpoint,
        DoclingConversionRequest request,
        Func<bool>? isLeaseValid,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(request);
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overall.CancelAfter(_options.OverallTimeout);
        try
        {
            EnsureLeaseValid(isLeaseValid);
            var taskId = await SubmitAsync(endpoint, request, overall.Token).ConfigureAwait(false);
            while (true)
            {
                EnsureLeaseValid(isLeaseValid);
                var status = await PollAsync(endpoint, taskId, request.ApiKey, overall.Token).ConfigureAwait(false);
                if (status == "success")
                {
                    EnsureLeaseValid(isLeaseValid);
                    return await ReadResultAsync(endpoint, taskId, request.ApiKey, overall.Token).ConfigureAwait(false);
                }

                if (status == "failure")
                {
                    return new DoclingConversionResult(
                        DoclingConversionStatus.DocumentFailure,
                        Markdown: null,
                        StructuredJson: null,
                        WarningCount: 0,
                        SafeDiagnosticCode: "docling-conversion-failed");
                }

                await Task.Delay(_options.PollInterval, overall.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ParserInfrastructureException(
                ParserInfrastructureFailureCode.ConversionTimedOut,
                "The Docling conversion exceeded its bounded timeout.");
        }
        catch (HttpRequestException exception)
        {
            throw new ParserInfrastructureException(
                ParserInfrastructureFailureCode.TransportFailure,
                "The Docling endpoint could not be reached.",
                exception);
        }
        catch (IOException exception)
        {
            throw new ParserInfrastructureException(
                ParserInfrastructureFailureCode.TransportFailure,
                "The Docling transport was interrupted.",
                exception);
        }
        catch (JsonException exception)
        {
            throw new ParserInfrastructureException(
                ParserInfrastructureFailureCode.ApiIncompatible,
                "The Docling response did not match API contract v1.",
                exception);
        }
    }

    private async Task<string> SubmitAsync(
        Uri endpoint,
        DoclingConversionRequest conversion,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Combine(endpoint, SubmitPath));
        AddApiKey(request, conversion.ApiKey);
        using var multipart = new MultipartFormDataContent();
        using var streamContent = new StreamContent(conversion.Source);
        streamContent.Headers.ContentType = MediaTypeHeaderValue.Parse(conversion.MediaType);
        multipart.Add(streamContent, FileFieldName, conversion.SafeFileName);
        AddField(multipart, "from_formats", conversion.InputFormat);
        AddField(multipart, "to_formats", "md");
        AddField(multipart, "to_formats", "json");
        AddField(multipart, "pipeline", conversion.Profile.Pipeline);
        AddField(multipart, "do_ocr", Lower(conversion.Profile.OcrEnabled));
        AddField(multipart, "force_ocr", Lower(conversion.Profile.ForceOcr));
        AddField(multipart, "ocr_preset", conversion.Profile.OcrPreset);
        AddField(multipart, "do_table_structure", Lower(conversion.Profile.TableStructureEnabled));
        AddField(multipart, "table_mode", conversion.Profile.TableMode);
        AddField(multipart, "image_export_mode", conversion.Profile.ImageExportMode);
        AddField(multipart, "do_picture_description", Lower(conversion.Profile.PictureDescriptionEnabled));
        AddField(multipart, "do_picture_classification", Lower(conversion.Profile.PictureClassificationEnabled));
        AddField(multipart, "do_code_enrichment", Lower(conversion.Profile.CodeEnrichmentEnabled));
        AddField(multipart, "do_formula_enrichment", Lower(conversion.Profile.FormulaEnrichmentEnabled));
        AddField(multipart, "do_chart_extraction", Lower(conversion.Profile.ChartEnrichmentEnabled));
        request.Content = multipart;

        using var response = await SendAsync(request, _options.SubmitTimeout, cancellationToken).ConfigureAwait(false);
        EnsureSuccessWithoutRedirect(response);
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var root = json.RootElement;
        if (!root.TryGetProperty("task_id", out var taskId) || taskId.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(taskId.GetString()))
        {
            throw Incompatible("The Docling submit response omitted task_id.");
        }

        return taskId.GetString()!;
    }

    private async Task<string> PollAsync(
        Uri endpoint,
        string taskId,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            Combine(endpoint, PollPathPrefix + Uri.EscapeDataString(taskId)));
        AddApiKey(request, apiKey);
        using var response = await SendAsync(request, _options.PollRequestTimeout, cancellationToken).ConfigureAwait(false);
        EnsureSuccessWithoutRedirect(response);
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        if (!json.RootElement.TryGetProperty("task_status", out var status) || status.ValueKind != JsonValueKind.String)
        {
            throw Incompatible("The Docling poll response omitted task_status.");
        }

        return status.GetString() switch
        {
            "pending" => "pending",
            "started" => "started",
            "success" => "success",
            "failure" => "failure",
            _ => throw Incompatible("The Docling poll response contained an unknown task status."),
        };
    }

    private async Task<DoclingConversionResult> ReadResultAsync(
        Uri endpoint,
        string taskId,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            Combine(endpoint, ResultPathPrefix + Uri.EscapeDataString(taskId)));
        AddApiKey(request, apiKey);
        using var response = await SendAsync(request, _options.ResultTimeout, cancellationToken).ConfigureAwait(false);
        EnsureSuccessWithoutRedirect(response);
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return DoclingV1ResponseReader.Read(json.RootElement);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(timeout);
        return await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            requestTimeout.Token).ConfigureAwait(false);
    }

    private async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is { } length && length > _options.MaximumResponseBytes)
        {
            throw new ParserInfrastructureException(
                ParserInfrastructureFailureCode.ResponseTooLarge,
                "The Docling response exceeded the configured size limit.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var bounded = new MemoryStream();
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > _options.MaximumResponseBytes)
            {
                throw new ParserInfrastructureException(
                    ParserInfrastructureFailureCode.ResponseTooLarge,
                    "The Docling response exceeded the configured size limit.");
            }

            await bounded.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        bounded.Position = 0;
        return await JsonDocument.ParseAsync(bounded, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureSuccessWithoutRedirect(HttpResponseMessage response)
    {
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            throw Incompatible("Docling redirects are not supported for document conversion.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed
                ? Incompatible("The configured service does not expose the expected Docling v1 endpoint.")
                : new HttpRequestException("The Docling endpoint returned an unsuccessful response.", null, response.StatusCode);
        }
    }

    private static void EnsureLeaseValid(Func<bool>? isLeaseValid)
    {
        if (isLeaseValid is not null && !isLeaseValid())
        {
            throw new ParserInfrastructureException(
                ParserInfrastructureFailureCode.RuntimeFailure,
                "The managed Docling process exited during conversion.");
        }
    }

    private static void AddApiKey(HttpRequestMessage request, string? apiKey)
    {
        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.TryAddWithoutValidation(ApiKeyHeaderName, apiKey);
        }
    }

    private static Uri Combine(Uri endpoint, string relativePath)
    {
        var builder = new UriBuilder(endpoint)
        {
            Path = endpoint.AbsolutePath.TrimEnd('/') + "/",
        };
        return new Uri(builder.Uri, relativePath);
    }

    private static void AddField(MultipartFormDataContent content, string name, string value) =>
        content.Add(new StringContent(value, Encoding.UTF8), name);

    private static string Lower(bool value) => value ? "true" : "false";

    private static ParserInfrastructureException Incompatible(string message) =>
        new(ParserInfrastructureFailureCode.ApiIncompatible, message);

    public void Dispose() => _httpClient.Dispose();
}

internal static class DoclingV1ResponseReader
{
    internal static DoclingConversionResult Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("status", out var statusValue) || statusValue.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("The Docling result root or status is invalid.");
        }

        var status = statusValue.GetString() switch
        {
            "success" => DoclingConversionStatus.Success,
            "partial_success" => DoclingConversionStatus.PartialSuccess,
            "failure" or "skipped" => DoclingConversionStatus.DocumentFailure,
            _ => throw new JsonException("The Docling result status is unknown."),
        };
        var warningCount = root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array
            ? errors.GetArrayLength()
            : 0;
        if (status == DoclingConversionStatus.DocumentFailure)
        {
            return new(status, null, null, warningCount, "docling-conversion-failed");
        }

        if (!root.TryGetProperty("document", out var document) || document.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The Docling result omitted its document representation.");
        }

        var markdown = document.TryGetProperty("md_content", out var markdownValue) && markdownValue.ValueKind == JsonValueKind.String
            ? markdownValue.GetString()
            : null;
        string? structured = null;
        if (document.TryGetProperty("json_content", out var jsonValue))
        {
            structured = jsonValue.ValueKind switch
            {
                JsonValueKind.Object => jsonValue.GetRawText(),
                JsonValueKind.String when !string.IsNullOrWhiteSpace(jsonValue.GetString()) => jsonValue.GetString(),
                _ => null,
            };
        }

        if (string.IsNullOrWhiteSpace(structured))
        {
            return new(
                DoclingConversionStatus.DocumentFailure,
                null,
                null,
                warningCount,
                "docling-structured-output-missing");
        }

        return new(
            status,
            NormalizeMarkdown(markdown),
            structured,
            warningCount,
            status == DoclingConversionStatus.PartialSuccess ? "docling-partial-success" : null);
    }

    internal static string NormalizeMarkdown(string? markdown)
    {
        var normalized = (markdown ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        return normalized.Length == 0 ? string.Empty : normalized.TrimEnd('\n') + "\n";
    }
}
