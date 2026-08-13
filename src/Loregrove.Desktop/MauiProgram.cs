using Loregrove.Application.Client;
using Loregrove.Application.Library;
using Loregrove.Application.Platform;
using Loregrove.Application.Parsing;
using Loregrove.Application.Security;
using Loregrove.Application.Storage;
using Loregrove.Infrastructure.Desktop;
using Loregrove.Infrastructure.Docling;
using Loregrove.Infrastructure.LocalFiles;
using Loregrove.Infrastructure.Sqlite;
using Loregrove.Infrastructure.Search;
using Microsoft.Extensions.Logging;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Loregrove.Desktop;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
#if WINDOWS
        var webViewData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Loregrove",
            "WebView2");
        Directory.CreateDirectory(webViewData);
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", webViewData);
#endif

        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts => fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular"));

        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddFluentUIComponents();
        builder.Services.AddSingleton<ILibraryClient, LibraryClient>();
        builder.Services.AddSingleton<LibraryImportCoordinator>();
        builder.Services.AddSingleton<ISearchClient, SearchClient>();
        builder.Services.AddSingleton<IKnowledgeClient, KnowledgeClient>();
        builder.Services.AddSingleton<IReviewClient, ReviewClient>();
        builder.Services.AddSingleton<IAskClient, AskClient>();
        builder.Services.AddSingleton<ILoregroveClient, LoregroveClient>();
        builder.Services.AddSingleton<IDesktopPlatform, MauiDesktopPlatform>();
        builder.Services.AddSingleton<IDesktopDropAdapter, UnavailableDesktopDropAdapter>();
        builder.Services.AddSingleton<ISecretStore, UnavailableSecretStore>();
        builder.Services.AddLoregroveParsing();
        builder.Services.AddLoregroveDocling(configuration =>
            configuration.DeveloperPackOverridePath =
                Environment.GetEnvironmentVariable("LOREGROVE_DOCLING_PACK"));

        var libraryPaths = new LocalLibraryPaths(Path.Combine(FileSystem.AppDataDirectory, "Library"));
        builder.Services.AddSingleton<ILibraryPaths>(libraryPaths);
        builder.Services.AddSingleton<ILibraryDirectoryInitializer, LocalLibraryInitializer>();
        builder.Services.AddSingleton<IObjectStore, LocalObjectStore>();
        builder.Services.AddSingleton<IArtifactStore, LocalArtifactStore>();
        builder.Services.AddLoregroveSqlite(libraryPaths.Database);
        builder.Services.AddLoregroveSearch();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        var app = builder.Build();
        using (var scope = app.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ILibraryInitializer>()
                .InitializeAsync(CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }

        return app;
    }
}
