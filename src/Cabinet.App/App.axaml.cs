using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Cabinet.App;

public partial class App : Application
{
    private Shell? _shell;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = AppServices.Create();
            if (services is null)
            {
                // Une autre fenêtre de l'outil est déjà ouverte.
                desktop.Shutdown();
            }
            else
            {
                _shell = new Shell(this, desktop, services);
                _shell.Start();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
