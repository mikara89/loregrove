using Loregrove.Application.Processing;
using Loregrove.Application.Storage;
using Microsoft.EntityFrameworkCore;

namespace Loregrove.Infrastructure.Sqlite.Persistence;

public sealed class SqliteLibraryInitializer(
    ILibraryDirectoryInitializer directoryInitializer,
    IDbContextFactory<LoregroveDbContext> contextFactory) : ILibraryInitializer
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await directoryInitializer.InitializeDirectoriesAsync(cancellationToken).ConfigureAwait(false);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken)
            .ConfigureAwait(false);
        var recovery = new ProcessingJobRecovery(context);
        await recovery.RecoverInterruptedJobsAsync(cancellationToken).ConfigureAwait(false);
    }
}
