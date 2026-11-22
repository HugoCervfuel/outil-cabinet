using Avalonia;
using Avalonia.Controls;
using Cabinet.Core;

namespace Cabinet.App;

/// <summary>Réglages : exclusions, inactivité, arrondi, heure du rappel.</summary>
internal sealed class SettingsWindow : Window
{
    private readonly Shell _shell;
    private readonly TextBox _exclusions = new() { AcceptsReturn = true, Height = 120, PlaceholderText = "Un mot par ligne. Ex. : banque, doctolib" };
    private readonly NumericUpDown _idle = new() { Minimum = 1, Maximum = 60, Increment = 1, FormatString = "0", Width = 140 };
    private readonly ComboBox _rounding = new() { Width = 140 };
    private readonly TextBox _reminder = new() { Width = 140, PlaceholderText = "18:30" };
    private readonly TextBlock _status = Ui.Text("", 12, brush: "Ink3");
    private readonly ComboBox _model = new() { Width = 300 };

    private static readonly (string? Model, string Label)[] ModelChoices =
    {
        (null, "Conseillé pour cet ordinateur"),
        ("gemma3:4b", "gemma3:4b — rapide, pour tout ordinateur"),
        ("gemma3:12b", "gemma3:12b — équilibré (16 Go de mémoire)"),
        ("mistral-small3.2", "mistral-small3.2 — meilleure rédaction (Mac 32 Go)"),
    };

    private static readonly int[] RoundingChoices = { 1, 5, 6, 10, 15 };

    public SettingsWindow(Shell shell)
    {
        _shell = shell;
        Title = "Réglages — Outil de cabinet";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        Background = Ui.Brush("Paper");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var settings = shell.Session.Settings;
        _exclusions.Text = string.Join(Environment.NewLine, settings.Exclusions);
        _idle.Value = settings.IdleMinutes;
        _rounding.ItemsSource = RoundingChoices.Select(m => $"{m} min").ToList();
        _rounding.SelectedIndex = Math.Max(0, Array.IndexOf(RoundingChoices, settings.RoundingMinutes));
        _reminder.Text = settings.ReminderTime.ToString("HH:mm");
        var recommended = AppServices.ModelFor(settings with { AiModel = null });
        _model.ItemsSource = ModelChoices.Select(c => c.Model is null ? $"{c.Label} ({recommended})" : c.Label).ToList();
        _model.SelectedIndex = Math.Max(0, Array.FindIndex(ModelChoices, c => c.Model == settings.AiModel));

        Content = new Border
        {
            Padding = new Thickness(20),
            Child = Ui.Column(14,
                new TextBlock { Text = "Réglages", Classes = { "title" } },
                Ui.Card(Ui.Column(8,
                    Ui.Text("Ne jamais enregistrer", 14, bold: true),
                    Ui.Text("Les applications et pages dont le nom contient un de ces mots ne sont jamais enregistrées (ni titre, ni durée).", 12, brush: "Ink2"),
                    _exclusions), new Thickness(0)),
                Ui.Card(Ui.Column(10,
                    Line("Arrêt après une absence de (minutes)", _idle),
                    Line("Arrondi des durées", _rounding),
                    Line("Rappel pour valider la journée", _reminder)), new Thickness(0)),
                Ui.Card(Ui.Column(8,
                    Ui.Text("IA locale (courrier)", 14, bold: true),
                    Ui.Text("Un modèle plus léger répond plus vite ; un modèle plus gros rédige mieux mais demande un ordinateur puissant. Tout reste sur cet ordinateur.", 12, brush: "Ink2"),
                    _model), new Thickness(0)),
                Ui.Row(8, Ui.Primary("Enregistrer", Save), Ui.Secondary("Fermer", Close)),
                _status),
        };
    }

    private static Grid Line(string label, Control control)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var text = Ui.Text(label, 13);
        Grid.SetColumn(control, 1);
        grid.Children.Add(text);
        grid.Children.Add(control);
        return grid;
    }

    private void Save()
    {
        if (!TimeOnly.TryParse(_reminder.Text?.Replace('h', ':'), Format.French, out var reminder))
        {
            _status.Text = "Heure du rappel illisible. Écrivez-la par exemple « 18:30 ».";
            _status.Foreground = Ui.Brush("Warn");
            return;
        }

        var exclusions = (_exclusions.Text ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        _shell.Session.UpdateSettings(_shell.Session.Settings with
        {
            Exclusions = exclusions,
            IdleMinutes = (int)(_idle.Value ?? 5),
            RoundingMinutes = RoundingChoices[Math.Max(0, _rounding.SelectedIndex)],
            ReminderTime = reminder,
            AiModel = ModelChoices[Math.Max(0, _model.SelectedIndex)].Model,
        });
        _shell.ApplyAiModel();

        _status.Text = "Réglages enregistrés.";
        _status.Foreground = Ui.Brush("Run");
        _shell.Refresh();
    }
}
