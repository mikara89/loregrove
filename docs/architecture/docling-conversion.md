# Docling conversion and complex-format anchors

Loregrove converts PDF, DOCX, PPTX, XLSX, PNG, JPEG, TIFF, BMP, and WEBP sources through the
Docling boundary. The immutable object-store stream is always the conversion input. The original
import path is metadata only and is reduced to a safe basename before multipart upload.

Conversion is implemented in `Loregrove.Infrastructure.Docling`; Application owns parser,
availability, retry, and parsed-result contracts; Domain owns only durable evidence concepts.
No UI, persistence, object-store, or AI dependency enters the converter or mapper.

## Modes and availability

| Mode | Prompt 07 behavior |
|---|---|
| `ManagedLocal` | Validate the pinned Processing Pack, acquire its exclusive generation lease, and call its loopback Docling Serve endpoint. |
| `Remote` | Call the configured endpoint without starting or stopping a local process. Explicit document-upload consent is required. HTTPS is required except for loopback development endpoints unless insecure remote use is explicitly enabled. |
| `Disabled` | Return a typed deferred result before a durable parsing claim. |
| `OneShot` | Return a typed deferred result; execution remains reserved for a later decision. |

Missing, corrupt, incompatible, or unsupported local packs and missing remote endpoint, consent, or
credential all defer before `AttemptCount` changes. Remote credentials are resolved from
`ISecretStore` by key and never enter SQLite, artifacts, fingerprints, diagnostics, or filenames.
Automatic HTTP redirects and system proxies are disabled so document bytes and credentials are not
forwarded to a different destination.

## Stable Docling Serve v1 adapter

The adapter centralizes the pinned asynchronous API contract:

1. multipart `POST /v1/convert/file/async` with one source stream and deterministic profile fields;
2. bounded polling of `GET /v1/status/poll/{task_id}`;
3. bounded `GET /v1/result/{task_id}`;
4. mapping of `success`, `partial_success`, and document `failure` without parsing human messages.

Submission, poll, result, and overall timeouts are separate. Each operation timeout covers sending,
response headers, bounded body download, and JSON parsing. Responses are streamed into a bounded
buffer, with a 128 MiB production default. Malformed or incompatible responses, oversize payloads,
timeouts, transport failures, and managed runtime exits are typed infrastructure failures. A
managed conversion is resubmitted at most once and only when its acquired generation is proven
invalid; a transport failure on a still-valid generation is not transparently retried. Caller
cancellation stops the owned generation if submission is in flight.

Seekable immutable object streams are rewound without being disposed by the adapter. Non-seekable
streams are copied once to a random temporary file, reopened for the bounded retry and XLSX
structural pass, and deleted deterministically on disposal.

## Deterministic processing identity and artifact

The conservative profile explicitly fixes the API contract, input format, standard pipeline,
format-driven OCR, accurate table extraction, placeholder image export, disabled enrichment, mapper
schema, and workbook schema. Its canonical value is hashed into parser identity. Managed identity
also includes Processing Pack, runtime, Docling, and docling-serve versions. Remote identity includes
a normalized endpoint hash but never a credential.

The schema-2 parsed artifact contains deterministic representations named `doclingDocument`,
`markdown`, and, for XLSX, `workbookStructure`. JSON object properties are canonicalized while array
order is preserved. Task IDs, timing data, process metadata, timestamps, and machine source paths
are removed. Markdown uses LF line endings. Relational and artifact metadata record complete versus
partial output, warning count, and a bounded safe diagnostic code; raw upstream errors are not
persisted.

`partial_success` commits only when usable evidence exists. A document conversion failure or an
otherwise successful response with no usable evidence follows the existing parse-failure path and
commits no artifact or anchors. Infrastructure failures return the claimed job to retryable
Pending/Parsing while preserving its consumed attempt.

## Evidence mapping

Docling reading order is traversed through a reference index, with cycle and missing-reference
checks. The mapper retains headings, paragraphs, list items, code, tables, formulae, captions, and
OCR text, and ignores headers, footers, and furniture. It does not infer facts, create image
descriptions, evaluate formulas, or fetch remote content.

Typed schema-2 complex locator payloads preserve every upstream provenance region and only source
semantics genuinely available for each format (spreadsheet locators remain schema 1):

- PDF: ordered pages/regions, item reference, block ordinal, optional finite bounding boxes and
  character spans, and optional per-page dimensions;
- DOCX: item reference, ordinal, heading path, and ordered upstream page/region provenance;
- PPTX: structural slide-group number/title context, item reference, ordinal, and ordered regions;
  when neither group context nor provenance supplies a number, the slide number remains unknown;
- images: item reference, ordinal, ordered OCR regions, and optional pixel dimensions;
- XLSX: sheet name/index/visibility, cell or range, optional table name, and ordinal.

Locator JSON is strict: unknown kinds, schema versions, properties, invalid ranges, non-finite
geometry, and invalid coordinate systems are rejected. Structurally incompatible Docling JSON is
an API-infrastructure incompatibility and returns a claimed job to retryable Pending/Parsing; an
explicit upstream conversion failure remains a non-retryable document parse failure.

## XLSX structural preservation

XLSX conversion combines Docling content with a read-only Open XML pass. The structural
representation preserves sheet order/name/visibility, raw and display cell values, formulas and
cached values without evaluation, style/number-format identifiers, merged ranges, and explicit
table names/ranges/headers. Table blocks and remaining row-range blocks produce spreadsheet
locators. Hidden sheets are retained as source evidence.

## Validation

Versioned Docling v1 fixtures cover every supported format, partial/failure responses, provenance,
OCR, tables, and Unicode. Integration tests cover real loopback multipart/poll/result traffic,
stalled response bodies, redirect and credential containment, managed generation loss, cancellation,
deterministic replay, large structured output, a moderately large workbook, migrations, transactions,
and concurrency semantics.

`DoclingRealProcessTests.OptionalRealDoclingSmokeIsExplicitlyReported` is the real-pack gate. It is
not executed unless `LOREGROVE_DOCLING_SMOKE=1` and `LOREGROVE_DOCLING_PACK` identifies a compatible
pack. When enabled it verifies the complete immutable-object -> ManagedLocal Docling -> parsed
artifact -> paged-anchor path using a real PDF and requires extracted text, a page, and a bounding
box. A normal test run reports the gate as not executed rather than implying real Docling coverage.
