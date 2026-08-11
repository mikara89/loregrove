using System.Security.Cryptography;
using Loregrove.Application.Sources;
using Loregrove.Application.Storage;
using Loregrove.Domain.Sources;
using Loregrove.Infrastructure.LocalFiles;

namespace Loregrove.IntegrationTests;

public sealed class LocalLibraryIntegrationTests
{
    [Fact]
    public async Task InitializationIsIdempotentAndPreservesExistingData()
    {
        using var directory = TemporaryDirectory.Create();
        var paths = new LocalLibraryPaths(directory.Path);
        var initializer = new LocalLibraryInitializer(paths);

        await initializer.InitializeAsync(CancellationToken.None);
        var sentinel = Path.Combine(paths.Objects, "existing-object");
        await File.WriteAllTextAsync(sentinel, "keep", CancellationToken.None);
        await initializer.InitializeAsync(CancellationToken.None);

        Assert.All(
            new[] { paths.Root, paths.Objects, paths.Artifacts, paths.Indexes, paths.Backups, paths.Logs },
            path => Assert.True(Directory.Exists(path), path));
        Assert.Equal("keep", await File.ReadAllTextAsync(sentinel, CancellationToken.None));
    }

    [Fact]
    public async Task ObjectUsesNestedHashDirectoryAndReopensAfterStoreRestart()
    {
        using var directory = TemporaryDirectory.Create();
        var paths = new LocalLibraryPaths(directory.Path);
        var firstStore = new LocalObjectStore(paths);
        var bytes = "durable source"u8.ToArray();

        var stored = await firstStore.StoreAsync(new MemoryStream(bytes), CancellationToken.None);
        var parts = stored.ObjectKey.Split('/');
        var expectedPath = Path.Combine(paths.Objects, parts[0], parts[1]);
        var restartedStore = new LocalObjectStore(new LocalLibraryPaths(directory.Path));
        await using var reopened = await restartedStore.OpenReadAsync(stored.ObjectKey, CancellationToken.None);

        Assert.True(File.Exists(expectedPath));
        Assert.Equal(stored.ContentHash[..2], parts[0]);
        Assert.Equal(stored.ContentHash, parts[1]);
        Assert.Equal(bytes, await ReadAllAsync(reopened));
    }

    public static TheoryData<string> UnsafeFileNames => new()
    {
        "../../something.pdf",
        "..\\..\\something.pdf",
        "CON",
        "document?.pdf",
        new string('x', 5000),
        "資料-🌳.pdf",
        "folder/name\\source.txt",
    };

