using System.Text;
using Loregrove.Infrastructure.LocalFiles;

namespace Loregrove.ContractTests;

public sealed class ArtifactStoreContractTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "loregrove-artifact-contract",
        Guid.NewGuid().ToString("N"));
    private readonly LocalArtifactStore _store;

    public ArtifactStoreContractTests()
    {
        Directory.CreateDirectory(_root);
        _store = new LocalArtifactStore(new LocalLibraryPaths(_root));
    }

    [Fact]
    public async Task DeterministicBytesConvergeOnOneImmutableArtifact()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"schemaVersion\":1}");
        var writes = Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var stream = new MemoryStream(bytes, writable: false);
            return await _store.StoreAsync(stream, CancellationToken.None);
        });

        var results = await Task.WhenAll(writes);

        Assert.Single(results.Select(result => result.ObjectKey).Distinct(StringComparer.Ordinal));
        Assert.Single(Directory.EnumerateFiles(
            Path.Combine(_root, "artifacts", "parsed"),
            "*.json",
            SearchOption.AllDirectories));
        await using var opened = await _store.OpenReadAsync(results[0].ObjectKey, CancellationToken.None);
        using var memory = new MemoryStream();
        await opened.CopyToAsync(memory);
        Assert.Equal(bytes, memory.ToArray());
    }

    [Fact]
    public async Task CancellationLeavesNoFinalOrTemporaryArtifact()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("cancel"), writable: false);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _store.StoreAsync(stream, cancellation.Token));

        var parsedRoot = Path.Combine(_root, "artifacts", "parsed");
        Assert.False(Directory.Exists(parsedRoot) && Directory.EnumerateFiles(parsedRoot, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task CancellationDuringStreamingCleansTemporaryArtifact()
    {
        using var cancellation = new CancellationTokenSource();
        await using var stream = new CancelAfterFirstReadStream(cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _store.StoreAsync(stream, cancellation.Token));

        var parsedRoot = Path.Combine(_root, "artifacts", "parsed");
        Assert.False(Directory.Exists(parsedRoot) && Directory.EnumerateFiles(parsedRoot, "*", SearchOption.AllDirectories).Any());
    }

    [Theory]
    [InlineData("../escape.json")]
    [InlineData("parsed/aa/not-a-hash.json")]
    [InlineData("parsed/bb/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.json")]
    public async Task InvalidKeysCannotEscapeArtifactStore(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _store.OpenReadAsync(key, CancellationToken.None));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Contract failures should not be hidden by best-effort cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Contract failures should not be hidden by best-effort cleanup.
        }
    }

    private sealed class CancelAfterFirstReadStream(CancellationTokenSource cancellation) : Stream
    {
        private bool _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_read)
            {
                return ValueTask.FromResult(0);
            }

            _read = true;
            buffer.Span[0] = 42;
            cancellation.Cancel();
            return ValueTask.FromResult(1);
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
