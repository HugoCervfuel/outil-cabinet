using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Cabinet.Ai;
using Cabinet.Core;
using Cabinet.Mail;

namespace Cabinet.App;

/// <summary>
/// « Courrier » : pour chaque e-mail déposé, le dossier, ce qu'il faut faire et un brouillon de réponse,
/// préparés par l'IA locale. L'avocat relit, corrige et copie la réponse : l'outil n'envoie rien.
/// </summary>
internal sealed class CourrierWindow : Window
{
    private static readonly Uri OllamaDownload = new("https://ollama.com/download");

    private readonly Shell _shell;
    private readonly StackPanel _list = new() { Spacing = 4 };
    private readonly ContentControl _detail = new();
    private readonly StackPanel _aiBar = new() { Spacing = 8 };
    private readonly HashSet<Guid> _analyzing = new();
    private Guid? _selected;
    private bool _queueRunning;

    public CourrierWindow(Shell shell)
    {
        _shell = shell;
        Title = "Courrier — Outil de cabinet";
        Width = 1100;
        Height = 720;
        MinWidth = 820;
        MinHeight = 480;
        Background = Ui.Brush("Paper");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var header = Ui.Column(4,
            new TextBlock { Text = "Courrier", Classes = { "title" } },
            Ui.Text("Déposez un e-mail : l'outil retrouve le dossier, dit ce qu'il faut faire et prépare une réponse. Vous relisez et validez ; rien n'est envoyé par l'outil.", 13, brush: "Ink2"));

        var actions = Ui.Row(8,
            Ui.Primary("Ouvrir un e-mail…", () => _ = PickFilesAsync()),
            Ui.Secondary("Coller un e-mail", ShowPasteDialog));

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(20, 18, 20, 10) };
        Grid.SetColumn(actions, 1);
        actions.VerticalAlignment = VerticalAlignment.Center;
        top.Children.Add(header);
        top.Children.Add(actions);

        var dropHint = Ui.Text("Astuce : glissez un e-mail depuis Outlook sur votre Bureau, puis déposez le fichier ici.", 12, brush: "Ink3");
        dropHint.Margin = new Thickness(20, 0, 20, 8);

