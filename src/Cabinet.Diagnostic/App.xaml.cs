using System.Windows;
using System.Windows.Threading;

namespace Cabinet.Diagnostic;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Aucun détail technique affiché ni enregistré : il pourrait contenir des informations du poste.
        MessageBox.Show(
            "Une erreur inattendue s'est produite. Vous pouvez relancer le diagnostic.",
            "Diagnostic de l'outil de cabinet",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }
}
