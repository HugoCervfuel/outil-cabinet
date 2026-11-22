using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Cabinet.Core;

namespace Cabinet.App;

/// <summary>« Ma journée » : relire, corriger, valider et copier pour Kleos.</summary>
internal sealed class RecapWindow : Window
{
    private static readonly ActivityKind[] Kinds = Enum.GetValues<ActivityKind>();

    private readonly Shell _shell;
    private readonly DateOnly _date;
    private readonly IReadOnlyList<Dossier> _dossiers;
    private readonly List<LineEditor> _editors = new();
    private readonly StackPanel _rows = new() { Spacing = 6 };
    private readonly TextBlock _total = Ui.Text("", 14, bold: true);
    private readonly TextBlock _status = Ui.Text("", 12, brush: "Ink3");

    public RecapWindow(Shell shell, DateOnly date)
    {
        _shell = shell;
        _date = date;
        _dossiers = shell.Session.ActiveDossiers;
        Title = "Ma journée — Outil de cabinet";
        Width = 980;
        Height = 620;
        MinWidth = 760;
        MinHeight = 400;
        Background = Ui.Brush("Paper");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var validated = shell.Session.ValidatedLines(date);
        var lines = validated ?? shell.Session.BuildRecap(date);
        foreach (var line in lines)
        {
            AddEditor(line);
        }

        _status.Text = validated is null
            ? "Relisez et corrigez si besoin, puis validez. Rien n'est envoyé nulle part."
            : "Journée déjà validée. Vous pouvez encore la corriger et la valider à nouveau.";

        var header = Ui.Column(4,
            new TextBlock { Text = Format.Day(date), Classes = { "title" } },
            _status);

        var columns = new Grid
        {
            ColumnDefinitions = LineEditor.Columns(),
            Margin = new Thickness(12, 0, 12, 4),
        };
        string[] titles = { "Dossier", "Type", "Durée", "Description", "" };
        for (var i = 0; i < titles.Length; i++)
        {
            var title = Ui.Label(titles[i]);
            Grid.SetColumn(title, i);
            columns.Children.Add(title);
        }

        var addLine = Ui.Secondary("+ Ajouter une ligne", () =>
        {
            AddEditor(new RecapLine(null, ActivityKind.Autre, TimeSpan.FromMinutes(15), ""));
            UpdateTotal();
        });

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(20, 12, 20, 18) };
        var left = Ui.Row(16, Ui.Text("Total", 14, bold: true), _total, addLine);
        var right = Ui.Row(8,
            Ui.Secondary("Copier tout pour Kleos", () => _ = CopyAsync(_shell.Session.KleosText(_date, CurrentLines()), "Tout est copié. Collez dans Kleos ou dans un tableur.")),
            Ui.Primary("Valider la journée", Validate));
        Grid.SetColumn(right, 1);
        footer.Children.Add(left);
        footer.Children.Add(right);

        var body = Ui.Card(new DockPanel
        {
            Children =
            {
                Top(columns),
                new ScrollViewer { Content = _rows },
            },
        }, new Thickness(20, 0, 20, 0));

        Content = new DockPanel
        {
            Children =
            {
                Top(new Border { Padding = new Thickness(20, 18, 20, 14), Child = header }),
                Bottom(footer),
                body,
            },
        };

