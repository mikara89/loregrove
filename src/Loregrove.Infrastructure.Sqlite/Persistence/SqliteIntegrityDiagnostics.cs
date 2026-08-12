using Loregrove.Application.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Loregrove.Infrastructure.Sqlite.Persistence;

public sealed class SqliteIntegrityDiagnostics(IDbContextFactory<LoregroveDbContext> contextFactory)
    : IDatabaseIntegrityDiagnostics
{
    public async Task<DatabaseIntegrityResult> QuickCheckAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        var result = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) ?? "No result";
        return new DatabaseIntegrityResult(
            string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase),
            result);
    }
}
