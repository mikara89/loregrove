using Loregrove.Application.Client;
using Loregrove.Application.Platform;
using Loregrove.Application.Security;
using Loregrove.Infrastructure.Desktop;
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
        builder.Services.AddSingleton<ISearchClient, SearchClient>();
        builder.Services.AddSingleton<IKnowledgeClient, KnowledgeClient>();
        builder.Services.AddSingleton<IReviewClient, ReviewClient>();
        builder.Services.AddSingleton<IAskClient, AskClient>();
        builder.Services.AddSingleton<ILoregroveClient, LoregroveClient>();
        builder.Services.AddSingleton<IDesktopPlatform, UnavailableDesktopPlatform>();
        builder.Services.AddSingleton<IDesktopDropAdapter, UnavailableDesktopDropAdapter>();
        builder.Services.AddSingleton<ISecretStore, UnavailableSecretStore>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
