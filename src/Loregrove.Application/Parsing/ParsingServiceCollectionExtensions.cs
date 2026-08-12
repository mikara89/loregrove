using Microsoft.Extensions.DependencyInjection;

namespace Loregrove.Application.Parsing;

public static class ParsingServiceCollectionExtensions
{
    public static IServiceCollection AddLoregroveParsing(this IServiceCollection services)
    {
        services.AddSingleton<IDocumentParser, MarkdownDocumentParser>();
        services.AddSingleton<IDocumentParser, TextDocumentParser>();
        services.AddSingleton<IDocumentParserResolver, DocumentParserResolver>();
        services.AddScoped<ParseSourceService>();
        services.AddScoped<IParsedEvidenceReader, ParsedEvidenceReader>();
        return services;
    }
}
