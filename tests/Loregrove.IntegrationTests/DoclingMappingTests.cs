using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Loregrove.Application.Docling;
using Loregrove.Application.Parsing;
using Loregrove.Domain.Sources;
using Loregrove.Infrastructure.Docling;
using Loregrove.Infrastructure.Sqlite.Persistence;

namespace Loregrove.IntegrationTests;

public sealed class DoclingMappingTests
{
    public static TheoryData<string, string, Type, int> FormatFixtures => new()
    {
        { "pdf-success.json", "pdf", typeof(PagedRegionSourceLocator), 3 },
        { "docx-success.json", "docx", typeof(StructuredDocumentSourceLocator), 2 },
        { "pptx-success.json", "pptx", typeof(PresentationSourceLocator), 3 },
        { "image-success.json", "png", typeof(ImageRegionSourceLocator), 1 },
        { "xlsx-success.json", "xlsx", typeof(StructuredDocumentSourceLocator), 1 },
    };

    [Theory]
    [MemberData(nameof(FormatFixtures))]
    public void MapsVersionedFormatFixture(
        string fixture,
        string format,
        Type locatorType,
        int blockCount)
    {
        var mapped = DoclingDocumentMapper.Map(ReadFixture(fixture), format);

        Assert.Equal(blockCount, mapped.Blocks.Count);
        Assert.All(mapped.Blocks, block => Assert.IsType(locatorType, block.Locator));
        Assert.Equal(Enumerable.Range(0, blockCount), mapped.Blocks.Select(block => block.Ordinal));
        using var canonical = JsonDocument.Parse(mapped.CanonicalStructuredJson);
        Assert.Equal("DoclingDocument", canonical.RootElement.GetProperty("schema_name").GetString());
        Assert.False(canonical.RootElement.TryGetProperty("task_id", out _));
        Assert.False(canonical.RootElement.TryGetProperty("timings", out _));
        Assert.False(canonical.RootElement.TryGetProperty("source_path", out _));
    }

