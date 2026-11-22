using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using Cabinet.Core;
using Cabinet.Platform;

namespace Cabinet.App;

/// <summary>Chef d'orchestre de l'interface : la pastille, le panneau, les fenêtres et le relevé régulier.</summary>
internal sealed class Shell
{
    private readonly Application _application;
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly AppServices _services;
    private readonly DispatcherTimer _timer;
    private PastilleWindow? _pastille;
    private PanelWindow? _panel;
    private DossiersWindow? _dossiersWindow;
    private RecapWindow? _recapWindow;
    private SettingsWindow? _settingsWindow;
    private CourrierWindow? _courrierWindow;
    private NativeMenuItem? _pauseMenuItem;
    private int _ticks;
    private bool _reminder;

    public Shell(Application application, IClassicDesktopStyleApplicationLifetime desktop, AppServices services)
    {
        _application = application;
        _desktop = desktop;
        _services = services;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => OnTick());
    }

    public WorkSession Session => _services.Session;

    public MailDesk Desk => _services.Desk;

    public Cabinet.Ai.OllamaClient? Ai => _services.Ai;

    public void Start()
    {
        _desktop.ShutdownRequested += (_, _) => SaveQuietly();
        _desktop.Exit += (_, _) =>
        {
            SaveQuietly();
            _services.Dispose();
        };

        _pastille = new PastilleWindow(this);
        _pastille.Show();
        SetUpTrayIcon();
        OnTick();
        _timer.Start();

        if (_services.Demo && Environment.GetEnvironmentVariable("CABINET_DEMO_OPEN") is { } screen)
        {
            // Mode démonstration : ouvre directement un écran, pour les captures.
            Dispatcher.UIThread.Post(() =>
            {
                switch (screen)
                {
                    case "panel": ShowPanel(); break;
                    case "recap": ShowRecap(); break;
                    case "dossiers": ShowDossiers(); break;
                    case "reglages": ShowSettings(); break;
                    case "courrier": ShowCourrier(); break;
                }
            }, DispatcherPriority.Background);
        }
        else if (Session.Dossiers.Count == 0)
        {
            ShowDossiers();
        }
        else if (PlatformServices.NeedsAccessibilityPermission())
        {
            ShowPanel();
        }
    }

    public bool AccessibilityMissing => PlatformServices.NeedsAccessibilityPermission();

    public void TogglePanel()
    {
        if (_panel is { IsVisible: true })
        {
            _panel.Hide();
        }
        else
        {
            ShowPanel();
        }
    }

    public void ShowPanel()
    {
        _panel ??= new PanelWindow(this);
        _panel.Refresh();
        _panel.ShowNear(_pastille!);
    }

    public void TogglePause()
    {
        Session.SetPaused(!Session.Paused);
        Refresh();
    }

    public void AssignCurrent(Guid? dossierId)
    {
        Session.AssignCurrent(dossierId);
        Refresh();
    }

    public void ShowDossiers()
    {
        _panel?.Hide();
        if (_dossiersWindow is null)
        {
            _dossiersWindow = new DossiersWindow(this);
            _dossiersWindow.Closed += (_, _) => _dossiersWindow = null;
        }

        _dossiersWindow.Show();
        _dossiersWindow.Activate();
    }

    public void ShowRecap(DateOnly? date = null)
    {
        _panel?.Hide();
        _recapWindow?.Close();
        _recapWindow = new RecapWindow(this, date ?? Session.Today);
        _recapWindow.Closed += (_, _) =>
        {
            _recapWindow = null;
            _reminder = ComputeReminder();
            Refresh();
        };
        _recapWindow.Show();
        _recapWindow.Activate();
    }

    public void ShowSettings()
    {
        _panel?.Hide();
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(this);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    public void ShowCourrier()
    {
        _panel?.Hide();
        if (_courrierWindow is null)
        {
            _courrierWindow = new CourrierWindow(this);
            _courrierWindow.Closed += (_, _) => _courrierWindow = null;
        }

        _courrierWindow.Show();
        _courrierWindow.Activate();
    }

    public void OpenAccessibilitySettings() => PlatformServices.OpenAccessibilitySettings();

    public void Quit()
    {
        SaveQuietly();
        _desktop.Shutdown();
    }

    /// <summary>Après un changement de dossiers ou de réglages.</summary>
    public void Refresh()
    {
        _pastille?.Refresh(Session.Pastille, _reminder);
        if (_panel is { IsVisible: true })
        {
            _panel.Refresh();
        }

        if (_pauseMenuItem is not null)
        {
            _pauseMenuItem.Header = Session.Paused ? "Reprendre" : "Pause";
        }
    }

    private void OnTick()
    {
        try
        {
            Session.Tick();
        }
        catch (Exception)
        {
            // Une erreur ponctuelle (disque plein, fenêtre illisible) ne doit pas arrêter l'outil.
        }

        if (_ticks++ % 30 == 0)
        {
            _reminder = ComputeReminder();
        }

        Refresh();
    }

    /// <summary>Le soir, la pastille invite à valider la journée.</summary>
    private bool ComputeReminder()
    {
        var now = TimeOnly.FromDateTime(DateTime.Now);
        return now >= Session.Settings.ReminderTime
               && Session.Pastille.DayTotal >= TimeSpan.FromMinutes(5)
               && !Session.IsValidated(Session.Today);
    }

    private void SetUpTrayIcon()
    {
        _pauseMenuItem = new NativeMenuItem("Pause");
        _pauseMenuItem.Click += (_, _) => TogglePause();

        NativeMenuItem Item(string header, Action action)
        {
            var item = new NativeMenuItem(header);
            item.Click += (_, _) => action();
            return item;
        }

        var menu = new NativeMenu
        {
            Item("Ouvrir le panneau", ShowPanel),
            Item("Courrier", ShowCourrier),
            _pauseMenuItem,
            new NativeMenuItemSeparator(),
            Item("Ma journée", () => ShowRecap()),
            Item("Mes dossiers", ShowDossiers),
            Item("Réglages", ShowSettings),
            new NativeMenuItemSeparator(),
            Item("Quitter", Quit),
        };

        var tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://OutilCabinet/Assets/tray.png"))),
            ToolTipText = "Outil de cabinet",
            Menu = menu,
            IsVisible = true,
        };

        TrayIcon.SetIcons(_application, new TrayIcons { tray });
    }

    private void SaveQuietly()
    {
        try
        {
            Session.Save();
        }
        catch (Exception)
        {
            // Rien à faire de plus à la fermeture.
        }
    }
}