        var listCard = Ui.Card(new ScrollViewer { Content = _list }, new Thickness(0, 0, 12, 0));
        listCard.Padding = new Thickness(8);
        var detailCard = Ui.Card(new ScrollViewer { Content = _detail }, new Thickness(0));

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("360,*"), Margin = new Thickness(20, 0, 20, 20) };
        Grid.SetColumn(detailCard, 1);
        body.Children.Add(listCard);
        body.Children.Add(detailCard);

        var aiCard = new Border { Margin = new Thickness(20, 0, 20, 10), Child = _aiBar };

        Content = new DockPanel
        {
            Children =
            {
                Dock(top, Avalonia.Controls.Dock.Top),
                Dock(aiCard, Avalonia.Controls.Dock.Top),
                Dock(dropHint, Avalonia.Controls.Dock.Top),
                body,
            },
        };

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = DragDropEffects.Copy);

        Opened += async (_, _) =>
        {
            await RefreshAiStatusAsync();
            QueuePendingAnalyses();
        };

        RefreshList();
    }

    private static Control Dock(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    private MailDesk Desk => _shell.Desk;

    // ---- Dépôt des e-mails -------------------------------------------------------------------------

    private async Task PickFilesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choisir un ou plusieurs e-mails",
            AllowMultiple = true,
            FileTypeFilter = new[] { new FilePickerFileType("E-mails") { Patterns = new[] { "*.eml", "*.msg" } } },
        });
        AddFiles(files.Select(f => f.TryGetLocalPath()).OfType<string>());
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles() ?? Array.Empty<IStorageItem>();
        AddFiles(files.Select(f => f.TryGetLocalPath()).OfType<string>());
    }

    private void AddFiles(IEnumerable<string> paths)
    {
        var refused = 0;
        Guid? last = null;
        foreach (var path in paths)
        {
            try
            {
                last = Desk.Add(MailReader.FromFile(path)).Id;
            }
            catch (Exception)
            {
                refused++;
            }
        }

        if (refused > 0)
        {
            ShowNotice($"{refused} fichier(s) n'ont pas pu être lus. Seuls les e-mails .eml et .msg sont acceptés.");
        }

        AfterAdd(last);
    }

    private void ShowPasteDialog()
    {
        var box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 320,
            PlaceholderText = "Dans Outlook, ouvrez l'e-mail, sélectionnez tout (Cmd+A ou Ctrl+A), copiez, puis collez ici.",
        };
        var dialog = new Window
        {
            Title = "Coller un e-mail",
            Width = 640,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            Background = Ui.Brush("Paper"),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        dialog.Content = new Border
        {
            Padding = new Thickness(20),
            Child = Ui.Column(12,
                Ui.Text("Collez le texte de l'e-mail, avec ses lignes « De : » et « Objet : » si possible.", 13, brush: "Ink2"),
                box,
                Ui.Row(8,
                    Ui.Primary("Analyser", () =>
                    {
                        if (!string.IsNullOrWhiteSpace(box.Text))
                        {
                            AfterAdd(Desk.Add(MailReader.FromPastedText(box.Text)).Id);
                            dialog.Close();
                        }
                    }),
                    Ui.Secondary("Annuler", dialog.Close))),
        };
        dialog.ShowDialog(this);
    }

    private void AfterAdd(Guid? last)
    {
        if (last is { } id)
        {
            _selected = id;
        }

        RefreshList();
        _shell.Refresh();
        QueuePendingAnalyses();
    }

    // ---- Analyse par l'IA locale -----------------------------------------------------------------

    /// <summary>Analyse, l'un après l'autre, les e-mails à traiter qui n'ont pas encore d'analyse.</summary>
    private void QueuePendingAnalyses()
    {
        if (_queueRunning)
        {
            return;
        }

        _queueRunning = true;
        _ = Task.Run(async () =>
        {
            try
            {
                while (Desk.Items.FirstOrDefault(i => i.Status == MailStatus.ATraiter && i.Analysis is null && i.AnalysisError is null) is { } next)
                {
                    await AnalyzeAsync(next.Id);
                }
            }
            finally
            {
                _queueRunning = false;
            }
        });
    }

    private async Task AnalyzeAsync(Guid id)
    {
        lock (_analyzing)
        {
            if (!_analyzing.Add(id))
            {
                return;
            }
        }

        await Dispatcher.UIThread.InvokeAsync(RefreshAll);
        try
        {
            await Desk.AnalyzeAsync(id, CancellationToken.None);
        }
        finally
        {
            lock (_analyzing)
            {
                _analyzing.Remove(id);
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                RefreshAll();
                _shell.Refresh();
            });
        }
    }

    private bool IsAnalyzing(Guid id)
    {
        lock (_analyzing)
        {
            return _analyzing.Contains(id);
        }
    }

    private async Task RefreshAiStatusAsync()
    {
        _aiBar.Children.Clear();
        if (_shell.Ai is not { } ai)
        {
            _aiBar.Children.Add(StatusLine("Run", "Mode démonstration : réponses fictives, sans IA."));
            return;
        }

        var status = await ai.GetStatusAsync(CancellationToken.None);
        if (!status.Running)
        {
            _aiBar.Children.Add(StatusLine("Warn", "L'IA locale n'est pas ouverte. Installez Ollama (gratuit), ouvrez-le, puis cliquez sur « Réessayer »."));
            _aiBar.Children.Add(Ui.Row(8,
                Ui.Secondary("Télécharger Ollama", () => _ = Launcher.LaunchUriAsync(OllamaDownload)),
                Ui.Secondary("Réessayer", () => _ = RetryAsync())));
            return;
        }

        if (!status.HasModel(ai.Model))
        {
            var progress = new ProgressBar { Minimum = 0, Maximum = 1, Width = 260, IsVisible = false };
            var label = Ui.Text("", 12, brush: "Ink2");
            Button? download = null;
            download = Ui.Primary($"Télécharger le modèle {ai.Model}", () => _ = PullAsync(ai, progress, label, download!));
            _aiBar.Children.Add(StatusLine("Amber", $"Ollama est ouvert. Il reste à télécharger le modèle d'IA « {ai.Model} » (une seule fois, plusieurs Go). Seul le modèle est téléchargé : aucune donnée du cabinet n'est envoyée."));
            _aiBar.Children.Add(Ui.Row(12, download, progress, label));
            return;
        }

        _aiBar.Children.Add(StatusLine("Run", $"IA locale prête ({ai.Model}). Tout est traité sur cet ordinateur."));
    }

    private async Task RetryAsync()
    {
        await RefreshAiStatusAsync();
        foreach (var item in Desk.Items.Where(i => i.AnalysisError is not null && i.Status == MailStatus.ATraiter))
        {
            await AnalyzeAsync(item.Id);
        }

        QueuePendingAnalyses();
    }

    private async Task PullAsync(OllamaClient ai, ProgressBar progress, TextBlock label, Button button)
    {
        button.IsEnabled = false;
        progress.IsVisible = true;
        try
        {
            await ai.PullAsync(new Progress<double>(value =>
            {
                progress.Value = value;
                label.Text = $"{value:P0}";
            }), CancellationToken.None);
            await RetryAsync();
        }
        catch (Exception ex)
        {
            label.Text = "Téléchargement interrompu : " + ex.Message;
            button.IsEnabled = true;
        }
    }

    private static Control StatusLine(string brush, string text)
    {
        var dot = new Avalonia.Controls.Shapes.Ellipse { Width = 9, Height = 9, Fill = Ui.Brush(brush), VerticalAlignment = VerticalAlignment.Center };
        var line = Ui.Text(text, 12, brush: "Ink2");
        line.Margin = new Thickness(8, 0, 0, 0);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(line, 1);
        grid.Children.Add(dot);
        grid.Children.Add(line);
        return grid;
    }

    private void ShowNotice(string text)
    {
        _aiBar.Children.Insert(0, StatusLine("Warn", text));
    }

    // ---- Liste et détail --------------------------------------------------------------------------

    private void RefreshAll()
    {
        RefreshList();
    }

    private void RefreshList()
    {
        _list.Children.Clear();
        var items = Desk.Items;
        if (items.Count == 0)
        {
            _list.Children.Add(Ui.Text("Aucun e-mail pour l'instant. Ouvrez ou collez un e-mail pour commencer.", 13, brush: "Ink3"));
        }

        _selected ??= items.FirstOrDefault(i => i.Status == MailStatus.ATraiter)?.Id ?? items.FirstOrDefault()?.Id;
        foreach (var item in items)
        {
            _list.Children.Add(ListRow(item));
        }

        RefreshDetail();
    }

    private Control ListRow(MailItem item)
    {
        var dossier = _shell.Session.Dossiers.FirstOrDefault(d => d.Id == item.DossierId);
        var urgencyBrush = item.Analysis?.Urgency switch
        {
            Urgency.Haute => "Warn",
            Urgency.Normale => "Amber",
            Urgency.Basse => "Grey",
            _ => "Line",
        };

        var title = Ui.Text(dossier?.Name ?? "Dossier ?", 13, bold: true, brush: dossier is null ? "Ink3" : "Ink");
        var subject = Ui.Text($"{item.Message.FromName} · {item.Message.Subject}", 12, brush: "Ink2");
        subject.TextWrapping = TextWrapping.NoWrap;
        subject.TextTrimming = TextTrimming.CharacterEllipsis;
        var state = IsAnalyzing(item.Id)
            ? "Analyse en cours…"
            : item.Status == MailStatus.Traite ? "Traité" : item.Analysis?.Action ?? (item.AnalysisError is not null ? "Analyse impossible" : "En attente d'analyse");
        var action = Ui.Text(state, 12, brush: item.Status == MailStatus.Traite ? "Ink3" : "Ink");
        action.TextWrapping = TextWrapping.NoWrap;
        action.TextTrimming = TextTrimming.CharacterEllipsis;

        var dot = new Avalonia.Controls.Shapes.Ellipse { Width = 8, Height = 8, Fill = Ui.Brush(urgencyBrush), Margin = new Thickness(0, 5, 8, 0), VerticalAlignment = VerticalAlignment.Top };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var texts = Ui.Column(2, title, subject, action);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(dot);
        grid.Children.Add(texts);

        var row = new Border
        {
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(8),
            Background = item.Id == _selected ? Ui.Brush("Paper") : Brushes.Transparent,
            Opacity = item.Status == MailStatus.Traite ? 0.6 : 1,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = grid,
        };
        row.PointerPressed += (_, _) =>
        {
            _selected = item.Id;
            RefreshList();
        };
        return row;
    }

    private void RefreshDetail()
    {
        var item = Desk.Items.FirstOrDefault(i => i.Id == _selected);
        if (item is null)
        {
            _detail.Content = Ui.Text("Sélectionnez un e-mail à gauche.", 13, brush: "Ink3");
            return;
        }

        var dossiers = _shell.Session.ActiveDossiers;
        var dossierChoice = new ComboBox { MinWidth = 260 };
        dossierChoice.ItemsSource = dossiers.Select(d => d.Name).Prepend("Dossier non identifié").ToList();
        dossierChoice.SelectedIndex = dossiers.ToList().FindIndex(d => d.Id == item.DossierId) + 1;
        dossierChoice.SelectionChanged += (_, _) =>
        {
            var chosen = dossierChoice.SelectedIndex > 0 ? dossiers[dossierChoice.SelectedIndex - 1] : null;
            if (chosen?.Id == item.DossierId)
            {
                return;
            }

            Desk.SetDossier(item.Id, chosen?.Id);
            if (chosen is not null && item.Message.FromAddress.Length > 0)
            {
                // Les prochains e-mails de cet expéditeur seront rangés dans ce dossier.
                _shell.Session.AddAddress(chosen.Id, item.Message.FromAddress);
            }

            RefreshList();
        };

        var panel = Ui.Column(14);
        var meta = $"De {item.Message.FromName}" + (item.Message.FromAddress.Length > 0 ? $" <{item.Message.FromAddress}>" : "")
                   + (item.Message.Date is { } date ? $" · {date.ToLocalTime():dd/MM/yyyy HH:mm}" : "");
        panel.Children.Add(Ui.Column(4, Ui.Text(item.Message.Subject, 17, bold: true), Ui.Text(meta, 12, brush: "Ink2")));
        panel.Children.Add(Ui.Row(10, Ui.Text("Dossier", 13, brush: "Ink2"), dossierChoice));

        if (IsAnalyzing(item.Id))
        {
            panel.Children.Add(Ui.Row(10, new ProgressBar { IsIndeterminate = true, Width = 120 }, Ui.Text("L'IA locale lit l'e-mail…", 13, brush: "Ink2")));
        }
        else if (item.Analysis is { } analysis)
        {
            var urgency = analysis.Urgency switch { Urgency.Haute => "Urgent", Urgency.Basse => "Peu urgent", _ => "Normal" };
            var deadline = analysis.Deadline is { } d ? $" · échéance le {d.ToString("d MMMM yyyy", Format.French)}" : "";
            panel.Children.Add(Section("À faire", Ui.Text(analysis.Action, 15, bold: true)));
            panel.Children.Add(Ui.Text($"{urgency}{deadline}", 12, brush: analysis.Urgency == Urgency.Haute ? "Warn" : "Ink2"));
            panel.Children.Add(Section("Résumé", Ui.Text(analysis.Summary, 13)));

            var draft = new TextBox { Text = item.Draft, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 200 };
            draft.LostFocus += (_, _) =>
            {
                if (draft.Text != item.Draft)
                {
                    Desk.UpdateDraft(item.Id, draft.Text ?? "");
                }
            };
            var copied = Ui.Text("", 12, brush: "Run");
            panel.Children.Add(Section("Brouillon de réponse", Ui.Column(8,
                Ui.Text("Les passages entre crochets [ ] sont à compléter.", 12, brush: "Ink3"),
                draft,
                Ui.Row(8,
                    Ui.Primary("Copier la réponse", () => _ = CopyAsync(draft.Text ?? "", copied)),
                    StatusButton(item),
                    Ui.Secondary("Relancer l'analyse", () => _ = AnalyzeAsync(item.Id))),
                copied)));
        }
        else
        {
            panel.Children.Add(Ui.Text(item.AnalysisError ?? "En attente d'analyse par l'IA locale.", 13, brush: item.AnalysisError is null ? "Ink2" : "Warn"));
            panel.Children.Add(Ui.Secondary("Analyser maintenant", () => _ = AnalyzeAsync(item.Id)));
        }

        panel.Children.Add(new Expander
        {
            Header = "Voir l'e-mail reçu",
            Content = new SelectableTextBlock
            {
                Text = item.Message.Body + (item.Message.Attachments.Count > 0 ? "\n\nPièces jointes : " + string.Join(", ", item.Message.Attachments) : ""),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
            },
            HorizontalAlignment = HorizontalAlignment.Stretch,
        });

        panel.Children.Add(Ui.Row(8,
            item.Analysis is null ? StatusButton(item) : new Control(),
            Ui.Secondary("Retirer de la liste", () => { Desk.Remove(item.Id); _selected = null; RefreshList(); _shell.Refresh(); })));

        _detail.Content = panel;
    }

    private static Control Section(string label, Control content) => Ui.Column(4, Ui.Label(label), content);

    private Button StatusButton(MailItem item)
        => item.Status == MailStatus.ATraiter
            ? Ui.Secondary("Marquer comme traité", () => { Desk.SetStatus(item.Id, MailStatus.Traite); _selected = null; RefreshList(); _shell.Refresh(); })
            : Ui.Secondary("Remettre à traiter", () => { Desk.SetStatus(item.Id, MailStatus.ATraiter); RefreshList(); _shell.Refresh(); });

    private async Task CopyAsync(string text, TextBlock confirmation)
    {
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
            confirmation.Text = "Copié. Collez-la dans votre réponse Outlook.";
        }
    }
}
