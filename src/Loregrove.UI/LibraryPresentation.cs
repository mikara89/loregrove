using System.Globalization;
using Loregrove.Application.Library;
using Loregrove.Domain.Sources;

namespace Loregrove.UI;

public static class LibraryPresentation
{
    public static string SourceType(string originalFileName, string? mediaType)
    {
        var extension = Path.GetExtension(originalFileName).ToLowerInvariant();
        var normalizedMediaType = mediaType?.ToLowerInvariant();

        if (normalizedMediaType == "application/pdf" || extension == ".pdf")
        {
            return "PDF";
        }

        if (normalizedMediaType?.Contains("wordprocessingml", StringComparison.Ordinal) == true ||
            normalizedMediaType == "application/msword" || extension is ".doc" or ".docx")
        {
            return "Word";
        }

        if (normalizedMediaType?.Contains("spreadsheetml", StringComparison.Ordinal) == true ||
            normalizedMediaType?.Contains("excel", StringComparison.Ordinal) == true ||
            extension is ".xls" or ".xlsx")
        {
            return "Excel";
        }

        if (normalizedMediaType?.StartsWith("image/", StringComparison.Ordinal) == true ||
            extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".tif" or ".tiff")
        {
            return "Image";
        }

        if (normalizedMediaType == "text/markdown" || extension is ".md" or ".markdown")
        {
            return "Markdown";
        }

        if (normalizedMediaType?.StartsWith("text/", StringComparison.Ordinal) == true || extension == ".txt")
        {
            return "Text";
        }

        return "Other";
    }

    public static string FileSize(long byteLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteLength);
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)byteLength;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        var format = unit == 0 ? "0" : "0.#";
        return $"{size.ToString(format, CultureInfo.CurrentCulture)} {units[unit]}";
    }

    public static string ProcessingState(SourceProcessingState state) => state switch
    {
        SourceProcessingState.Captured => "Captured",
        SourceProcessingState.PendingProcessing => "Pending",
        _ => "Unknown",
    };

    public static string ImportState(ImportItemState state) => state switch
    {
        ImportItemState.Queued => "Queued",
        ImportItemState.Importing => "Importing",
        ImportItemState.Imported => "Imported",
        ImportItemState.AlreadyExists => "Already in library",
        ImportItemState.Failed => "Failed",
        ImportItemState.Cancelled => "Cancelled",
        _ => "Unknown",
    };

    public static string ImportSummary(ImportFilesResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Items.Count == 0)
        {
            return "No files selected.";
        }

        return string.Join(
            " · ",
            $"{result.ImportedCount} imported",
            $"{result.AlreadyExistsCount} already in library",
            $"{result.FailedCount} failed",
            $"{result.CancelledCount} cancelled");
    }
}
