using System.Buffers;
using System.Security.Cryptography;
using Loregrove.Application.Storage;

namespace Loregrove.Infrastructure.LocalFiles;

/// <summary>
/// Immutable SHA-256-addressed parsed artifact storage beneath artifacts/parsed.
/// </summary>
public sealed class LocalArtifactStore(ILibraryPaths paths) : IArtifactStore
{
    private const int BufferSize = 81920;
    private readonly string _parsedRoot = Path.Combine(paths.Artifacts, "parsed");

    public async Task<StoredArtifact> StoreAsync(Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead)
        {
            throw new ArgumentException("The artifact stream must be readable.", nameof(content));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var temporaryDirectory = Path.Combine(_parsedRoot, ".tmp");
        Directory.CreateDirectory(temporaryDirectory);
        var temporaryPath = Path.Combine(temporaryDirectory, $"{Guid.NewGuid():N}.tmp");
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
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            try
            {
                File.Move(temporaryPath, finalPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                // Concurrent deterministic writes converge on the already finalized artifact.
            }

            return new StoredArtifact(contentHash, objectKey, byteLength);
        }
        finally
        {
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            TryDelete(temporaryPath);
        }
    }

    public Task<Stream> OpenReadAsync(string artifactObjectKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stream stream = new FileStream(
            PathForObjectKey(artifactObjectKey),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    public Task<bool> ExistsAsync(string artifactObjectKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(PathForObjectKey(artifactObjectKey)));
    }

    public static string ObjectKeyForHash(string contentHash)
    {
        ValidateHash(contentHash, nameof(contentHash));
        return $"parsed/{contentHash[..2]}/{contentHash}.json";
    }

    private string PathForObjectKey(string objectKey)
    {
        ArgumentNullException.ThrowIfNull(objectKey);
        var parts = objectKey.Split('/');
        if (parts.Length != 3 ||
            !string.Equals(parts[0], "parsed", StringComparison.Ordinal) ||
            parts[1].Length != 2 ||
            !parts[2].EndsWith(".json", StringComparison.Ordinal))
        {
            throw new ArgumentException("The artifact key is not a valid parsed artifact key.", nameof(objectKey));
        }

        var hash = parts[2][..^5];
        ValidateHash(hash, nameof(objectKey));
        if (!string.Equals(parts[1], hash[..2], StringComparison.Ordinal))
        {
            throw new ArgumentException("The artifact prefix does not match its hash.", nameof(objectKey));
        }

        return Path.Combine(_parsedRoot, parts[1], parts[2]);
    }

    private static void ValidateHash(string value, string parameterName)
    {
        if (value.Length != 64 || !value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw new ArgumentException("A lowercase SHA-256 hex value is required.", parameterName);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup; maintenance may remove an abandoned temporary file.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup; preserve the original operation result.
        }
    }
}
