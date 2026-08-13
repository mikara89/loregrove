using Microsoft.Extensions.DependencyInjection;

namespace Loregrove.Application.Chunking;

public static class ChunkingServiceCollectionExtensions
{
    public static IServiceCollection AddLoregroveChunking(this IServiceCollection services)
    {
        services.AddSingleton(new EvidenceAwareChunkerOptions());
        services.AddSingleton<IChunker, EvidenceAwareChunker>();
        services.AddScoped<ChunkingDocumentReader>();
        services.AddScoped<ChunkSourceService>();
        return services;
    }
}
