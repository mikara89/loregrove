using System.Diagnostics;
using Loregrove.Application.Platform;
using Loregrove.Application.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Loregrove.Application.Library;

public sealed partial class LibraryImportCoordinator(
    IServiceScopeFactory scopeFactory,
    ILogger<LibraryImportCoordinator>? logger = null)
{
    public const int MaximumConcurrentImports = 4;

    public async Task<ImportFilesResult> ImportFilesAsync(
        IReadOnlyList<PickedFile> files,
        IProgress<ImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
        {
            return new ImportFilesResult([]);
        }

        var operationId = Guid.NewGuid();
        var startedAt = Stopwatch.GetTimestamp();
        if (logger?.IsEnabled(LogLevel.Information) == true)
        {
            LogOperationStarted(logger, operationId, files.Count);
        }

        using var concurrency = new SemaphoreSlim(MaximumConcurrentImports);
        for (var index = 0; index < files.Count; index++)
        {
            Report(progress, files[index], index, files.Count, ImportItemState.Queued);
        }

        var tasks = files.Select((file, index) => ImportOneAsync(
            file,
            index,
            files.Count,
            operationId,
            concurrency,
            progress,
            cancellationToken));
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var batchResult = new ImportFilesResult(results.OrderBy(result => result.Index).ToArray());
        var durationMilliseconds = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        if (logger?.IsEnabled(LogLevel.Information) == true)
        {
            LogOperationCompleted(
                logger,
                operationId,
                batchResult.ImportedCount,
                batchResult.AlreadyExistsCount,
                batchResult.FailedCount,
                batchResult.CancelledCount,
                durationMilliseconds);
        }

        return batchResult;
    }

    private async Task<ImportFileResult> ImportOneAsync(
        PickedFile file,
        int zeroBasedIndex,
        int total,
        Guid operationId,
        SemaphoreSlim concurrency,
        IProgress<ImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(file, zeroBasedIndex, total, operationId, progress);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, file, zeroBasedIndex, total, ImportItemState.Importing);
            await using var scope = scopeFactory.CreateAsyncScope();
            var importService = scope.ServiceProvider.GetRequiredService<ImportSourceService>();
            await using var content = await file.OpenReadAsync(cancellationToken).ConfigureAwait(false);
            if (!content.CanRead)
            {
                throw new IOException("The selected source stream is not readable.");
            }

            var imported = await importService.ImportAsync(
                new ImportSourceCommand(
                    file.DisplayName,
                    file.OriginalFileName,
                    file.ContentType,
                    content),
                cancellationToken).ConfigureAwait(false);
            var state = imported.Disposition == ImportDisposition.Created
                ? ImportItemState.Imported
                : ImportItemState.AlreadyExists;
            var message = state == ImportItemState.AlreadyExists ? "Already in library." : null;
            Report(progress, file, zeroBasedIndex, total, state, message);
            var fileExtension = SafeExtension(file.OriginalFileName);
            var disposition = state.ToString();
            if (logger?.IsEnabled(LogLevel.Information) == true)
            {
                LogItemCompleted(
                    logger,
                    operationId,
                    zeroBasedIndex + 1,
                    fileExtension,
                    file.Size ?? -1,
                    disposition,
                    imported.DocumentId.Value,
                    imported.VersionId.Value);
            }

            return new ImportFileResult(
                file.DisplayName,
                zeroBasedIndex + 1,
                state,
                imported.DocumentId,
                imported.VersionId,
                message);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(file, zeroBasedIndex, total, operationId, progress);
        }
        catch (Exception exception)
        {
            var message = SafeImportError.From(exception);
            Report(progress, file, zeroBasedIndex, total, ImportItemState.Failed, message);
            if (logger?.IsEnabled(LogLevel.Warning) == true)
            {
                LogItemFailed(
                    logger,
                    operationId,
                    zeroBasedIndex + 1,
                    SafeExtension(file.OriginalFileName),
                    exception.GetType().Name);
            }

            return new ImportFileResult(
                file.DisplayName,
                zeroBasedIndex + 1,
                ImportItemState.Failed,
                Message: message);
        }
        finally
        {
            concurrency.Release();
        }
    }

    private ImportFileResult Cancelled(
        PickedFile file,
        int zeroBasedIndex,
        int total,
        Guid operationId,
        IProgress<ImportProgress>? progress)
    {
        const string message = "Import cancelled.";
        Report(progress, file, zeroBasedIndex, total, ImportItemState.Cancelled, message);
        var fileExtension = SafeExtension(file.OriginalFileName);
        if (logger?.IsEnabled(LogLevel.Information) == true)
        {
            LogItemCancelled(
                logger,
                operationId,
                zeroBasedIndex + 1,
                fileExtension);
        }

        return new ImportFileResult(
            file.DisplayName,
            zeroBasedIndex + 1,
            ImportItemState.Cancelled,
            Message: message);
    }

    private static void Report(
        IProgress<ImportProgress>? progress,
        PickedFile file,
        int zeroBasedIndex,
        int total,
        ImportItemState state,
        string? message = null) =>
        progress?.Report(new ImportProgress(
            file.DisplayName,
            zeroBasedIndex + 1,
            total,
            state,
            message));

    private static string SafeExtension(string originalFileName)
    {
        var extension = Path.GetExtension(originalFileName).ToLowerInvariant();
        return extension.Length <= 16 ? extension : extension[..16];
    }

    [LoggerMessage(
        EventId = 4001,
        Level = LogLevel.Information,
        Message = "Library import operation {OperationId} started with {FileCount} files.")]
    private static partial void LogOperationStarted(ILogger logger, Guid operationId, int fileCount);

    [LoggerMessage(
        EventId = 4002,
        Level = LogLevel.Information,
        Message = "Library import operation {OperationId} completed: {ImportedCount} imported, {DuplicateCount} duplicates, {FailedCount} failed, {CancelledCount} cancelled in {DurationMilliseconds} ms.")]
    private static partial void LogOperationCompleted(
        ILogger logger,
        Guid operationId,
        int importedCount,
        int duplicateCount,
        int failedCount,
        int cancelledCount,
        double durationMilliseconds);

    [LoggerMessage(
        EventId = 4003,
        Level = LogLevel.Information,
        Message = "Library import operation {OperationId} item {ItemIndex} completed with {Disposition}; extension {FileExtension}, reported bytes {ByteLength}, document {DocumentId}, version {VersionId}.")]
    private static partial void LogItemCompleted(
        ILogger logger,
        Guid operationId,
        int itemIndex,
        string fileExtension,
        long byteLength,
        string disposition,
        Guid documentId,
        Guid versionId);

    [LoggerMessage(
        EventId = 4004,
        Level = LogLevel.Warning,
        Message = "Library import operation {OperationId} item {ItemIndex} failed; extension {FileExtension}, error type {ErrorType}.")]
    private static partial void LogItemFailed(
        ILogger logger,
        Guid operationId,
        int itemIndex,
        string fileExtension,
        string errorType);

    [LoggerMessage(
        EventId = 4005,
        Level = LogLevel.Information,
        Message = "Library import operation {OperationId} item {ItemIndex} was cancelled; extension {FileExtension}.")]
    private static partial void LogItemCancelled(
        ILogger logger,
        Guid operationId,
        int itemIndex,
        string fileExtension);

    private static class SafeImportError
    {
        public static string From(Exception exception) => exception switch
        {
            UnauthorizedAccessException => "Access was denied.",
            FileNotFoundException or DirectoryNotFoundException => "File could not be opened.",
            DbUpdateException => "The library database is temporarily busy.",
            IOException => "The source could not be stored.",
            _ => "The source could not be imported.",
        };
    }
}
