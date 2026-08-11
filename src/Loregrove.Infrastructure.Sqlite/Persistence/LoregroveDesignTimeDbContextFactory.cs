using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Loregrove.Infrastructure.Sqlite.Persistence;

public sealed class LoregroveDesignTimeDbContextFactory : IDesignTimeDbContextFactory<LoregroveDbContext>
{
    public LoregroveDbContext CreateDbContext(string[] args)
    {
        var databasePath = args.FirstOrDefault() ?? Path.Combine(Directory.GetCurrentDirectory(), "library.db");
        var options = new DbContextOptionsBuilder<LoregroveDbContext>()
            .UseSqlite(SqliteModule.CreateConnectionString(databasePath))
            .Options;
        return new LoregroveDbContext(options);
    }
}
