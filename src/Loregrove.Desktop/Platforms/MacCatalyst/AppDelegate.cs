using Foundation;

namespace Loregrove.Desktop;

[Register("AppDelegate")]
public class MauiAppHost : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
