using System.Security.Cryptography;
using Loregrove.Application.Storage;
using Loregrove.Infrastructure.LocalFiles;

namespace Loregrove.ContractTests;

/// <summary>
/// Reusable behavioral contract for every future <see cref="IObjectStore"/> implementation.
/// </summary>
public abstract class ObjectStoreContractTests(ObjectStoreFixture fixture) : IAsyncLifetime
{
    private readonly IObjectStore _store = fixture.Store;

    [Fact]
    public async Task StoreAndOpenRoundTrip()
    {
        var content = "round-trip evidence"u8.ToArray();

        var stored = await _store.StoreAsync(new MemoryStream(content), CancellationToken.None);
        await using var reopened = await _store.OpenReadAsync(stored.ObjectKey, CancellationToken.None);

        Assert.Equal(content, await ReadAllAsync(reopened));
        Assert.Equal(content.Length, stored.ByteLength);
        Assert.True(await _store.ExistsAsync(stored.ObjectKey, CancellationToken.None));
    }

    [Fact]
    public async Task SameBytesHaveSameIdentityAndDuplicateStoreIsReadable()
    {
        var content = "same bytes"u8.ToArray();

        var first = await _store.StoreAsync(new MemoryStream(content), CancellationToken.None);
        var second = await _store.StoreAsync(new MemoryStream(content), CancellationToken.None);

        Assert.Equal(first, second);
        await using var reopened = await _store.OpenReadAsync(first.ObjectKey, CancellationToken.None);
        Assert.Equal(content, await ReadAllAsync(reopened));
    }

    [Fact]
    public async Task DifferentBytesHaveDifferentIdentity()
    {
        var first = await _store.StoreAsync(new MemoryStream([1, 2, 3]), CancellationToken.None);
        var second = await _store.StoreAsync(new MemoryStream([1, 2, 4]), CancellationToken.None);

        Assert.NotEqual(first.ContentHash, second.ContentHash);
        Assert.NotEqual(first.ObjectKey, second.ObjectKey);
    }

    [Fact]
    public async Task EmptyContentIsStored()
    {
        var stored = await _store.StoreAsync(new MemoryStream(), CancellationToken.None);

        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            stored.ContentHash);
        Assert.Equal(0, stored.ByteLength);
        Assert.True(await _store.ExistsAsync(stored.ObjectKey, CancellationToken.None));
    }

    [Fact]
    public async Task CancellationDoesNotFinalizeAPartialObject()
    {
        var fullContent = new byte[256 * 1024];
        Random.Shared.NextBytes(fullContent);
        using var cancellation = new CancellationTokenSource();
        await using var stream = new CancelAfterFirstReadStream(fullContent, cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _store.StoreAsync(stream, cancellation.Token));

        Assert.False(await _store.ExistsAsync(ObjectKey(fullContent), CancellationToken.None));
    }

    [Fact]
    public async Task ReadFailureDoesNotFinalizeAPartialObject()
    {
        var partialContent = "partial bytes"u8.ToArray();
        await using var stream = new FaultAfterFirstReadStream(partialContent);

        await Assert.ThrowsAsync<IOException>(() =>
            _store.StoreAsync(stream, CancellationToken.None));

        Assert.False(await _store.ExistsAsync(ObjectKey(partialContent), CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentIdenticalStoresConvergeOnOneReadableIdentity()
    {
        var content = new byte[384 * 1024];
        Random.Shared.NextBytes(content);
        var tasks = Enumerable.Range(0, 12)
            .Select(_ => _store.StoreAsync(new MemoryStream(content, writable: false), CancellationToken.None));

        var results = await Task.WhenAll(tasks);

        Assert.Single(results.Select(result => result.ObjectKey).Distinct(StringComparer.Ordinal));
        await using var reopened = await _store.OpenReadAsync(results[0].ObjectKey, CancellationToken.None);
        Assert.Equal(content, await ReadAllAsync(reopened));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        fixture.Dispose();
        return Task.CompletedTask;
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var destination = new MemoryStream();
        await stream.CopyToAsync(destination);
        return destination.ToArray();
    }

    private static string ObjectKey(byte[] content)
    {
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        return $"{hash[..2]}/{hash}";
    }

    private sealed class CancelAfterFirstReadStream(
        byte[] content,
        CancellationTokenSource cancellation) : Stream
    {
        private int _position;

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
            cancellationToken.ThrowIfCancellationRequested();
            if (_position >= content.Length)
            {
                return ValueTask.FromResult(0);
            }

            var count = Math.Min(buffer.Length, content.Length - _position);
            content.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            cancellation.Cancel();
            return ValueTask.FromResult(count);
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FaultAfterFirstReadStream(byte[] content) : Stream
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

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_read)
            {
                throw new IOException("Synthetic read failure.");
            }

            _read = true;
            content.AsMemory().CopyTo(buffer);
            return ValueTask.FromResult(content.Length);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed class LocalObjectStoreContractTests()
    : ObjectStoreContractTests(ObjectStoreFixture.Create());

public sealed class ObjectStoreFixture : IDisposable
{
    private ObjectStoreFixture(string root, IObjectStore store)
    {
        Root = root;
        Store = store;
    }

    public string Root { get; }

    public IObjectStore Store { get; }

    public static ObjectStoreFixture Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "loregrove-contract", Guid.NewGuid().ToString("N"));
        var paths = new LocalLibraryPaths(root);
        Directory.CreateDirectory(paths.Objects);
        return new ObjectStoreFixture(root, new LocalObjectStore(paths));
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
