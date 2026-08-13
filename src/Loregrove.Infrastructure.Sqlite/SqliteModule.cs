using Loregrove.Application.Chunking;
using Loregrove.Application.Library;
using Loregrove.Application.Parsing;
using Loregrove.Application.Persistence;
using Loregrove.Application.Processing;
using Loregrove.Application.Sources;
using Loregrove.Application.Storage;
using Loregrove.Infrastructure.Sqlite.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Loregrove.Infrastructure.Sqlite;

public static class SqliteModule
{
    public static IServiceCollection AddLoregroveSqlite(
        this IServiceCollection services,
        string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var connectionString = CreateConnectionString(databasePath);

        services.AddSingleton<SqliteConnectionConfigurationInterceptor>();
        services.AddDbContextFactory<LoregroveDbContext>((serviceProvider, options) =>
            options.UseSqlite(connectionString)
                .AddInterceptors(serviceProvider.GetRequiredService<SqliteConnectionConfigurationInterceptor>()));
        services.AddScoped<LoregroveDbContext>(serviceProvider =>
            serviceProvider.GetRequiredService<IDbContextFactory<LoregroveDbContext>>().CreateDbContext());
        services.AddScoped<ILoregroveDbContext>(serviceProvider =>
            serviceProvider.GetRequiredService<LoregroveDbContext>());
        services.AddSingleton<IDatabaseExceptionClassifier, SqliteDatabaseExceptionClassifier>();
        services.AddSingleton<IDatabaseIntegrityDiagnostics, SqliteIntegrityDiagnostics>();
        services.AddSingleton<ISourceLocatorCodec, JsonSourceLocatorCodec>();
        services.AddScoped<IProcessingJobRecovery, ProcessingJobRecovery>();
        services.AddScoped<ImportSourceService>();
        services.AddScoped<LibraryQueryService>();
        services.AddLoregroveChunking();
        services.AddSingleton<ILibraryInitializer, SqliteLibraryInitializer>();
        return services;
    }

    internal static string CreateConnectionString(string databasePath) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            DefaultTimeout = SqliteConnectionConfigurationInterceptor.BusyTimeoutMilliseconds / 1000,
            ForeignKeys = true,
            Pooling = true,
        }.ToString();
}
