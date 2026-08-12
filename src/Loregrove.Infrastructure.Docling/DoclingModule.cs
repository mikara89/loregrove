using Loregrove.Application.Docling;
using Microsoft.Extensions.DependencyInjection;

namespace Loregrove.Infrastructure.Docling;

public static class DoclingModule
{
    public static IServiceCollection AddLoregroveDocling(
        this IServiceCollection services,
        Action<DoclingConfiguration>? configure = null,
        Action<DoclingSupervisorOptions>? configureSupervisor = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var configuration = new DoclingConfiguration();
        configure?.Invoke(configuration);
        var supervisorOptions = new DoclingSupervisorOptions();
        configureSupervisor?.Invoke(supervisorOptions);
        supervisorOptions.Validate();

        services.AddSingleton(configuration);
        services.AddSingleton(supervisorOptions);
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
        return services;
    }
}
