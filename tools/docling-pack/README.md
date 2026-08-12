# Docling Processing Pack build area

This directory defines the packaging contract consumed by Prompt 06. It intentionally contains no
Python binary, wheel, model, native runtime, or runtime archive.

Processing Pack production is a development/release operation, not Loregrove runtime behavior. A
release pipeline must:

1. choose one supported runtime identifier (`win-x64`, `osx-x64`, or `osx-arm64`);
2. create a private Python runtime without depending on system Python or `PATH` at application run
   time;
3. install the reviewed, pinned Docling and docling-serve versions into a staging directory during
   the controlled pack build;
4. inspect that pinned docling-serve command interface rather than copying switches from another
   version;
5. add a native pack launcher that translates launcher contract v1 to that exact interface, binds
   `127.0.0.1`, disables UI and remote URL retrieval, serves `GET /health`, and handles
   `POST /shutdown`;
6. stage every native dependency and permitted runtime/model asset;
7. use `New-Manifest.ps1` to write `manifest.json` from explicit pinned values;
8. validate on the target OS, then sign/notarize/archive outside this repository.

`New-Manifest.ps1` only describes an already-staged pack. It never runs `pip`, `uv`, `conda`,
`brew`, `winget`, `apt`, or any downloader. Release packaging integration remains Prompt 18 work.

The final layout is conceptually:

```text
<staging root>/
  manifest.json
  bin/<pack launcher>
  runtime/<private Python and native dependencies>
  assets/<redistributable pinned assets>
```

Paths in the manifest are forward-slash relative paths. Do not include developer-machine absolute
paths. Copy `manifest.example.json` only as a shape reference; release versions and required files
must match the staged artifact exactly.
