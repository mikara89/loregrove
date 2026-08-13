using Loregrove.Application.Processing;
using Loregrove.Application.Storage;
using Microsoft.Data.Sqlite;
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
        try
        {
            await context.Database.ExecuteSqlRawAsync(
                "SELECT count(*) FROM LexicalSearchFts LIMIT 1;",
                cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw new InvalidOperationException(
                "Loregrove Search requires SQLite FTS5, but the bundled SQLite runtime did not provide it.",
                exception);
        }
        var recovery = new ProcessingJobRecovery(context);
        await recovery.RecoverInterruptedJobsAsync(cancellationToken).ConfigureAwait(false);
    }
}
