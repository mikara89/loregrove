using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Loregrove.Application.Parsing;
using Loregrove.Domain.Sources;

namespace Loregrove.Infrastructure.Docling;

internal sealed record WorkbookCell(
    string Reference,
    uint RowIndex,
    int ColumnIndex,
    string? RawValue,
    string? DisplayValue,
    string? Formula,
    string? CachedValue,
    uint? StyleIndex,
    uint? NumberFormatId,
    string? NumberFormatCode);

internal sealed record WorkbookTable(
    string Name,
    string Range,
    IReadOnlyList<string> Headers);

internal sealed record WorkbookSheet(
    string Name,
    int Index,
    string Visibility,
    IReadOnlyList<WorkbookCell> Cells,
    IReadOnlyList<string> MergedRanges,
    IReadOnlyList<WorkbookTable> Tables);

internal sealed record WorkbookStructure(IReadOnlyList<WorkbookSheet> Sheets);

internal sealed record XlsxMappedStructure(
    WorkbookStructure Workbook,
    IReadOnlyList<ParsedBlock> Blocks,
    string CanonicalJson);

internal interface IXlsxStructureReader
{
    Task<XlsxMappedStructure> ReadAsync(Stream stream, CancellationToken cancellationToken);
}

internal sealed partial class OpenXmlXlsxStructureReader : IXlsxStructureReader
{
    public Task<XlsxMappedStructure> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var document = SpreadsheetDocument.Open(stream, false, new OpenSettings { AutoSave = false });
            var workbookPart = document.WorkbookPart ?? throw new DocumentParseException("The workbook part is missing.");
            var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable?
                .Elements<SharedStringItem>().Select(item => item.InnerText).ToArray() ?? [];
            var formats = ReadFormats(workbookPart.WorkbookStylesPart?.Stylesheet);
            var sheets = new List<WorkbookSheet>();
            var sheetIndex = 0;
            foreach (var sheet in workbookPart.Workbook.Sheets?.Elements<Sheet>() ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relationshipId = sheet.Id?.Value;
                if (string.IsNullOrWhiteSpace(relationshipId) ||
                    workbookPart.GetPartById(relationshipId) is not WorksheetPart worksheetPart)
                {
                    throw new DocumentParseException("A workbook sheet relationship is invalid.");
                }

                var cells = worksheetPart.Worksheet.Descendants<Cell>()
                    .Select(cell => ReadCell(cell, sharedStrings, formats))
                    .Where(cell => cell is not null)
                    .Cast<WorkbookCell>()
                    .OrderBy(cell => cell.RowIndex)
                    .ThenBy(cell => cell.ColumnIndex)
                    .ToArray();
                var merged = worksheetPart.Worksheet.Elements<MergeCells>()
                    .SelectMany(value => value.Elements<MergeCell>())
                    .Select(value => value.Reference?.Value)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Cast<string>()
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                var tables = worksheetPart.TableDefinitionParts
                    .Select(ReadTable)
                    .OrderBy(table => table.Range, StringComparer.Ordinal)
                    .ThenBy(table => table.Name, StringComparer.Ordinal)
                    .ToArray();
                sheets.Add(new WorkbookSheet(
                    sheet.Name?.Value ?? $"Sheet{sheetIndex + 1}",
                    sheetIndex,
                    ReadVisibility(sheet.State?.Value),
                    cells,
                    merged,
                    tables));
                sheetIndex++;
            }

