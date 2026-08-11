namespace Loregrove.Desktop;

public partial class App : Microsoft.Maui.Controls.Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState) =>
        new(new MainPage()) { Title = "Loregrove" };
}
