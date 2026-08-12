using Loregrove.Application.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Loregrove.Infrastructure.Sqlite.Persistence;

public sealed class SqliteDatabaseExceptionClassifier : IDatabaseExceptionClassifier
{
    public bool IsUniqueConstraintViolation(Exception exception) =>
        exception is DbUpdateException
        {
            InnerException: SqliteException
            {
                SqliteErrorCode: 19,
                SqliteExtendedErrorCode: 1555 or 2067,
            },
        };
}
