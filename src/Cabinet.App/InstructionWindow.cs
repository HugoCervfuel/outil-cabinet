using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Cabinet.Core;
using Cabinet.Documents;

namespace Cabinet.App;

/// <summary>
/// « Instruction des dossiers » : les pièces d'un dossier, lues par l'IA locale, pour en tirer une synthèse,
/// une chronologie, des réponses citant leurs sources et des premiers jets. Rien ne quitte l'ordinateur.
/// </summary>
internal sealed class InstructionWindow : Window
{
    private static readonly (DraftKind Kind, string Label)[] DraftKinds =
    {
        (DraftKind.CourrierClient, "Courrier au client"),
        (DraftKind.NoteSynthese, "Note de synthèse"),
        (DraftKind.TrameConclusions, "Trame de conclusions"),
        (DraftKind.MiseEnDemeure, "Mise en demeure"),
        (DraftKind.Libre, "Autre (précisez dans les consignes)"),
    };

    private readonly Shell _shell;
    private readonly CancellationTokenSource _closing = new();
    private readonly ComboBox _dossierChoice = new() { MinWidth = 320 };
    private readonly StackPanel _pieces = new() { Spacing = 6 };
    private readonly TextBlock _progress = Ui.Text("", 12, brush: "Ink2");
    private readonly ContentControl _summary = new();
    private readonly ContentControl _chronology = new();
    private readonly TextBox _question = new() { PlaceholderText = "Ex. : Quel montant l'expert retient-il pour les travaux ?", AcceptsReturn = false };
    private readonly StackPanel _answers = new() { Spacing = 12 };
    private readonly ComboBox _draftKind = new() { MinWidth = 260 };
    private readonly TextBox _draftInstructions = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 70, PlaceholderText = "Consignes : à qui, dans quel but, quel ton, points à aborder…" };
    private readonly TextBox _draft = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 280 };
    private readonly List<Button> _actions = new();
    private IReadOnlyList<Dossier> _dossiers = Array.Empty<Dossier>();
    private bool _busy;

    public InstructionWindow(Shell shell)
    {
        _shell = shell;
        Title = "Instruction des dossiers — Outil de cabinet";
        Width = 1180;
        Height = 780;
        MinWidth = 900;
        MinHeight = 520;
        Background = Ui.Brush("Paper");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _dossiers = shell.Session.ActiveDossiers;
        _dossierChoice.ItemsSource = _dossiers.Select(d => d.Name).ToList();
        // Par défaut, le premier dossier qui a déjà des pièces.
        var withPieces = _dossiers.ToList().FindIndex(d => shell.Instruction.Pieces(d.Id).Count > 0);
        _dossierChoice.SelectedIndex = withPieces >= 0 ? withPieces : _dossiers.Count > 0 ? 0 : -1;
        _dossierChoice.SelectionChanged += (_, _) => RefreshAll();
        _draftKind.ItemsSource = DraftKinds.Select(k => k.Label).ToList();
        _draftKind.SelectedIndex = 0;
        _question.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                _ = AskAsync();
            }
        };

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(20, 18, 20, 12) };
        var titles = Ui.Column(4,
            new TextBlock { Text = "Instruction des dossiers", Classes = { "title" } },
            Ui.Text("Déposez les pièces d'un dossier : l'IA locale les lit, puis vous aide à les résumer, à dater les faits, à répondre à vos questions et à préparer un premier jet. Tout reste sur cet ordinateur.", 13, brush: "Ink2"));
        var choice = Ui.Row(8, Ui.Text("Dossier", 13, brush: "Ink2"), _dossierChoice);
        choice.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(choice, 1);
        header.Children.Add(titles);
        header.Children.Add(choice);

        var addButton = Action(Ui.Primary("Ajouter des pièces…", () => _ = PickPiecesAsync()));
        var piecesCard = Ui.Card(new DockPanel
        {
            Children =
            {
                Top(Ui.Column(8,
                    Ui.Text("Pièces", 15, bold: true),
                    addButton,
                    Ui.Text("PDF, Word (.docx), textes, e-mails. Vous pouvez aussi les glisser dans cette fenêtre.", 12, brush: "Ink3"))),
                new ScrollViewer { Content = _pieces, Margin = new Thickness(0, 12, 0, 0) },
            },
        }, new Thickness(0, 0, 12, 0));

        var tabs = new TabControl
        {
            Items =
            {
                Tab("Synthèse", SummaryTab()),
                Tab("Chronologie", ChronologyTab()),
                Tab("Questions", QuestionTab()),
                Tab("Premier jet", DraftTab()),
            },
        };
        if (int.TryParse(Environment.GetEnvironmentVariable("CABINET_DEMO_TAB"), out var demoTab) && shell.IsDemo)
        {
            tabs.SelectedIndex = demoTab;
        }
        var workCard = Ui.Card(new DockPanel { Children = { Top(_progress), tabs } }, new Thickness(0));

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("330,*"), Margin = new Thickness(20, 0, 20, 20) };
        Grid.SetColumn(workCard, 1);
        body.Children.Add(piecesCard);
        body.Children.Add(workCard);

        Content = new DockPanel { Children = { Top(header), body } };

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            var paths = (e.DataTransfer.TryGetFiles() ?? Array.Empty<IStorageItem>()).Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
            _ = AddPiecesAsync(paths);
        });
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = DragDropEffects.Copy);
        Closed += (_, _) => _closing.Cancel();
        Opened += (_, _) => _ = ReadPendingPiecesAsync();

        RefreshAll();
    }

    private Dossier? Current => _dossierChoice.SelectedIndex >= 0 && _dossierChoice.SelectedIndex < _dossiers.Count ? _dossiers[_dossierChoice.SelectedIndex] : null;

    private InstructionDesk Desk => _shell.Instruction;

    private static Control Top(Control control)
    {
        DockPanel.SetDock(control, Dock.Top);
        return control;
    }

    private static TabItem Tab(string header, Control content)
        => new() { Header = new TextBlock { Text = header, FontSize = 15, FontWeight = FontWeight.SemiBold }, Content = content };

    private Button Action(Button button)
    {
        _actions.Add(button);
        return button;
    }

    // ---- Onglets -------------------------------------------------------------------------------------

    private Control SummaryTab() => Scroll(Ui.Column(12,
        Ui.Text("Une synthèse du dossier : faits, positions des parties, points en litige, pièces importantes, points à vérifier. Les pièces sont citées entre crochets.", 12, brush: "Ink2"),
        Ui.Row(8, Action(Ui.Primary("Rédiger la synthèse", () => _ = SummarizeAsync()))),
        _summary));

    private Control ChronologyTab() => Scroll(Ui.Column(12,
        Ui.Text("Les faits datés repérés dans les pièces, du plus ancien au plus récent, chacun avec sa pièce et sa page. À vérifier sur les pièces avant usage.", 12, brush: "Ink2"),
        Ui.Row(8, Action(Ui.Secondary("Actualiser", () => _ = ReadPendingPiecesAsync()))),
        _chronology));

    private Control QuestionTab() => Scroll(Ui.Column(12,
        Ui.Text("Posez une question sur le dossier : la réponse s'appuie uniquement sur les pièces, avec la pièce et la page.", 12, brush: "Ink2"),
        _question,
        Ui.Row(8, Action(Ui.Primary("Poser la question", () => _ = AskAsync()))),
        _answers));

    private Control DraftTab() => Scroll(Ui.Column(12,
        Ui.Text("Un premier jet à partir des notes du dossier et des pièces. Les informations manquantes apparaissent entre crochets [ ].", 12, brush: "Ink2"),
        Ui.Row(8, Ui.Text("Document", 13, brush: "Ink2"), _draftKind),
        _draftInstructions,
        Ui.Row(8,
            Action(Ui.Primary("Rédiger le premier jet", () => _ = DraftAsync())),
            Ui.Secondary("Copier", () => _ = CopyAsync(_draft.Text ?? ""))),
        _draft));

    private static Control Scroll(Control content) => new ScrollViewer { Content = content, Padding = new Thickness(0, 8, 8, 0) };

    // ---- Pièces --------------------------------------------------------------------------------------

    private async Task PickPiecesAsync()
    {
        if (Current is null)
        {
            ShowProgress("Choisissez d'abord un dossier (ou créez-le dans « Mes dossiers »).");
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Ajouter des pièces au dossier",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Pièces") { Patterns = DocumentReader.SupportedExtensions.Select(e => "*" + e).ToList() },
            },
        });
        await AddPiecesAsync(files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList());
    }

    private async Task AddPiecesAsync(IReadOnlyList<string> paths)
    {
        if (Current is not { } dossier || paths.Count == 0)
        {
            return;
        }

        ShowProgress("Lecture des fichiers…");
        var read = await Task.Run(() => paths.Select(path =>
        {
            try
            {
                return (Name: Path.GetFileName(path), Pages: DocumentReader.Read(path), Error: (string?)null);
            }
            catch (Exception ex) when (ex is NotSupportedException)
            {
                return (Name: Path.GetFileName(path), Pages: (IReadOnlyList<PiecePage>)Array.Empty<PiecePage>(), Error: ex.Message);
            }
            catch (Exception)
            {
                return (Name: Path.GetFileName(path), Pages: (IReadOnlyList<PiecePage>)Array.Empty<PiecePage>(), Error: "fichier illisible ou protégé par un mot de passe");
            }
        }).ToList());

        var refused = new List<string>();
        foreach (var (name, pages, error) in read)
        {
            if (error is null)
            {
                Desk.AddPiece(dossier.Id, name, pages);
            }
            else
            {
                refused.Add($"{name} ({error})");
            }
        }

        ShowProgress(refused.Count > 0 ? "Non ajoutées : " + string.Join(" ; ", refused) : "");
        RefreshPieces();
        await ReadPendingPiecesAsync();
    }

    /// <summary>Fait lire par l'IA les pièces pas encore lues, l'une après l'autre.</summary>
    private async Task ReadPendingPiecesAsync()
    {
        if (Current is not { } dossier || _busy)
        {
            RefreshChronology();
            return;
        }

        var pending = Desk.Pieces(dossier.Id).Where(Desk.NeedsDigest).ToList();
        if (pending.Count == 0)
        {
            RefreshChronology();
            return;
        }

        await RunAsync(async progress =>
        {
            var failed = 0;
            foreach (var piece in pending)
            {
                if (await Desk.DigestAsync(piece.Id, _closing.Token, progress) is null)
                {
                    failed++;
                }

                RefreshPieces();
            }

            return failed == 0 ? "" : $"{failed} pièce(s) n'ont pas pu être lues par l'IA. Vérifiez qu'Ollama est ouvert et que le modèle est téléchargé (fenêtre Courrier), puis cliquez sur « Actualiser ».";
        });
        RefreshChronology();
    }

    private void RefreshPieces()
    {
        _pieces.Children.Clear();
        if (Current is not { } dossier)
        {
            _pieces.Children.Add(Ui.Text("Aucun dossier. Créez-en un dans « Mes dossiers ».", 13, brush: "Ink3"));
            return;
        }

        var pieces = Desk.Pieces(dossier.Id);
        if (pieces.Count == 0)
        {
            _pieces.Children.Add(Ui.Text("Aucune pièce pour l'instant.", 13, brush: "Ink3"));
        }

        foreach (var piece in pieces.OrderBy(p => p.FileName, StringComparer.CurrentCultureIgnoreCase))
        {
            var (state, brush) = piece.LooksScanned
                ? ("PDF scanné : texte non lisible pour l'instant", "Warn")
                : Desk.NeedsDigest(piece) ? ("À lire par l'IA", "Amber") : ("Lue", "Run");
            var info = Ui.Column(2,
                Ui.Text(piece.FileName, 13, bold: true),
                Ui.Text($"{piece.Pages.Count} page(s) · {state}", 12, brush: brush));
            var remove = new Button { Content = "✕", Classes = { "flat" }, Foreground = Ui.Brush("Ink3"), VerticalAlignment = VerticalAlignment.Top };
            ToolTip.SetTip(remove, "Retirer la pièce de l'outil (le fichier d'origine n'est pas touché)");
            remove.Click += (_, _) =>
            {
                Desk.RemovePiece(dossier.Id, piece.Id);
                RefreshPieces();
                RefreshChronology();
            };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(remove, 1);
            row.Children.Add(info);
            row.Children.Add(remove);
            _pieces.Children.Add(row);
        }
    }

    // ---- Actions -------------------------------------------------------------------------------------

    private async Task SummarizeAsync()
    {
        if (Current is not { } dossier)
        {
            return;
        }

        await RunAsync(async progress =>
        {
            var result = await Desk.SummarizeAsync(dossier.Id, _closing.Token, progress);
            ShowResult(_summary, result);
            RefreshPieces();
            RefreshChronology();
            return result.IsError ? result.Text : "";
        });
    }

    private async Task AskAsync()
    {
        if (Current is not { } dossier || string.IsNullOrWhiteSpace(_question.Text) || _busy)
        {
            return;
        }

        var question = _question.Text.Trim();
        await RunAsync(async progress =>
        {
            progress.Report("Recherche dans les pièces et rédaction de la réponse…");
            var result = await Desk.AskAsync(dossier.Id, question, _closing.Token);
            if (!result.IsError)
            {
                _question.Text = "";
            }

            RefreshAnswers(result.IsError ? result : null);
            return result.IsError ? result.Text : "";
        });
    }

    private async Task DraftAsync()
    {
        if (Current is not { } dossier)
        {
            return;
        }

        var kind = DraftKinds[Math.Max(0, _draftKind.SelectedIndex)].Kind;
        var instructions = _draftInstructions.Text ?? "";
        await RunAsync(async progress =>
        {
            progress.Report("Rédaction du premier jet… (cela peut prendre une ou deux minutes)");
            var result = await Desk.DraftAsync(dossier.Id, kind, instructions, _closing.Token);
            if (!result.IsError)
            {
                _draft.Text = result.Text;
            }

            return result.IsError ? result.Text : "";
        });
    }

    /// <summary>Une action à la fois ; les boutons sont désactivés pendant que l'IA travaille.</summary>
    private async Task RunAsync(Func<IProgress<string>, Task<string>> work)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        foreach (var button in _actions)
        {
            button.IsEnabled = false;
        }

        try
        {
            var message = await work(new Progress<string>(ShowProgress));
            ShowProgress(message);
        }
        catch (OperationCanceledException)
        {
            // Fenêtre fermée pendant le travail.
        }
        finally
        {
            _busy = false;
            foreach (var button in _actions)
            {
                button.IsEnabled = true;
            }
        }
    }

    private void ShowProgress(string text)
    {
        _progress.Text = text;
        _progress.Margin = text.Length > 0 ? new Thickness(0, 0, 0, 10) : new Thickness(0);
    }

    // ---- Affichage des résultats ---------------------------------------------------------------------

    private void RefreshAll()
    {
        RefreshPieces();
        RefreshChronology();
        RefreshAnswers();
        var summary = Current is { } dossier ? Desk.Results(dossier.Id).FirstOrDefault(r => r.Title == "Synthèse du dossier") : null;
        if (summary is null)
        {
            _summary.Content = null;
        }
        else
        {
            ShowResult(_summary, summary);
        }

        _draft.Text = "";
        ShowProgress("");
        _ = ReadPendingPiecesAsync();
    }

    private void RefreshChronology()
    {
        if (Current is not { } dossier)
        {
            _chronology.Content = null;
            return;
        }

        var events = Desk.Chronology(dossier.Id);
        if (events.Count == 0)
        {
            _chronology.Content = Ui.Text("Aucun fait daté pour l'instant. Ajoutez des pièces : elles sont lues automatiquement.", 13, brush: "Ink3");
            return;
        }

        var list = Ui.Column(6);
        foreach (var line in Chronology.ToText(events).Split('\n'))
        {
            list.Children.Add(new SelectableTextBlock { Text = line, TextWrapping = TextWrapping.Wrap, FontSize = 13 });
        }

        list.Children.Add(Ui.Secondary("Copier la chronologie", () => _ = CopyAsync(Chronology.ToText(events))));
        _chronology.Content = list;
    }

    private void RefreshAnswers(InstructionResult? error = null)
    {
        _answers.Children.Clear();
        if (error is not null)
        {
            _answers.Children.Add(Ui.Text(error.Text, 13, brush: "Warn"));
        }

        if (Current is not { } dossier)
        {
            return;
        }

        foreach (var result in Desk.Results(dossier.Id).Where(r => r.Title.StartsWith("Question : ", StringComparison.Ordinal)).Take(20))
        {
            var block = Ui.Column(4,
                Ui.Text(result.Title["Question : ".Length..], 13, bold: true),
                new SelectableTextBlock { Text = result.Text, TextWrapping = TextWrapping.Wrap, FontSize = 13 },
                Ui.Text(result.At.ToLocalTime().ToString("dd/MM/yyyy HH:mm", Format.French), 11, brush: "Ink3"));
            _answers.Children.Add(new Border
            {
                Background = Ui.Brush("Paper"),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10),
                Child = block,
            });
        }
    }

    private void ShowResult(ContentControl target, InstructionResult result)
    {
        if (result.IsError)
        {
            target.Content = Ui.Text(result.Text, 13, brush: "Warn");
            return;
        }

        target.Content = Ui.Column(8,
            new SelectableTextBlock { Text = result.Text, TextWrapping = TextWrapping.Wrap, FontSize = 13 },
            Ui.Row(8,
                Ui.Secondary("Copier", () => _ = CopyAsync(result.Text)),
                Ui.Text("Rédigé le " + result.At.ToLocalTime().ToString("dd/MM/yyyy à HH:mm", Format.French) + " · à relire", 11, brush: "Ink3")));
    }

    private async Task CopyAsync(string text)
    {
        if (Clipboard is { } clipboard && text.Length > 0)
        {
            await clipboard.SetTextAsync(text);
            ShowProgress("Copié.");
        }
    }
}
