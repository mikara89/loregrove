# Prompt 07 — Advanced Document Conversion

Implement Docling Serve HTTP parsing behind `IDocumentParser` for ManagedLocal and Remote modes.

Support PDF, DOCX, PPTX, common images and XLSX. Produce normalized Markdown/structured artifacts and map provenance to SourceAnchors.

For XLSX add a separate structural reader preserving sheet names, cells/ranges, raw/display values, formulas, merged ranges/tables where possible. Docling prose is not a complete workbook representation.

Persist diagnostics/partial status. Use fixture tests; real Docling tests may be environment-dependent.

Acceptance: citations can later resolve to pages/paragraphs/sheets/cells.
