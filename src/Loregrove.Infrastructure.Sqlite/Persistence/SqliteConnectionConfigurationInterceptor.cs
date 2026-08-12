using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Loregrove.Infrastructure.Sqlite.Persistence;

public sealed class SqliteConnectionConfigurationInterceptor : DbConnectionInterceptor
{
    public const int BusyTimeoutMilliseconds = 5000;

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ApplyConfiguration(connection);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await ApplyConfigurationAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static void ApplyConfiguration(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_keys=ON; PRAGMA busy_timeout={BusyTimeoutMilliseconds};";
        command.ExecuteNonQuery();
    }

    private static async Task ApplyConfigurationAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_keys=ON; PRAGMA busy_timeout={BusyTimeoutMilliseconds};";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
