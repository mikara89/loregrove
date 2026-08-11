namespace Loregrove.Application.Storage;

public sealed record StoredObject(
    string ContentHash,
    string ObjectKey,
    long ByteLength);
