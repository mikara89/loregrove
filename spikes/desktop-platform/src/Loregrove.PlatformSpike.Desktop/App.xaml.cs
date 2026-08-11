namespace Loregrove.PlatformSpike.Desktop;

public partial class App : Application
{
    public App()
    {
#if WINDOWS
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER")))
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", Path.Combine(FileSystem.AppDataDirectory, "WebView2"));
#endif
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState) =>
        new(new MainPage()) { Title = "Loregrove Platform Spike", Width = 1280, Height = 820 };
}
