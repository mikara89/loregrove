using System.Security.Cryptography;
using System.Text;

namespace Loregrove.Infrastructure.Docling;

internal sealed record DoclingConversionProfile(
    string ApiContractVersion,
    string Pipeline,
    bool OcrEnabled,
    bool ForceOcr,
    string OcrPreset,
    bool TableStructureEnabled,
    string TableMode,
    string ImageExportMode,
    bool PictureDescriptionEnabled,
    bool PictureClassificationEnabled,
    bool CodeEnrichmentEnabled,
    bool FormulaEnrichmentEnabled,
    bool ChartEnrichmentEnabled,
    string MapperVersion,
    string WorkbookReaderVersion)
{
    internal static DoclingConversionProfile Conservative { get; } = new(
        ApiContractVersion: "docling-serve-v1",
        Pipeline: "standard",
        OcrEnabled: true,
        ForceOcr: false,
        OcrPreset: "auto",
        TableStructureEnabled: true,
        TableMode: "accurate",
        ImageExportMode: "placeholder",
        PictureDescriptionEnabled: false,
        PictureClassificationEnabled: false,
        CodeEnrichmentEnabled: false,
        FormulaEnrichmentEnabled: false,
        ChartEnrichmentEnabled: false,
        MapperVersion: "docling-document-v2",
        WorkbookReaderVersion: "openxml-v1");

    internal string CanonicalValue => string.Join('\n',
        ApiContractVersion,
        Pipeline,
        OcrEnabled,
        ForceOcr,
        OcrPreset,
        TableStructureEnabled,
        TableMode,
        ImageExportMode,
        PictureDescriptionEnabled,
        PictureClassificationEnabled,
        CodeEnrichmentEnabled,
        FormulaEnrichmentEnabled,
        ChartEnrichmentEnabled,
        MapperVersion,
        WorkbookReaderVersion);

    internal string Fingerprint => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalValue))).ToLowerInvariant();
}
