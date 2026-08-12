using Loregrove.Application.Docling;
using Loregrove.Application.Parsing;
using Microsoft.Extensions.DependencyInjection;

namespace Loregrove.Infrastructure.Docling;

public static class DoclingModule
{
    public static IServiceCollection AddLoregroveDocling(
        this IServiceCollection services,
        Action<DoclingConfiguration>? configure = null,
        Action<DoclingSupervisorOptions>? configureSupervisor = null,
        Action<DoclingConversionOptions>? configureConversion = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var configuration = new DoclingConfiguration();
        configure?.Invoke(configuration);
        var supervisorOptions = new DoclingSupervisorOptions();
        configureSupervisor?.Invoke(supervisorOptions);
        supervisorOptions.Validate();
        var conversionOptions = new DoclingConversionOptions();
        configureConversion?.Invoke(conversionOptions);
        conversionOptions.Validate();

        services.AddSingleton(configuration);
        services.AddSingleton(supervisorOptions);
        services.AddSingleton(conversionOptions);
        services.AddSingleton(DoclingConversionProfile.Conservative);
        services.AddSingleton<IDoclingPackLocator, FileSystemDoclingPackLocator>();
        services.AddSingleton<IDoclingPackValidator, FileSystemDoclingPackValidator>();
        services.AddSingleton<IDoclingPackInspector, DoclingPackInspector>();
        services.AddSingleton<IDoclingCommandBuilder, DoclingCommandBuilder>();
        services.AddSingleton<ILoopbackPortAllocator, LoopbackPortAllocator>();
        services.AddSingleton<IChildProcessLauncher, SystemChildProcessLauncher>();
        services.AddSingleton<HttpDoclingControlClient>();
        services.AddSingleton<IDoclingReadinessProbe>(services =>
            services.GetRequiredService<HttpDoclingControlClient>());
        services.AddSingleton<IDoclingShutdownSignal>(services =>
            services.GetRequiredService<HttpDoclingControlClient>());
        services.AddSingleton<IDoclingProcessManager, DoclingProcessManager>();
        services.AddSingleton<IDoclingConversionClient, DoclingV1ApiClient>();
        services.AddSingleton<IXlsxStructureReader, OpenXmlXlsxStructureReader>();
        services.AddSingleton<IDocumentParser, DoclingDocumentParser>();
        return services;
    }
}
