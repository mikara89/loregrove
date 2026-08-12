namespace Loregrove.Domain.Sources;

public readonly record struct SourceDocumentId(Guid Value)
{
    public static SourceDocumentId New() => new(Guid.NewGuid());
}

public readonly record struct SourceDocumentVersionId(Guid Value)
{
    public static SourceDocumentVersionId New() => new(Guid.NewGuid());
}

public readonly record struct ProcessingJobId(Guid Value)
{
    public static ProcessingJobId New() => new(Guid.NewGuid());
}

public readonly record struct ParsedArtifactId(Guid Value)
{
    public static ParsedArtifactId New() => new(Guid.NewGuid());
}

public readonly record struct SourceAnchorId(Guid Value)
{
    public static SourceAnchorId New() => new(Guid.NewGuid());
}
