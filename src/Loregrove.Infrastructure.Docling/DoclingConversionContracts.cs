namespace Loregrove.Infrastructure.Docling;

internal sealed record DoclingConversionRequest(
    Stream Source,
    string SafeFileName,
    string MediaType,
    string InputFormat,
    DoclingConversionProfile Profile,
    string? ApiKey);

internal enum DoclingConversionStatus
{
    Success,
    PartialSuccess,
    DocumentFailure,
}

internal sealed record DoclingConversionResult(
    DoclingConversionStatus Status,
    string? Markdown,
    string? StructuredJson,
    int WarningCount,
    string? SafeDiagnosticCode);

internal interface IDoclingConversionClient
{
    Task<DoclingConversionResult> ConvertAsync(
        Uri endpoint,
        DoclingConversionRequest request,
        Func<bool>? isLeaseValid,
        CancellationToken cancellationToken);
}
