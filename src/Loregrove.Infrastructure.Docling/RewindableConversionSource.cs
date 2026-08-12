namespace Loregrove.Infrastructure.Docling;

internal interface IRewindableConversionSource : IAsyncDisposable
{
    ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken);
}

internal sealed class RewindableConversionSource : IRewindableConversionSource
{
    private readonly Stream? _seekableSource;
    private readonly string? _temporaryPath;
    private int _disposed;

    internal string? TemporaryPath => _temporaryPath;

    private RewindableConversionSource(Stream seekableSource)
    {
        _seekableSource = seekableSource;
    }

    private RewindableConversionSource(string temporaryPath)
    {
        _temporaryPath = temporaryPath;
    }

    internal static async Task<IRewindableConversionSource> CreateAsync(
        Stream source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.CanSeek)
        {
            return new RewindableConversionSource(source);
        }

        var directory = Path.Combine(Path.GetTempPath(), "loregrove-docling");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}.tmp");
        try
        {
            await using var destination = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(destination, 64 * 1024, cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new RewindableConversionSource(path);
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_seekableSource is not null)
        {
            _seekableSource.Position = 0;
            return ValueTask.FromResult<Stream>(new NonDisposingStream(_seekableSource));
        }

        return ValueTask.FromResult<Stream>(new FileStream(
            _temporaryPath!,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan));
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _temporaryPath is not null)
        {
            File.Delete(_temporaryPath);
        }

        return ValueTask.CompletedTask;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class NonDisposingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);
        protected override void Dispose(bool disposing) => base.Dispose(disposing);
        public override ValueTask DisposeAsync() => base.DisposeAsync();
    }
}
