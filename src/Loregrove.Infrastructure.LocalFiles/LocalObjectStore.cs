using System.Buffers;
using System.Security.Cryptography;
using Loregrove.Application.Storage;

namespace Loregrove.Infrastructure.LocalFiles;

/// <summary>
/// Immutable SHA-256-addressed storage rooted under the library's objects directory.
/// </summary>
public sealed class LocalObjectStore(ILibraryPaths paths) : IObjectStore
{
    private const int BufferSize = 81920;
    private const string TemporaryDirectoryName = ".tmp";

    public async Task<StoredObject> StoreAsync(Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead)
        {
            throw new ArgumentException("The content stream must be readable.", nameof(content));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var temporaryDirectory = Path.Combine(paths.Objects, TemporaryDirectoryName);
        Directory.CreateDirectory(temporaryDirectory);
        var temporaryPath = Path.Combine(
            temporaryDirectory,
            $"{Guid.NewGuid():N}.tmp");
        byte[]? buffer = null;

        try
        {
            buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long byteLength = 0;

            await using (var temporaryFile = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                while (true)
                {
                    var read = await content.ReadAsync(
                        buffer.AsMemory(0, BufferSize),
                        cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    hash.AppendData(buffer, 0, read);
                    await temporaryFile.WriteAsync(
                        buffer.AsMemory(0, read),
                        cancellationToken).ConfigureAwait(false);
                    byteLength = checked(byteLength + read);
                }

                await temporaryFile.FlushAsync(cancellationToken).ConfigureAwait(false);
                temporaryFile.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var contentHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            var objectKey = ObjectKeyForHash(contentHash);
            var finalPath = PathForObjectKey(objectKey);
            var finalDirectory = Path.GetDirectoryName(finalPath)
                ?? throw new InvalidOperationException("The object path has no parent directory.");
            Directory.CreateDirectory(finalDirectory);

            try
            {
                File.Move(temporaryPath, finalPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                // Another writer finalized these exact bytes after our hash was known.
                // The existing immutable object wins and the temporary copy is removed below.
            }

            return new StoredObject(contentHash, objectKey, byteLength);
        }
        finally
        {
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    public Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stream stream = new FileStream(
            PathForObjectKey(objectKey),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    public Task<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(PathForObjectKey(objectKey)));
    }

    public static string ObjectKeyForHash(string contentHash)
    {
        if (!IsSha256(contentHash))
        {
            throw new ArgumentException("The content hash must be a lowercase SHA-256 hex value.", nameof(contentHash));
        }

        return $"{contentHash[..2]}/{contentHash}";
    }

    private string PathForObjectKey(string objectKey)
    {
        ArgumentNullException.ThrowIfNull(objectKey);
        var parts = objectKey.Split('/');
        if (parts.Length != 2 ||
            parts[0].Length != 2 ||
            !IsSha256(parts[1]) ||
            !string.Equals(parts[0], parts[1][..2], StringComparison.Ordinal))
        {
            throw new ArgumentException("The object key is not a valid Loregrove SHA-256 object key.", nameof(objectKey));
        }

        return Path.Combine(paths.Objects, parts[0], parts[1]);
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a later maintenance pass may remove abandoned temp files.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup; do not hide the original capture result/failure.
        }
    }
}
