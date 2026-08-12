using Loregrove.Application.Library;
using Loregrove.Application.Platform;
using Loregrove.Domain.Sources;
using Microsoft.Extensions.DependencyInjection;

namespace Loregrove.Application.Client;

public sealed class LibraryClient(
    IDesktopPlatform desktopPlatform,
    LibraryImportCoordinator importCoordinator,
    IServiceScopeFactory scopeFactory) : ILibraryClient
{
    public string Name => "Library";

    public async Task<LibraryPage> GetSourcesAsync(
        LibraryQuery query,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var queryService = scope.ServiceProvider.GetRequiredService<LibraryQueryService>();
        return await queryService.GetSourcesAsync(query, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LibrarySourceDetails?> GetSourceAsync(
        SourceDocumentId documentId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var queryService = scope.ServiceProvider.GetRequiredService<LibraryQueryService>();
        return await queryService.GetSourceAsync(documentId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ImportFilesResult> PickAndImportFilesAsync(
        IProgress<ImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var files = await desktopPlatform.PickFilesAsync(cancellationToken).ConfigureAwait(false);
        return await ImportFilesAsync(files, progress, cancellationToken).ConfigureAwait(false);
    }

    public Task<ImportFilesResult> ImportFilesAsync(
        IReadOnlyList<PickedFile> files,
        IProgress<ImportProgress>? progress,
        CancellationToken cancellationToken) =>
        importCoordinator.ImportFilesAsync(files, progress, cancellationToken);
}