    [Theory]
    [MemberData(nameof(UnsafeFileNames))]
    public async Task UntrustedOriginalNameRemainsMetadataOnly(string originalFileName)
    {
        using var directory = TemporaryDirectory.Create();
        var paths = new LocalLibraryPaths(directory.Path);
        var repository = new InMemorySourceDocumentRepository();
        var service = new ImportSourceService(new LocalObjectStore(paths), repository);

        var result = await service.ImportAsync(
            new ImportSourceCommand("Imported source", originalFileName, null, new MemoryStream([4, 5, 6])),
            CancellationToken.None);

        var version = Assert.Single(repository.Versions);
        var finalObjects = FinalObjectFiles(paths).ToArray();
        Assert.Equal(ImportDisposition.Created, result.Disposition);
        Assert.Equal(originalFileName, version.OriginalFileName);
        Assert.Single(finalObjects);
        Assert.Equal(version.ContentHash, Path.GetFileName(finalObjects[0]));
        Assert.DoesNotContain(originalFileName, finalObjects[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task LargeNonSeekableSourceIsReadInBoundedChunks()
    {
        using var directory = TemporaryDirectory.Create();
        var paths = new LocalLibraryPaths(directory.Path);
        var store = new LocalObjectStore(paths);
        const long length = 12L * 1024 * 1024 + 37;
        await using var source = new SyntheticForwardOnlyStream(length);

        var stored = await store.StoreAsync(source, CancellationToken.None);
        await using var reopened = await store.OpenReadAsync(stored.ObjectKey, CancellationToken.None);
        var reopenedHash = Convert.ToHexString(await SHA256.HashDataAsync(reopened)).ToLowerInvariant();

        Assert.Equal(length, stored.ByteLength);
        Assert.Equal(stored.ContentHash, reopenedHash);
        Assert.False(source.CanSeek);
        Assert.InRange(source.MaximumRequestedBuffer, 1, 81920);
        Assert.True(source.ReadCount > 2);
    }

    [Fact]
    public async Task CancellationRemovesTemporaryDataAndCreatesNoCapture()
    {
        using var directory = TemporaryDirectory.Create();
        var paths = new LocalLibraryPaths(directory.Path);
        var repository = new InMemorySourceDocumentRepository();
        var service = new ImportSourceService(new LocalObjectStore(paths), repository);
        using var cancellation = new CancellationTokenSource();
        await using var source = new CancelingSyntheticStream(cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ImportAsync(
            new ImportSourceCommand("Cancelled", "cancelled.bin", null, source),
            cancellation.Token));

        Assert.Empty(Directory.EnumerateFiles(paths.Objects, "*", SearchOption.AllDirectories));
        Assert.Empty(repository.Documents);
        Assert.Empty(repository.Versions);
        Assert.Empty(repository.Jobs);
    }

    [Fact]
    public async Task ConcurrentIdenticalImportsCreateOneCaptureAndOneFinalObject()
    {
        using var directory = TemporaryDirectory.Create();
        var paths = new LocalLibraryPaths(directory.Path);
        var repository = new InMemorySourceDocumentRepository();
        var service = new ImportSourceService(new LocalObjectStore(paths), repository);
        var bytes = new byte[512 * 1024];
        Random.Shared.NextBytes(bytes);
        var imports = Enumerable.Range(0, 16).Select(index => service.ImportAsync(
            new ImportSourceCommand(
                $"Source {index}",
                $"renamed-{index}.bin",
                "application/octet-stream",
                new MemoryStream(bytes, writable: false)),
            CancellationToken.None));

        var results = await Task.WhenAll(imports);

        Assert.Single(results, result => result.Disposition == ImportDisposition.Created);
        Assert.Equal(15, results.Count(result => result.Disposition == ImportDisposition.AlreadyExists));
        Assert.Single(results.Select(result => result.DocumentId).Distinct());
        Assert.Single(results.Select(result => result.VersionId).Distinct());
        Assert.Single(FinalObjectFiles(paths));
        Assert.Single(repository.Documents);
        Assert.Single(repository.Versions);
        Assert.Single(repository.Jobs);
    }

    [Fact]
    public async Task ChangedBytesCreateIndependentSourcesWithoutAssumedVersionRelationship()
    {
        using var directory = TemporaryDirectory.Create();
        var repository = new InMemorySourceDocumentRepository();
        var service = new ImportSourceService(
            new LocalObjectStore(new LocalLibraryPaths(directory.Path)),
            repository);

        var first = await service.ImportAsync(
            new ImportSourceCommand("Report", "report.pdf", "application/pdf", new MemoryStream([1])),
            CancellationToken.None);
        var changed = await service.ImportAsync(
            new ImportSourceCommand("Report", "report.pdf", "application/pdf", new MemoryStream([2])),
            CancellationToken.None);

        Assert.Equal(ImportDisposition.Created, first.Disposition);
        Assert.Equal(ImportDisposition.Created, changed.Disposition);
        Assert.NotEqual(first.DocumentId, changed.DocumentId);
        Assert.Equal(2, repository.Versions.Count);
        Assert.All(repository.Versions, version => Assert.Null(version.PreviousVersionId));
    }

    [Fact]
    public async Task MetadataFailureLeavesFinalizedObjectAvailableForLaterRecovery()
    {
        using var directory = TemporaryDirectory.Create();
        var paths = new LocalLibraryPaths(directory.Path);
        var service = new ImportSourceService(new LocalObjectStore(paths), new FailingRepository());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportAsync(
            new ImportSourceCommand("Source", "source.bin", null, new MemoryStream([9, 8, 7])),
            CancellationToken.None));

        Assert.Single(FinalObjectFiles(paths));
    }

    private static IEnumerable<string> FinalObjectFiles(LocalLibraryPaths paths) =>
        Directory.Exists(paths.Objects)
            ? Directory.EnumerateFiles(paths.Objects, "*", SearchOption.AllDirectories)
                .Where(path => !path.Contains(
                    $"{Path.DirectorySeparatorChar}.tmp{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal))
            : [];

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var destination = new MemoryStream();
        await stream.CopyToAsync(destination);
        return destination.ToArray();
    }

    private sealed class InMemorySourceDocumentRepository : ISourceDocumentRepository
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, (SourceDocument Document, SourceDocumentVersion Version)> _byHash = [];

        public List<SourceDocument> Documents { get; } = [];

        public List<SourceDocumentVersion> Versions { get; } = [];

        public List<ProcessingJob> Jobs { get; } = [];

        public Task<SourceCaptureCommitResult> TryAddCaptureAsync(
            SourceDocument document,
            SourceDocumentVersion version,
            ProcessingJob processingJob,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_byHash.TryGetValue(version.ContentHash, out var existing))
                {
                    return Task.FromResult(new SourceCaptureCommitResult(
                        existing.Document.Id,
                        existing.Version.Id,
                        Created: false));
                }

                _byHash.Add(version.ContentHash, (document, version));
                Documents.Add(document);
                Versions.Add(version);
                Jobs.Add(processingJob);
                return Task.FromResult(new SourceCaptureCommitResult(document.Id, version.Id, Created: true));
            }
        }
    }

    private sealed class FailingRepository : ISourceDocumentRepository
    {
        public Task<SourceCaptureCommitResult> TryAddCaptureAsync(
            SourceDocument document,
            SourceDocumentVersion version,
            ProcessingJob processingJob,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Simulated metadata transaction failure.");
    }

    private sealed class SyntheticForwardOnlyStream(long length) : Stream
    {
        private long _position;

        public int MaximumRequestedBuffer { get; private set; }

        public int ReadCount { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MaximumRequestedBuffer = Math.Max(MaximumRequestedBuffer, buffer.Length);
            if (_position == length)
            {
                return ValueTask.FromResult(0);
            }

            var count = (int)Math.Min(buffer.Length, length - _position);
            for (var index = 0; index < count; index++)
            {
                buffer.Span[index] = (byte)((_position + index) % 251);
            }

            _position += count;
            ReadCount++;
            return ValueTask.FromResult(count);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CancelingSyntheticStream(CancellationTokenSource cancellation) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            buffer.Span[0] = 1;
            cancellation.Cancel();
            return ValueTask.FromResult(1);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "loregrove-integration",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