            var structure = new WorkbookStructure(sheets);
            return Task.FromResult(new XlsxMappedStructure(
                structure,
                CreateBlocks(structure),
                Serialize(structure)));
        }
        catch (DocumentParseException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or FormatException or OpenXmlPackageException)
        {
            throw new DocumentParseException("The workbook structure could not be read.", exception);
        }
    }

    private static Dictionary<uint, (uint? NumberFormatId, string? FormatCode)> ReadFormats(Stylesheet? stylesheet)
    {
        var custom = stylesheet?.NumberingFormats?.Elements<NumberingFormat>()
            .Where(item => item.NumberFormatId?.Value is not null)
            .ToDictionary(
                item => item.NumberFormatId!.Value,
                item => item.FormatCode?.Value,
                EqualityComparer<uint>.Default) ?? [];
        var result = new Dictionary<uint, (uint?, string?)>();
        if (stylesheet?.CellFormats is null)
        {
            return result;
        }

        uint index = 0;
        foreach (var format in stylesheet.CellFormats.Elements<CellFormat>())
        {
            var numberFormatId = format.NumberFormatId?.Value;
            custom.TryGetValue(numberFormatId ?? 0, out var code);
            result[index++] = (numberFormatId, code);
        }

        return result;
    }

    private static WorkbookCell? ReadCell(
        Cell cell,
        string[] sharedStrings,
        Dictionary<uint, (uint? NumberFormatId, string? FormatCode)> formats)
    {
        var reference = cell.CellReference?.Value;
        if (string.IsNullOrWhiteSpace(reference) || !TryParseReference(reference, out var column, out var row))
        {
            throw new DocumentParseException("A workbook cell reference is invalid.");
        }

        var raw = cell.CellValue?.Text;
        var formula = cell.CellFormula?.Text;
        var cached = formula is null ? null : raw;
        string? display;
        var dataType = cell.DataType?.Value;
        if (dataType == CellValues.SharedString &&
            int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
            index >= 0 && index < sharedStrings.Length)
        {
            display = sharedStrings[index];
        }
        else if (dataType == CellValues.InlineString)
        {
            display = cell.InlineString?.InnerText;
        }
        else if (dataType == CellValues.Boolean)
        {
            display = raw == "1" ? "TRUE" : raw == "0" ? "FALSE" : raw;
        }
        else
        {
            display = raw;
        }
        if (raw is null && formula is null && string.IsNullOrEmpty(display))
        {
            return null;
        }

        var styleIndex = cell.StyleIndex?.Value;
        formats.TryGetValue(styleIndex ?? uint.MaxValue, out var format);
        return new WorkbookCell(
            reference,
            row,
            column,
            raw,
            display,
            formula,
            cached,
            styleIndex,
            format.NumberFormatId,
            format.FormatCode);
    }

    private static WorkbookTable ReadTable(TableDefinitionPart part)
    {
        var table = part.Table;
        var name = table.DisplayName?.Value ?? table.Name?.Value ?? "Table";
        var range = table.Reference?.Value ?? throw new DocumentParseException("A workbook table range is missing.");
        var headers = table.TableColumns?.Elements<TableColumn>()
            .Select(column => column.Name?.Value ?? string.Empty)
            .ToArray() ?? [];
        return new WorkbookTable(name, range, headers);
    }

    private static string ReadVisibility(SheetStateValues? state)
    {
        if (state == SheetStateValues.Hidden)
        {
            return "Hidden";
        }

        if (state == SheetStateValues.VeryHidden)
        {
            return "VeryHidden";
        }

        return "Visible";
    }

    private static List<ParsedBlock> CreateBlocks(WorkbookStructure workbook)
    {
        var blocks = new List<ParsedBlock>();
        foreach (var sheet in workbook.Sheets)
        {
            var tableCells = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var table in sheet.Tables)
            {
                var cells = CellsInRange(sheet.Cells, table.Range).ToArray();
                foreach (var cell in cells)
                {
                    tableCells.Add(cell.Reference);
                }

                if (cells.Length > 0)
                {
                    AddBlock(blocks, sheet, table.Range, table.Name, RenderRows(cells), ParsedBlockKind.Table);
                }
            }

            foreach (var row in sheet.Cells.Where(cell => !tableCells.Contains(cell.Reference)).GroupBy(cell => cell.RowIndex))
            {
                var ordered = row.OrderBy(cell => cell.ColumnIndex).ToArray();
                if (ordered.Length == 0)
                {
                    continue;
                }

                var range = ordered.Length == 1
                    ? ordered[0].Reference
                    : $"{ordered[0].Reference}:{ordered[^1].Reference}";
                AddBlock(blocks, sheet, range, null, RenderRows(ordered), ParsedBlockKind.Table);
            }
        }

        return blocks;
    }

    private static void AddBlock(
        List<ParsedBlock> blocks,
        WorkbookSheet sheet,
        string range,
        string? tableName,
        string text,
        ParsedBlockKind kind)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var heading = new[] { sheet.Name };
        blocks.Add(new ParsedBlock(
            blocks.Count,
            kind,
            text,
            new SpreadsheetSourceLocator(sheet.Name, sheet.Index, range, tableName),
            heading));
    }

    private static IEnumerable<WorkbookCell> CellsInRange(IEnumerable<WorkbookCell> cells, string range)
    {
        var parts = range.Split(':', 2);
        if (!TryParseReference(parts[0].Replace("$", string.Empty, StringComparison.Ordinal), out var startColumn, out var startRow) ||
            !TryParseReference(parts.Length == 2 ? parts[1].Replace("$", string.Empty, StringComparison.Ordinal) : parts[0], out var endColumn, out var endRow))
        {
            throw new DocumentParseException("A workbook table range is invalid.");
        }

        return cells.Where(cell => cell.ColumnIndex >= startColumn && cell.ColumnIndex <= endColumn &&
                                   cell.RowIndex >= startRow && cell.RowIndex <= endRow);
    }

    private static string RenderRows(IEnumerable<WorkbookCell> cells)
    {
        var builder = new StringBuilder();
        foreach (var row in cells.GroupBy(cell => cell.RowIndex).OrderBy(group => group.Key))
        {
            var ordered = row.OrderBy(cell => cell.ColumnIndex).ToArray();
            for (var index = 0; index < ordered.Length; index++)
            {
                if (index > 0)
                {
                    builder.Append('\t');
                }

                var cell = ordered[index];
                builder.Append(cell.Reference).Append('=').Append(
                    cell.Formula is null
                        ? cell.DisplayValue ?? cell.RawValue
                        : $"={cell.Formula}" + (cell.CachedValue is null ? string.Empty : $" [cached: {cell.CachedValue}]"));
            }

            builder.Append('\n');
        }

        return builder.ToString().TrimEnd('\n');
    }

    private static string Serialize(WorkbookStructure workbook)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WritePropertyName("sheets");
            writer.WriteStartArray();
            foreach (var sheet in workbook.Sheets.OrderBy(item => item.Index))
            {
                writer.WriteStartObject();
                writer.WriteString("name", sheet.Name);
                writer.WriteNumber("index", sheet.Index);
                writer.WriteString("visibility", sheet.Visibility);
                WriteStringArray(writer, "mergedRanges", sheet.MergedRanges);
                writer.WritePropertyName("tables");
                writer.WriteStartArray();
                foreach (var table in sheet.Tables)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", table.Name);
                    writer.WriteString("range", table.Range);
                    WriteStringArray(writer, "headers", table.Headers);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WritePropertyName("cells");
                writer.WriteStartArray();
                foreach (var cell in sheet.Cells)
                {
                    writer.WriteStartObject();
                    writer.WriteString("reference", cell.Reference);
                    WriteOptionalString(writer, "rawValue", cell.RawValue);
                    WriteOptionalString(writer, "displayValue", cell.DisplayValue);
                    WriteOptionalString(writer, "formula", cell.Formula);
                    WriteOptionalString(writer, "cachedValue", cell.CachedValue);
                    if (cell.StyleIndex is { } styleIndex) writer.WriteNumber("styleIndex", styleIndex);
                    if (cell.NumberFormatId is { } formatId) writer.WriteNumber("numberFormatId", formatId);
                    WriteOptionalString(writer, "numberFormatCode", cell.NumberFormatCode);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteStringArray(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WritePropertyName(name);
        writer.WriteStartArray();
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static void WriteOptionalString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }

    private static bool TryParseReference(string reference, out int column, out uint row)
    {
        column = 0;
        row = 0;
        var match = CellReferenceRegex().Match(reference);
        if (!match.Success || !uint.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out row) || row == 0)
        {
            return false;
        }

        foreach (var character in match.Groups[1].Value)
        {
            column = checked(column * 26 + (char.ToUpperInvariant(character) - 'A' + 1));
        }

        return column > 0;
    }

    [GeneratedRegex("^([A-Za-z]+)([1-9][0-9]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex CellReferenceRegex();
}
