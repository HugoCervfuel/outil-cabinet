using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cabinet.Core;

namespace Cabinet.App;

/// <summary>« Mes dossiers » : saisie rapide de la liste qui sert à reconnaître les dossiers.</summary>
internal sealed class DossiersWindow : Window
{
    private readonly Shell _shell;
    private readonly StackPanel _list = new() { Spacing = 6 };
    private readonly TextBox _name = new() { PlaceholderText = "Ex. : MARTIN c/ Axa" };
    private readonly TextBox _keywords = new() { PlaceholderText = "Ex. : martin, axa, sinistre 2024" };
    private readonly TextBox _reference = new() { PlaceholderText = "Facultatif" };
    private readonly TextBlock _formTitle = Ui.Text("Ajouter un dossier", 15, bold: true);
    private readonly Button _submit;
    private Guid? _editing;
    private bool _keywordsTouched;

    public DossiersWindow(Shell shell)
    {
        _shell = shell;
        Title = "Mes dossiers — Outil de cabinet";
        Width = 760;
        Height = 620;
        MinWidth = 600;
        MinHeight = 420;
        Background = Ui.Brush("Paper");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _submit = Ui.Primary("Ajouter", Submit);
        _name.TextChanged += (_, _) =>
        {
            if (!_keywordsTouched)
            {
                _keywords.Text = string.Join(", ", Dossier.SuggestKeywords(_name.Text ?? ""));
            }
        };
        _keywords.GotFocus += (_, _) => _keywordsTouched = true;

        var form = Ui.Column(10,
            _formTitle,
            Field("Nom du dossier", _name),
            Field("Mots-clés (séparés par des virgules)", _keywords),
            Ui.Text("L'outil reconnaît le dossier quand un de ces mots apparaît dans le nom d'un document, d'un e-mail ou d'une page.", 12, brush: "Ink3"),
            Field("Référence Kleos", _reference),
            Ui.Row(8, _submit, Ui.Secondary("Effacer", ResetForm)));

        var listScroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        var listCard = Ui.Card(new DockPanel
        {
            Children =
            {
                Docked(Ui.Text("Dossiers ouverts", 15, bold: true), Dock.Top),
                listScroll,
            },
        }, new Thickness(0));

        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("320,*"), Margin = new Thickness(20) };
        var formCard = Ui.Card(form, new Thickness(0, 0, 16, 0));
        formCard.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(listCard, 1);
        layout.Children.Add(formCard);
        layout.Children.Add(listCard);

        Content = new DockPanel
        {
            Children =
            {
                Docked(new Border
                {
                    Padding = new Thickness(20, 18, 20, 0),
                    Child = Ui.Column(4,
                        new TextBlock { Text = "Mes dossiers", Classes = { "title" } },
                        Ui.Text("Saisissez vos dossiers ouverts une fois : ensuite, vos temps s'y rattachent tout seuls.", 13, brush: "Ink2")),
                }, Dock.Top),
                layout,
            },
        };

        RefreshList();
    }

    private static Control Docked(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        if (dock == Dock.Top && control is TextBlock text)
        {
            text.Margin = new Thickness(0, 0, 0, 10);
        }

        return control;
    }

    private static StackPanel Field(string label, TextBox box) => Ui.Column(4, Ui.Text(label, 12, brush: "Ink2"), box);

    private void Submit()
    {
        var name = _name.Text?.Trim() ?? "";
        if (name.Length == 0)
        {
            _name.Focus();
            return;
        }

        if (_editing is { } id)
        {
            _shell.Session.UpdateDossier(id, name, _keywords.Text ?? "", _reference.Text);
        }
        else
        {
            _shell.Session.AddDossier(name, _keywords.Text ?? "", _reference.Text);
        }

        ResetForm();
        RefreshList();
        _shell.Refresh();
        _name.Focus();
    }

    private void ResetForm()
    {
        _editing = null;
        _keywordsTouched = false;
        _name.Text = "";
        _keywords.Text = "";
        _reference.Text = "";
        _formTitle.Text = "Ajouter un dossier";
        _submit.Content = "Ajouter";
    }

    private void Edit(Dossier dossier)
    {
        _editing = dossier.Id;
        _keywordsTouched = true;
        _name.Text = dossier.Name;
        _keywords.Text = string.Join(", ", dossier.Keywords);
        _reference.Text = dossier.KleosReference ?? "";
        _formTitle.Text = "Modifier le dossier";
        _submit.Content = "Enregistrer";
    }

    private void RefreshList()
    {
        _list.Children.Clear();
        var dossiers = _shell.Session.ActiveDossiers;
        if (dossiers.Count == 0)
        {
            _list.Children.Add(Ui.Text("Aucun dossier pour l'instant. Ajoutez-en un à gauche.", 13, brush: "Ink3"));
            return;
        }

        foreach (var dossier in dossiers)
        {
            var info = Ui.Column(2,
                Ui.Text(dossier.Name, 13, bold: true),
                Ui.Text(string.Join(", ", dossier.Keywords) + (dossier.KleosReference is { } r ? $"  ·  Kleos {r}" : ""), 12, brush: "Ink3"));
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 4) };
            var buttons = Ui.Row(6,
                Ui.Secondary("Modifier", () => Edit(dossier)),
                Ui.Secondary("Archiver", () =>
                {
                    _shell.Session.ArchiveDossier(dossier.Id);
                    RefreshList();
                    _shell.Refresh();
                }));
            buttons.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(buttons, 1);
            row.Children.Add(info);
            row.Children.Add(buttons);
            _list.Children.Add(row);
            _list.Children.Add(new Border { Height = 1, Background = Ui.Brush("Line") });
        }
    }
}
