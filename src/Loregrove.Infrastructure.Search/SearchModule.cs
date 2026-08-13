using Loregrove.Application.Search;
using Microsoft.Extensions.DependencyInjection;

namespace Loregrove.Infrastructure.Search;

public static class SearchModule
{
    public static IServiceCollection AddLoregroveSearch(this IServiceCollection services)
    {
        services.AddScoped<SqliteLexicalSearchService>();
        services.AddScoped<ILexicalSearchService>(provider =>
            provider.GetRequiredService<SqliteLexicalSearchService>());
        services.AddScoped<ILexicalSearchMaintenance>(provider =>
            provider.GetRequiredService<SqliteLexicalSearchService>());
        return services;
    }
}