    [Fact]
    public void PdfFixturePreservesPageGeometryHierarchyTableAndItemReference()
    {
        var mapped = DoclingDocumentMapper.Map(ReadFixture("pdf-success.json"), "pdf");

        var heading = mapped.Blocks[0];
        var locator = Assert.IsType<PagedRegionSourceLocator>(heading.Locator);
        Assert.Equal(ParsedBlockKind.Heading, heading.Kind);
        Assert.Equal(["Architecture"], heading.HeadingPath);
        Assert.Equal(1, locator.PageNumber);
        Assert.Equal("#/texts/0", locator.ItemReference);
        Assert.Equal(SourceCoordinateOrigin.BottomLeft, locator.BoundingBox!.Origin);
        Assert.Equal(72, locator.BoundingBox.Left);
        Assert.Equal(612, locator.PageWidth);
        Assert.Equal(new SourceCharacterSpan(0, 12), locator.CharacterSpan);

        Assert.Equal(["Architecture"], mapped.Blocks[1].HeadingPath);
        Assert.Equal(ParsedBlockKind.Table, mapped.Blocks[2].Kind);
        Assert.Contains("Layer\tTrust", mapped.Blocks[2].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void MapperRejectsUnknownCoordinateOrigin()
    {
        var malformed = ReadFixture("pdf-success.json")
            .Replace("BOTTOMLEFT", "MIDDLE", StringComparison.Ordinal);

        Assert.Throws<DocumentParseException>(() => DoclingDocumentMapper.Map(malformed, "pdf"));
    }

    [Fact]
    public void DocxFixtureDoesNotInventPages()
    {
        var mapped = DoclingDocumentMapper.Map(ReadFixture("docx-success.json"), "docx");

        var locator = Assert.IsType<StructuredDocumentSourceLocator>(mapped.Blocks[1].Locator);
        Assert.Null(locator.PageNumber);
        Assert.Null(locator.BoundingBox);
        Assert.Equal(["Design"], locator.HeadingPath);
    }

    [Fact]
    public void PresentationAndImageFixturesUseSourceSemantics()
    {
        var slides = DoclingDocumentMapper.Map(ReadFixture("pptx-success.json"), "pptx").Blocks;
        Assert.Equal([1, 1, 2], slides.Select(block => ((PresentationSourceLocator)block.Locator).SlideNumber));
        Assert.Equal("First slide", ((PresentationSourceLocator)slides[0].Locator).SlideTitle);
        Assert.Equal("First slide", ((PresentationSourceLocator)slides[1].Locator).SlideTitle);

        var image = DoclingDocumentMapper.Map(ReadFixture("image-success.json"), "png").Blocks.Single();
        var imageLocator = Assert.IsType<ImageRegionSourceLocator>(image.Locator);
        Assert.Equal("OCR evidence", image.Text);
        Assert.Equal(800, imageLocator.ImageWidth);
        Assert.Equal(600, imageLocator.ImageHeight);
    }

    [Fact]
    public void ResponseReaderDistinguishesPartialAndRedactsFailureDetail()
    {
        using var partialJson = JsonDocument.Parse(ReadFixture("partial-result.json"));
        var partial = DoclingV1ResponseReader.Read(partialJson.RootElement);
        Assert.Equal(DoclingConversionStatus.PartialSuccess, partial.Status);
        Assert.Equal(1, partial.WarningCount);
        Assert.Equal("# Partial\n", partial.Markdown);
        Assert.DoesNotContain("processing_time", partial.StructuredJson, StringComparison.Ordinal);

        using var failureJson = JsonDocument.Parse(ReadFixture("failure-result.json"));
        var failure = DoclingV1ResponseReader.Read(failureJson.RootElement);
        Assert.Equal(DoclingConversionStatus.DocumentFailure, failure.Status);
        Assert.Equal("docling-conversion-failed", failure.SafeDiagnosticCode);
        Assert.DoesNotContain("private", failure.SafeDiagnosticCode, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NewLocatorSchemasRoundTripAndRejectUnknownProperties()
    {
        var codec = new JsonSourceLocatorCodec();
        SourceLocator[] locators =
        [
            new PagedRegionSourceLocator(2, "#/texts/1", 3, new SourceBoundingBox(1, 10, 9, 2, SourceCoordinateOrigin.BottomLeft), new SourceCharacterSpan(4, 8), 100, 200),
            new StructuredDocumentSourceLocator("#/texts/2", 4, ["A"], null, null),
            new PresentationSourceLocator(3, "#/texts/3", 5, "Title", new SourceBoundingBox(1, 2, 3, 4, SourceCoordinateOrigin.TopLeft)),
            new ImageRegionSourceLocator("#/texts/4", 6, new SourceBoundingBox(1, 2, 3, 4, SourceCoordinateOrigin.TopLeft), 640, 480),
            new SpreadsheetSourceLocator("Summary", 0, "B2:E2", "Forecast"),
        ];

        foreach (var locator in locators)
        {
            var json = codec.Serialize(locator);
            var roundTripped = codec.Deserialize(locator.Kind, locator.SchemaVersion, json);
            Assert.Equal(json, codec.Serialize(roundTripped));
            Assert.Throws<InvalidDataException>(() => codec.Deserialize(
                locator.Kind,
                locator.SchemaVersion,
                json.Insert(json.Length - 1, ",\"unknown\":true")));
        }
    }

    [Fact]
    public async Task WorkbookReaderPreservesFormulasMergesTablesHiddenSheetsAndRanges()
    {
        await using var workbook = CreateWorkbook();
        var reader = new OpenXmlXlsxStructureReader();
        var first = await reader.ReadAsync(workbook, CancellationToken.None);
        workbook.Position = 0;
        var second = await reader.ReadAsync(workbook, CancellationToken.None);

        Assert.Equal(first.CanonicalJson, second.CanonicalJson);
        Assert.Equal(2, first.Workbook.Sheets.Count);
        var summary = first.Workbook.Sheets[0];
        Assert.Contains("A1:D1", summary.MergedRanges);
        Assert.Contains(summary.Tables, table => table.Name == "Forecast" && table.Range == "A1:D3");
        var formula = Assert.Single(summary.Cells, cell => cell.Reference == "D2");
        Assert.Equal("SUM(B2:C2)", formula.Formula);
        Assert.Equal("3", formula.CachedValue);
        Assert.Equal("Hidden", first.Workbook.Sheets[1].Visibility);
        Assert.Contains(first.Blocks, block => block.Locator is SpreadsheetSourceLocator locator &&
            locator.Range == "A1:D3" && locator.TableName == "Forecast");
    }

    [Fact]
    public async Task WorkbookReaderHandlesModeratelyLargeSheetDeterministically()
    {
        const int rowCount = 3000;
        await using var workbook = CreateLargeWorkbook(rowCount);
        var reader = new OpenXmlXlsxStructureReader();

        var mapped = await reader.ReadAsync(workbook, CancellationToken.None);

        Assert.Equal(rowCount * 2, mapped.Workbook.Sheets.Single().Cells.Count);
        Assert.Equal(rowCount, mapped.Blocks.Count);
        Assert.Equal("A3000:B3000", Assert.IsType<SpreadsheetSourceLocator>(mapped.Blocks[^1].Locator).Range);
    }

    [Fact]
    public async Task NonSeekableInputIsSpooledForReplayAndDeletedOnDispose()
    {
        var content = Encoding.UTF8.GetBytes("immutable evidence");
        await using var source = new NonSeekableReadStream(new MemoryStream(content, writable: false));
        var rewindable = Assert.IsType<RewindableConversionSource>(
            await RewindableConversionSource.CreateAsync(source, CancellationToken.None));
        var path = Assert.IsType<string>(rewindable.TemporaryPath);
        Assert.True(File.Exists(path));

        await using (var first = await rewindable.OpenReadAsync(CancellationToken.None))
        await using (var second = await rewindable.OpenReadAsync(CancellationToken.None))
        {
            using var firstBytes = new MemoryStream();
            using var secondBytes = new MemoryStream();
            await first.CopyToAsync(firstBytes);
            await second.CopyToAsync(secondBytes);
            Assert.Equal(content, firstBytes.ToArray());
            Assert.Equal(content, secondBytes.ToArray());
        }

        await rewindable.DisposeAsync();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task XlsxParserCombinesDoclingAndWorkbookRepresentationsDeterministically()
    {
        await using var workbook = CreateWorkbook();
        var parser = new DoclingDocumentParser(
            new DoclingConfiguration
            {
                Mode = DoclingMode.Remote,
                RemoteEndpoint = new Uri("http://127.0.0.1:5001/"),
                AllowRemoteDocumentUpload = true,
            },
            DoclingConversionProfile.Conservative,
            new UnusedPackInspector(),
            new ForbiddenProcessManager(),
            new XlsxConversionClient(ReadFixture("xlsx-success.json")),
            new OpenXmlXlsxStructureReader());
        var source = new ParseSourceDescriptor(
            SourceDocumentVersionId.New(),
            new string('a', 64),
            "Budget.xlsx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");

        var first = await parser.ParseAsync(workbook, source, CancellationToken.None);
        workbook.Position = 0;
        var second = await parser.ParseAsync(workbook, source, CancellationToken.None);

        Assert.Contains(first.Blocks, block => block.Locator is StructuredDocumentSourceLocator);
        Assert.Contains(first.Blocks, block => block.Locator is SpreadsheetSourceLocator locator && locator.TableName == "Forecast");
        Assert.Equal(["doclingDocument", "markdown", "workbookStructure"], first.Representations!.Select(item => item.Name));
        var firstArtifact = ParsedArtifactSerializer.Serialize(source, first);
        var secondArtifact = ParsedArtifactSerializer.Serialize(source, second);
        Assert.Equal(firstArtifact.ContentHash, secondArtifact.ContentHash);
        Assert.Equal(firstArtifact.Bytes, secondArtifact.Bytes);
    }

    [Fact]
    public async Task ParserRejectsSuccessfulConversionWithoutUsableEvidence()
    {
        const string emptyDocument = """
            {
              "schema_name":"DoclingDocument","version":"1.0.0",
              "body":{"self_ref":"#/body","children":[]},
              "furniture":{"self_ref":"#/furniture","children":[]},
              "groups":[],"tables":[],"pictures":[],"key_value_items":[],"texts":[]
            }
            """;
        var parser = new DoclingDocumentParser(
            new DoclingConfiguration
            {
                Mode = DoclingMode.Remote,
                RemoteEndpoint = new Uri("http://127.0.0.1:5001/"),
                AllowRemoteDocumentUpload = true,
            },
            DoclingConversionProfile.Conservative,
            new UnusedPackInspector(),
            new ForbiddenProcessManager(),
            new XlsxConversionClient(emptyDocument),
            new OpenXmlXlsxStructureReader());
        await using var source = new MemoryStream([1, 2, 3]);

        await Assert.ThrowsAsync<DocumentParseException>(() => parser.ParseAsync(
            source,
            new ParseSourceDescriptor(
                SourceDocumentVersionId.New(),
                new string('b', 64),
                "empty.pdf",
                "application/pdf"),
            CancellationToken.None));
    }

    [Fact]
    public async Task MalformedStructuredJsonIsTypedAsApiIncompatibility()
    {
        var parser = new DoclingDocumentParser(
            new DoclingConfiguration
            {
                Mode = DoclingMode.Remote,
                RemoteEndpoint = new Uri("http://127.0.0.1:5001/"),
                AllowRemoteDocumentUpload = true,
            },
            DoclingConversionProfile.Conservative,
            new UnusedPackInspector(),
            new ForbiddenProcessManager(),
            new XlsxConversionClient("not-json"),
            new OpenXmlXlsxStructureReader());
        await using var source = new MemoryStream([1]);

        var exception = await Assert.ThrowsAsync<ParserInfrastructureException>(() => parser.ParseAsync(
            source,
            new ParseSourceDescriptor(
                SourceDocumentVersionId.New(),
                new string('c', 64),
                "malformed.pdf",
                "application/pdf"),
            CancellationToken.None));

        Assert.Equal(ParserInfrastructureFailureCode.ApiIncompatible, exception.Code);
    }

    [Fact]
    public async Task CorruptWorkbookIsAControlledDocumentFailure()
    {
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes("not an xlsx package"));

        await Assert.ThrowsAsync<DocumentParseException>(() =>
            new OpenXmlXlsxStructureReader().ReadAsync(source, CancellationToken.None));
    }

    [Fact]
    public void MapperHandlesThousandsOfItemsInSingleIndexedTraversal()
    {
        const int count = 5000;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema_name", "DoclingDocument");
            writer.WriteString("version", "1.0.0");
            writer.WritePropertyName("body");
            writer.WriteStartObject();
            writer.WriteString("self_ref", "#/body");
            writer.WritePropertyName("children");
            writer.WriteStartArray();
            for (var index = 0; index < count; index++)
            {
                writer.WriteStartObject(); writer.WriteString("$ref", $"#/texts/{index}"); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
            writer.WritePropertyName("furniture"); writer.WriteStartObject(); writer.WriteString("self_ref", "#/furniture"); writer.WritePropertyName("children"); writer.WriteStartArray(); writer.WriteEndArray(); writer.WriteEndObject();
            foreach (var name in new[] { "groups", "tables", "pictures", "key_value_items" })
            {
                writer.WritePropertyName(name); writer.WriteStartArray(); writer.WriteEndArray();
            }
            writer.WritePropertyName("texts"); writer.WriteStartArray();
            for (var index = 0; index < count; index++)
            {
                writer.WriteStartObject();
                writer.WriteString("self_ref", $"#/texts/{index}");
                writer.WriteString("label", "paragraph");
                writer.WriteString("text", $"Evidence {index}");
                writer.WritePropertyName("children"); writer.WriteStartArray(); writer.WriteEndArray();
                writer.WritePropertyName("prov"); writer.WriteStartArray(); writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }

        var mapped = DoclingDocumentMapper.Map(Encoding.UTF8.GetString(stream.ToArray()), "docx");

        Assert.Equal(count, mapped.Blocks.Count);
        Assert.Equal("Evidence 4999", mapped.Blocks[^1].Text);
    }

    private static string ReadFixture(string name) => File.ReadAllText(Path.Combine(
        FindRepositoryRoot(),
        "tests",
        "Fixtures",
        "Docling",
        "v1",
        name));

    private static MemoryStream CreateWorkbook()
    {
        var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, true))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            var sheets = workbookPart.Workbook.AppendChild(new Sheets());

            var summaryPart = workbookPart.AddNewPart<WorksheetPart>();
            var summaryData = new SheetData(
                new Row(
                    TextCell("A1", "Name"),
                    TextCell("B1", "Value 1"),
                    TextCell("C1", "Value 2"),
                    TextCell("D1", "Total"))
                { RowIndex = 1 },
                new Row(
                    TextCell("A2", "Unicode Ω"),
                    NumberCell("B2", "1"),
                    NumberCell("C2", "2"),
                    new Cell { CellReference = "D2", CellFormula = new CellFormula("SUM(B2:C2)"), CellValue = new CellValue("3") })
                { RowIndex = 2 },
                new Row(TextCell("A3", "End"), NumberCell("B3", "4")) { RowIndex = 3 });
            summaryPart.Worksheet = new Worksheet(summaryData, new MergeCells(new MergeCell { Reference = "A1:D1" }));
            var tablePart = summaryPart.AddNewPart<TableDefinitionPart>();
            tablePart.Table = new Table
            {
                Id = 1,
                Name = "Forecast",
                DisplayName = "Forecast",
                Reference = "A1:D3",
                TotalsRowShown = false,
                AutoFilter = new AutoFilter { Reference = "A1:D3" },
                TableColumns = new TableColumns(
                    new TableColumn { Id = 1, Name = "Name" },
                    new TableColumn { Id = 2, Name = "Value1" },
                    new TableColumn { Id = 3, Name = "Value2" },
                    new TableColumn { Id = 4, Name = "Total" })
                { Count = 4 },
            };
            tablePart.Table.Save();
            summaryPart.Worksheet.Append(new TableParts(new TablePart { Id = summaryPart.GetIdOfPart(tablePart) }) { Count = 1 });
            summaryPart.Worksheet.Save();
            sheets.Append(new Sheet { Id = workbookPart.GetIdOfPart(summaryPart), SheetId = 1, Name = "Summary" });

            var hiddenPart = workbookPart.AddNewPart<WorksheetPart>();
            hiddenPart.Worksheet = new Worksheet(new SheetData(
                new Row(TextCell("A1", "Hidden evidence")) { RowIndex = 1 }));
            hiddenPart.Worksheet.Save();
            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(hiddenPart),
                SheetId = 2,
                Name = "Hidden Data",
                State = SheetStateValues.Hidden,
            });
            workbookPart.Workbook.Save();
        }

        stream.Position = 0;
        return stream;
    }

