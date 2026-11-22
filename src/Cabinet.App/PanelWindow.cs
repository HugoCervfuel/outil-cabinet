using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cabinet.Core;

namespace Cabinet.App;

/// <summary>Le panneau ouvert depuis la pastille : en cours, aujourd'hui, à vérifier.</summary>
internal sealed class PanelWindow : Window
{
    private const double PanelWidth = 340;

    private readonly Shell _shell;
    private readonly StackPanel _content = new() { Spacing = 0 };

    public PanelWindow(Shell shell)
    {
        _shell = shell;
        Title = "Outil de cabinet";
        Width = PanelWidth;
        SizeToContent = SizeToContent.Height;
        WindowDecorations = WindowDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };

        Content = new Border
        {
            Margin = new Thickness(12),
            CornerRadius = new CornerRadius(14),
            Background = Brushes.White,
            BorderBrush = Ui.Brush("Line"),
            BorderThickness = new Thickness(1),
            BoxShadow = BoxShadows.Parse("0 10 30 0 #38142A3F"),
            Padding = new Thickness(16, 14),
            Child = _content,
        };

        Deactivated += (_, _) => Hide();
        LayoutUpdated += (_, _) => PlaceAboveAnchor();
    }

    private Window? _anchor;
    private Size _placedSize;

    public void ShowNear(Window pastille)
    {
        _anchor = pastille;
        _placedSize = default;
        Show();
        Activate();
        PlaceAboveAnchor();
    }

    /// <summary>Au-dessus de la pastille, bord droit aligné ; recalculé quand la taille du panneau change.</summary>
    private void PlaceAboveAnchor()
    {
        if (_anchor is null || Bounds.Width <= 0 || Bounds.Size == _placedSize)
        {
            return;
        }

        var scale = _anchor.Screens.ScreenFromWindow(_anchor)?.Scaling ?? 1;
        var width = (int)Math.Ceiling(Bounds.Width * scale);
        var height = (int)Math.Ceiling(Bounds.Height * scale);
        var anchorRight = _anchor.Position.X + (int)Math.Ceiling(_anchor.Bounds.Width * scale);
        var x = Math.Max(0, anchorRight - width);
        var y = Math.Max(0, _anchor.Position.Y - height + (int)(12 * scale));
        Position = new PixelPoint(x, y);
        _placedSize = Bounds.Size;
    }

    public void Refresh()
    {
        var session = _shell.Session;
        var state = session.Pastille;
        _content.Children.Clear();

        // En cours
        _content.Children.Add(Ui.Label("En cours"));
        var dot = new Avalonia.Controls.Shapes.Ellipse
        {
            Width = 9,
            Height = 9,
            Fill = Ui.Brush(state.Status switch { PastilleStatus.Recording => "Run", PastilleStatus.Paused => "Amber", _ => "Grey" }),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var current = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var name = Ui.Text(state.Label, 14, bold: true);
        name.Margin = new Thickness(8, 0);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.TextWrapping = TextWrapping.NoWrap;
        var clock = Ui.Text(state.Status is PastilleStatus.Recording or PastilleStatus.Unrecognized ? Format.Clock(state.CurrentElapsed) : "", 14, bold: true);
        Grid.SetColumn(name, 1);
        Grid.SetColumn(clock, 2);
        current.Children.Add(dot);
        current.Children.Add(name);
        current.Children.Add(clock);
        _content.Children.Add(current);

        var chooser = new ComboBox { PlaceholderText = "Changer de dossier…", MinWidth = 180, FontSize = 12 };
        var dossiers = session.ActiveDossiers;
        chooser.ItemsSource = dossiers.Select(d => d.Name).Prepend("Aucun dossier").ToList();
        chooser.SelectionChanged += (_, _) =>
        {
            if (chooser.SelectedIndex >= 0)
            {
                _shell.AssignCurrent(chooser.SelectedIndex == 0 ? null : dossiers[chooser.SelectedIndex - 1].Id);
            }
        };
        var actions = Ui.Row(8, Ui.Secondary(session.Paused ? "▶ Reprendre" : "❚❚ Pause", _shell.TogglePause), chooser);
        actions.Margin = new Thickness(0, 10, 0, 0);
        _content.Children.Add(actions);

        if (_shell.AccessibilityMissing)
        {
            _content.Children.Add(Ui.Separator());
            var warning = Ui.Column(8,
                Ui.Text("Autorisation nécessaire", 13, bold: true, brush: "Warn"),
                Ui.Text("Pour reconnaître vos documents, macOS doit autoriser l'outil à lire le nom des fenêtres. Dans les Réglages Système, ouvrez Confidentialité et sécurité, puis Accessibilité, et activez « Outil de cabinet ».", 12, brush: "Ink2"),
                Ui.Secondary("Ouvrir les réglages", _shell.OpenAccessibilitySettings));
            _content.Children.Add(warning);
        }

        // Courrier
        _content.Children.Add(Ui.Separator());
        _content.Children.Add(Ui.Label("Courrier"));
        var pendingMail = _shell.Desk.PendingCount;
        var mailRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var mailText = Ui.Text(pendingMail == 0 ? "Aucun e-mail à traiter." : $"{pendingMail} e-mail{(pendingMail > 1 ? "s" : "")} à traiter", 13, bold: pendingMail > 0);
        var mailButton = Ui.Secondary("Ouvrir", _shell.ShowCourrier);
        Grid.SetColumn(mailButton, 1);
        mailRow.Children.Add(mailText);
        mailRow.Children.Add(mailButton);
        _content.Children.Add(mailRow);

        // Aujourd'hui
        _content.Children.Add(Ui.Separator());
        _content.Children.Add(Ui.Label("Aujourd'hui"));
        var totals = session.TodayTotals;
        if (totals.Count == 0)
        {
            _content.Children.Add(Ui.Text("Rien d'enregistré pour l'instant.", 12, brush: "Ink3"));
        }

        foreach (var (dossier, duration) in totals.Take(8))
        {
            _content.Children.Add(Ui.KeyValue(dossier?.Name ?? "Non reconnu", Durations.Format(duration)));
        }

        var totalRow = Ui.KeyValue("Total", Durations.Format(state.DayTotal), bold: true);
        totalRow.Margin = new Thickness(0, 6, 0, 0);
        _content.Children.Add(totalRow);
        var validate = Ui.Primary("Valider ma journée", () => _shell.ShowRecap());
        validate.Margin = new Thickness(0, 10, 0, 0);
        _content.Children.Add(validate);

        // Jours en attente
        var pending = session.PendingDays;
        if (pending.Count > 0)
        {
            _content.Children.Add(Ui.Separator());
            _content.Children.Add(Ui.Label("À valider"));
            foreach (var day in pending.Take(5))
            {
                var link = Ui.Secondary(Format.Day(day), () => _shell.ShowRecap(day));
                link.Margin = new Thickness(0, 0, 0, 6);
                _content.Children.Add(link);
            }
        }

        // Pied
        _content.Children.Add(Ui.Separator());
        var footer = Ui.Row(6,
            Ui.Secondary("Mes dossiers", _shell.ShowDossiers),
            Ui.Secondary("Réglages", _shell.ShowSettings),
            Ui.Secondary("Quitter", _shell.Quit));
        _content.Children.Add(footer);
    }
}
