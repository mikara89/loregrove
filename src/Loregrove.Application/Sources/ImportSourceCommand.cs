namespace Loregrove.Application.Sources;

public sealed record ImportSourceCommand(
    string DisplayName,
    string OriginalFileName,
    string? MediaType,
    Stream Content);