    private static MemoryStream CreateLargeWorkbook(int rowCount)
    {
        var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, true))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var sheetData = new SheetData();
            for (var rowIndex = 1; rowIndex <= rowCount; rowIndex++)
            {
                sheetData.Append(new Row(
                    NumberCell($"A{rowIndex}", rowIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    TextCell($"B{rowIndex}", $"Evidence {rowIndex}"))
                { RowIndex = checked((uint)rowIndex) });
            }

            worksheetPart.Worksheet = new Worksheet(sheetData);
            worksheetPart.Worksheet.Save();
            workbookPart.Workbook.AppendChild(new Sheets()).Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = "Large",
            });
            workbookPart.Workbook.Save();
        }

        stream.Position = 0;
        return stream;
    }

    private static Cell TextCell(string reference, string value) => new()
    {
        CellReference = reference,
        DataType = CellValues.InlineString,
        InlineString = new InlineString(new Text(value)),
    };

    private static Cell NumberCell(string reference, string value) => new()
    {
        CellReference = reference,
        CellValue = new CellValue(value),
    };

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

        throw new DirectoryNotFoundException();
    }

    private sealed class XlsxConversionClient(string structuredJson) : IDoclingConversionClient
    {
        public Task<DoclingConversionResult> ConvertAsync(Uri endpoint, DoclingConversionRequest request, Func<bool>? isLeaseValid, CancellationToken cancellationToken) =>
            Task.FromResult(new DoclingConversionResult(DoclingConversionStatus.Success, "Workbook\n", structuredJson, 0, null));
    }

    private sealed class NonSeekableReadStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class UnusedPackInspector : IDoclingPackInspector
    {
        public Task<DoclingPackValidationResult> InspectAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Remote conversion must not inspect a local pack.");
    }

    private sealed class ForbiddenProcessManager : IDoclingProcessManager
    {
        public Task<DoclingReadyEndpoint> EnsureReadyAsync(CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task<IDoclingProcessLease> AcquireAsync(CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task StopAsync(CancellationToken cancellationToken) => throw new InvalidOperationException();
        public DoclingProcessSnapshot GetSnapshot() => new(DoclingProcessState.Stopped, null, null, null, null, null, null);
    }
}