        UpdateTotal();
    }

    private static Control Top(Control c)
    {
        DockPanel.SetDock(c, Dock.Top);
        return c;
    }

    private static Control Bottom(Control c)
    {
        DockPanel.SetDock(c, Dock.Bottom);
        return c;
    }

    private void AddEditor(RecapLine line)
    {
        var editor = new LineEditor(line, _dossiers, onChanged: UpdateTotal, onCopy: l =>
        {
            var dossier = _dossiers.FirstOrDefault(d => d.Id == l.DossierId);
            _ = CopyAsync(KleosFormatter.Line(_date, l, dossier), "Ligne copiée.");
        }, onRemove: e =>
        {
            _editors.Remove(e);
            _rows.Children.Remove(e.View);
            UpdateTotal();
        });
        _editors.Add(editor);
        _rows.Children.Add(editor.View);
    }

    private IReadOnlyList<RecapLine> CurrentLines() => _editors.Select(e => e.Line).Where(l => l.Duration > TimeSpan.Zero).ToList();

    private void UpdateTotal()
    {
        var total = CurrentLines().Aggregate(TimeSpan.Zero, (t, l) => t + l.Duration);
        _total.Text = Durations.Format(total);
    }

    private void Validate()
    {
        if (_editors.Any(e => !e.IsValid))
        {
            _status.Text = "Une durée n'est pas lisible. Écrivez-la par exemple « 1 h 40 », « 1:40 » ou « 45 ».";
            _status.Foreground = Ui.Brush("Warn");
            return;
        }

        _shell.Session.ValidateDay(_date, CurrentLines());
        _status.Text = "Journée validée. Pensez à reporter les temps dans Kleos (bouton « Copier tout pour Kleos »).";
        _status.Foreground = Ui.Brush("Run");
        _shell.Refresh();
    }

    private async Task CopyAsync(string text, string confirmation)
    {
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return;
        }

        await clipboard.SetTextAsync(text);
        _status.Text = confirmation;
        _status.Foreground = Ui.Brush("Ink2");
    }

    /// <summary>Une ligne éditable du récapitulatif.</summary>
    private sealed class LineEditor
    {
        private readonly IReadOnlyList<Dossier> _dossiers;
        private readonly ComboBox _dossier = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly ComboBox _kind = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly TextBox _duration = new() { HorizontalContentAlignment = HorizontalAlignment.Right };
        private readonly TextBox _description = new();

        public LineEditor(RecapLine line, IReadOnlyList<Dossier> dossiers, Action onChanged, Action<RecapLine> onCopy, Action<LineEditor> onRemove)
        {
            _dossiers = dossiers;
            _dossier.ItemsSource = dossiers.Select(d => d.Name).Prepend("Non attribué").ToList();
            var index = dossiers.ToList().FindIndex(d => d.Id == line.DossierId);
            _dossier.SelectedIndex = index + 1;
            _kind.ItemsSource = Kinds.Select(ActivityClassifier.Label).ToList();
            _kind.SelectedIndex = Array.IndexOf(Kinds, line.Kind);
            _duration.Text = Durations.Format(line.Duration);
            _description.Text = line.Description;

            _duration.TextChanged += (_, _) =>
            {
                if (IsValid)
                {
                    _duration.ClearValue(TextBox.BorderBrushProperty);
                }
                else
                {
                    _duration.BorderBrush = Ui.Brush("Warn");
                }

                onChanged();
            };

            var copy = new Button { Content = "Copier", Classes = { "secondary" }, FontSize = 12 };
            copy.Click += (_, _) => onCopy(Line);
            var remove = new Button { Content = "✕", Classes = { "flat" }, Foreground = Ui.Brush("Ink3") };
            ToolTip.SetTip(remove, "Supprimer la ligne");
            remove.Click += (_, _) => onRemove(this);

            var grid = new Grid { ColumnDefinitions = Columns(), Margin = new Thickness(0, 2) };
            var actions = Ui.Row(4, copy, remove);
            Control[] cells = { _dossier, _kind, _duration, _description, actions };
            for (var i = 0; i < cells.Length; i++)
            {
                cells[i].Margin = new Thickness(0, 0, 8, 0);
                cells[i].VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(cells[i], i);
                grid.Children.Add(cells[i]);
            }

            View = new Border { Padding = new Thickness(12, 4), Child = grid };
        }

        public Control View { get; }

        public bool IsValid => Durations.Parse(_duration.Text) is not null;

        public RecapLine Line => new(
            _dossier.SelectedIndex > 0 ? _dossiers[_dossier.SelectedIndex - 1].Id : null,
            _kind.SelectedIndex >= 0 ? Kinds[_kind.SelectedIndex] : ActivityKind.Autre,
            Durations.Parse(_duration.Text) ?? TimeSpan.Zero,
            _description.Text ?? "");

        public static ColumnDefinitions Columns() => new("2*,1.7*,100,3*,Auto");
    }
}
