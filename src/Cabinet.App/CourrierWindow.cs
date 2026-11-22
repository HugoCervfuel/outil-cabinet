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
/// L'analyse tourne dans la file de l'application (une à la fois), pas dans cette fenêtre.
/// </summary>
internal sealed class CourrierWindow : Window
{
    private static readonly Uri OllamaDownload = new("https://ollama.com/download");
    private static readonly TimeSpan DraftSaveDelay = TimeSpan.FromMilliseconds(800);

    private readonly Shell _shell;
    private readonly StackPanel _list = new() { Spacing = 4 };
    private readonly ContentControl _detail = new();
    private readonly StackPanel _aiBar = new() { Spacing = 8 };
    private readonly StackPanel _notices = new() { Spacing = 6 };
    private readonly DispatcherTimer _draftTimer;
    private Guid? _selected;
    private Guid? _shownDetail;
    private TextBox? _draftBox;
    private Guid? _draftOwner;
    private bool _detailStale;

    public CourrierWindow(Shell shell)
    {
        _shell = shell;
        Title = "Courrier — Outil de cabinet";
        Width = 1100;
        Height = 740;
        MinWidth = 820;
        MinHeight = 480;
        Background = Ui.Brush("Paper");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _draftTimer = new DispatcherTimer { Interval = DraftSaveDelay };
        _draftTimer.Tick += (_, _) => SaveDraft();

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

        var dropHint = Ui.Text("Astuce : glissez l'e-mail depuis Outlook vers un dossier de votre ordinateur, puis déposez le fichier ici. L'outil vous proposera ensuite de supprimer ce fichier.", 12, brush: "Ink3");
        dropHint.Margin = new Thickness(20, 0, 20, 8);

        var listCard = Ui.Card(new ScrollViewer { Content = _list }, new Thickness(0, 0, 12, 0));
        listCard.Padding = new Thickness(8);
        var detailCard = Ui.Card(new ScrollViewer { Content = _detail }, new Thickness(0));

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("360,*"), Margin = new Thickness(20, 0, 20, 20) };
        Grid.SetColumn(detailCard, 1);
        body.Children.Add(listCard);
        body.Children.Add(detailCard);

        var bars = new Border { Margin = new Thickness(20, 0, 20, 10), Child = Ui.Column(8, _aiBar, _notices) };

        Content = new DockPanel
        {
            Children =
            {
                Dock(top, Avalonia.Controls.Dock.Top),
                Dock(bars, Avalonia.Controls.Dock.Top),
                Dock(dropHint, Avalonia.Controls.Dock.Top),
                body,
            },
        };

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = DragDropEffects.Copy);

        Opened += async (_, _) => await RefreshAiStatusAsync();
        Closing += (_, _) => SaveDraft();

        RefreshList();
        RefreshDetail();
    }

    private static Control Dock(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    private MailDesk Desk => _shell.Desk;

    private MailAnalysisQueue Queue => _shell.Queue;

    /// <summary>Un e-mail commence ou finit d'être analysé (appelé sur le fil de l'interface).</summary>
    public void OnItemChanged(Guid id)
    {
        RefreshList();
        if (id != _selected)
        {
            return;
        }

        if (_draftBox is { IsFocused: true })
        {
            // L'avocat est en train d'écrire : on ne remplace pas la zone sous ses doigts.
            _detailStale = true;
            return;
        }

        RefreshDetail(force: true);
    }

    // ---- Dépôt des e-mails -------------------------------------------------------------------------

    private async Task PickFilesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choisir un ou plusieurs e-mails",
            AllowMultiple = true,
            FileTypeFilter = new[] { new FilePickerFileType("E-mails") { Patterns = new[] { "*.eml", "*.msg" } } },
        });
        await AddFilesAsync(files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList());
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles() ?? Array.Empty<IStorageItem>();
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count == 0)
        {
            ShowNotice("Rien n'a pu être déposé. Glissez d'abord l'e-mail depuis Outlook vers un dossier, puis déposez le fichier ici.", "Warn");
            return;
        }

        _ = AddFilesAsync(paths);
    }

    /// <summary>Lecture des fichiers hors du fil de l'interface (un e-mail mal formé ne fige pas la fenêtre).</summary>
    private async Task AddFilesAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        var results = await Task.Run(() => paths.Select(path =>
        {
            try
            {
                return (Path: path, Mail: (MailMessage?)MailReader.FromFile(path));
            }
            catch (Exception)
            {
                return (Path: path, Mail: (MailMessage?)null);
            }
        }).ToList());

        var imported = new List<string>();
        Guid? last = null;
        foreach (var (path, mail) in results)
        {
            if (mail is not null)
            {
                last = Desk.Add(mail).Id;
                imported.Add(path);
            }
        }

        var refused = results.Count - imported.Count;
        if (refused > 0)
        {
            ShowNotice($"{refused} fichier(s) n'ont pas pu être lus. Seuls les e-mails .eml et .msg de moins de 60 Mo sont acceptés.", "Warn");
        }

        if (imported.Count > 0)
        {
            OfferSourceDeletion(imported);
        }

        AfterAdd(last);
    }

    /// <summary>Le fichier déposé reste en clair sur le disque (et parfois synchronisé) : on propose de le supprimer.</summary>
    private void OfferSourceDeletion(IReadOnlyList<string> paths)
    {
        var text = Ui.Text(
            $"{paths.Count} e-mail(s) ajouté(s). L'outil en garde une copie chiffrée ; le fichier d'origine reste lisible sur votre disque, et peut être synchronisé (OneDrive, iCloud). Vous pouvez le supprimer.",
            12, brush: "Ink2");
        StackPanel? notice = null;
        var delete = Ui.Secondary("Supprimer les fichiers d'origine", () =>
        {
            var failed = 0;
            foreach (var path in paths)
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception)
                {
                    failed++;
                }
            }

            _notices.Children.Remove(notice!);
            if (failed > 0)
            {
                ShowNotice($"{failed} fichier(s) n'ont pas pu être supprimés.", "Warn");
            }
        });
        var keep = Ui.Secondary("Les garder", () => _notices.Children.Remove(notice!));
        notice = Ui.Column(6, text, Ui.Row(8, delete, keep));
        _notices.Children.Add(notice);
    }

    private void ShowPasteDialog()
    {
        var box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 320,
            PlaceholderText = "Dans Outlook, choisissez « Transférer » sur l'e-mail, sélectionnez tout le texte (en-têtes « De : » et « Objet : » compris), copiez-le, puis collez-le ici.",
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
                Ui.Text("Collez le texte de l'e-mail, avec ses lignes « De : » et « Objet : » si possible : elles aident à retrouver le dossier.", 13, brush: "Ink2"),
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
            Queue.Enqueue(id);
        }

        RefreshList();
        RefreshDetail(force: true);
        _shell.Refresh();
    }

    // ---- Analyse ------------------------------------------------------------------------------------

    private async Task RequestAnalysisAsync(MailItem item)
    {
        if (item.EditedDraft is not null && !await ConfirmAsync("Relancer l'analyse remplacera le brouillon que vous avez corrigé. Continuer ?"))
        {
            return;
        }

        Desk.ClearError(item.Id);
        Queue.Enqueue(item.Id, discardEdits: true);
        RefreshList();
        RefreshDetail(force: true);
    }

    private async Task<bool> ConfirmAsync(string question)
    {
        var answer = false;
        var dialog = new Window
        {
            Title = "Confirmation",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            Background = Ui.Brush("Paper"),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        dialog.Content = new Border
        {
            Padding = new Thickness(20),
            Child = Ui.Column(14,
                Ui.Text(question, 13),
                Ui.Row(8,
                    Ui.Primary("Continuer", () => { answer = true; dialog.Close(); }),
                    Ui.Secondary("Annuler", dialog.Close))),
        };
        await dialog.ShowDialog(this);
        return answer;
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

        _aiBar.Children.Add(StatusLine("Run", $"IA locale prête ({ai.Model}). Tout est traité sur cet ordinateur. Le modèle se change dans les Réglages."));
    }

    /// <summary>Après l'installation d'Ollama ou du modèle : les e-mails en erreur repassent dans la file.</summary>
    private async Task RetryAsync()
    {
        await RefreshAiStatusAsync();
        foreach (var item in Desk.Items.Where(i => i.AnalysisError is not null && i.Status == MailStatus.ATraiter && !string.IsNullOrWhiteSpace(i.Message.Body)))
        {
            Desk.ClearError(item.Id);
        }

        Queue.EnqueuePending();
        RefreshList();
        RefreshDetail(force: true);
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
        catch (Exception)
        {
            label.Text = "Téléchargement interrompu. Vérifiez la connexion Internet et l'espace disque, puis réessayez.";
            button.IsEnabled = true;
        }
    }

    private static Control StatusLine(string brush, string text)
    {
        var dot = new Avalonia.Controls.Shapes.Ellipse { Width = 9, Height = 9, Fill = Ui.Brush(brush), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 0, 0) };
        var line = Ui.Text(text, 12, brush: "Ink2");
        line.Margin = new Thickness(8, 0, 0, 0);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(line, 1);
        grid.Children.Add(dot);
        grid.Children.Add(line);
        return grid;
    }

    private void ShowNotice(string text, string brush)
    {
        Control? line = null;
        var close = new Button { Content = "✕", Classes = { "flat" }, Foreground = Ui.Brush("Ink3") };
        close.Click += (_, _) => _notices.Children.Remove(line!);
        line = Ui.Row(8, StatusLine(brush, text), close);
        _notices.Children.Add(line);
    }

    // ---- Liste et détail ----------------------------------------------------------------------------

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
    }

    private string StateOf(MailItem item)
    {
        if (Queue.IsAnalyzing(item.Id))
        {
            return "Analyse en cours…";
        }

        if (Queue.IsWaiting(item.Id))
        {
            return "En attente d'analyse…";
        }

        if (item.Status == MailStatus.Traite)
        {
            return "Traité";
        }

        return item.Analysis?.Action ?? (item.AnalysisError is not null ? "Analyse impossible" : "À analyser");
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
        var action = Ui.Text(StateOf(item), 12, brush: item.Status == MailStatus.Traite ? "Ink3" : "Ink");
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
            SaveDraft();
            _selected = item.Id;
            RefreshList();
            RefreshDetail(force: true);
        };
        return row;
    }

    private void SaveDraft()
    {
        _draftTimer.Stop();
        if (_draftBox is { } box && _draftOwner is { } owner)
        {
            Desk.UpdateDraft(owner, box.Text ?? "");
        }
    }

    private void RefreshDetail(bool force = false)
    {
        if (!force && _shownDetail == _selected)
        {
            return;
        }

        SaveDraft();
        _shownDetail = _selected;
        _draftBox = null;
        _draftOwner = null;
        _detailStale = false;

        var item = Desk.Items.FirstOrDefault(i => i.Id == _selected);
        if (item is null)
        {
            _detail.Content = Ui.Text("Sélectionnez un e-mail à gauche.", 13, brush: "Ink3");
            return;
        }

        var dossiers = _shell.Session.ActiveDossiers;
        var dossier = dossiers.FirstOrDefault(d => d.Id == item.DossierId);
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

            SaveDraft();
            Desk.SetDossier(item.Id, chosen?.Id);
            Queue.EnqueuePending();
            RefreshList();
            RefreshDetail(force: true);
        };

        var panel = Ui.Column(14);
        var meta = $"De {item.Message.FromName}" + (item.Message.FromAddress.Length > 0 ? $" <{item.Message.FromAddress}>" : "")
                   + (item.Message.Date is { } date ? $" · {date.ToLocalTime():dd/MM/yyyy HH:mm}" : "");
        panel.Children.Add(Ui.Column(4, Ui.Text(item.Message.Subject, 17, bold: true), Ui.Text(meta, 12, brush: "Ink2")));
        panel.Children.Add(Ui.Row(10, Ui.Text("Dossier", 13, brush: "Ink2"), dossierChoice));

        if (dossier is not null && item.Message.FromAddress.Length > 0 && !MailDesk.IsKnownSender(item.Message, dossier))
        {
            var learn = Ui.Secondary($"Ranger automatiquement les e-mails de {item.Message.FromAddress} dans ce dossier", () =>
            {
                _shell.Session.AddAddress(dossier.Id, item.Message.FromAddress);
                RefreshDetail(force: true);
            });
            learn.FontSize = 12;
            panel.Children.Add(learn);
        }

        if (MailDesk.IsUnexpectedSender(item.Message, dossier))
        {
            panel.Children.Add(Warning("Expéditeur inconnu pour ce dossier. Vérifiez son identité avant de répondre, et ne transmettez aucune information du dossier en cas de doute."));
        }

        if (Queue.IsWaiting(item.Id))
        {
            var label = Queue.IsAnalyzing(item.Id) ? "L'IA locale lit l'e-mail…" : "En attente : un autre e-mail est en cours d'analyse.";
            panel.Children.Add(Ui.Row(10, new ProgressBar { IsIndeterminate = true, Width = 120 }, Ui.Text(label, 13, brush: "Ink2")));
        }

        if (item.Analysis is { } analysis)
        {
            if (item.IsOutdated)
            {
                panel.Children.Add(Warning("Cette analyse a été faite pour un autre dossier. Relancez-la avant de répondre."));
            }

            var urgency = analysis.Urgency switch { Urgency.Haute => "Urgent", Urgency.Basse => "Peu urgent", _ => "Normal" };
            panel.Children.Add(Section("À faire", Ui.Text(analysis.Action, 15, bold: true)));
            panel.Children.Add(Ui.Text(urgency, 12, brush: analysis.Urgency == Urgency.Haute ? "Warn" : "Ink2"));
            if (analysis.Deadline is { } deadline)
            {
                panel.Children.Add(Ui.Text($"Échéance repérée par l'IA, à vérifier : {deadline.ToString("d MMMM yyyy", Format.French)}", 12, brush: "Warn"));
            }

            panel.Children.Add(Section("Résumé", Ui.Text(analysis.Summary, 13)));

            var draft = new TextBox { Text = item.Draft, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 200 };
            draft.TextChanged += (_, _) =>
            {
                _draftTimer.Stop();
                _draftTimer.Start();
            };
            draft.LostFocus += (_, _) =>
            {
                SaveDraft();
                if (_detailStale)
                {
                    RefreshDetail(force: true);
                }
            };
            _draftBox = draft;
            _draftOwner = item.Id;

            var copied = Ui.Text("", 12, brush: "Run");
            panel.Children.Add(Section("Brouillon de réponse", Ui.Column(8,
                Ui.Text("Brouillon à relire. Les passages entre crochets [ ] sont à compléter.", 12, brush: "Ink3"),
                draft,
                Ui.Row(8,
                    Ui.Primary("Copier la réponse", () => _ = CopyAsync(draft.Text ?? "", copied)),
                    StatusButton(item),
                    Ui.Secondary("Relancer l'analyse", () => _ = RequestAnalysisAsync(item))),
                copied)));
        }
        else if (!Queue.IsWaiting(item.Id))
        {
            panel.Children.Add(Ui.Text(item.AnalysisError ?? "Cet e-mail n'a pas encore été analysé.", 13, brush: item.AnalysisError is null ? "Ink2" : "Warn"));
            panel.Children.Add(Ui.Row(8, Ui.Secondary("Analyser maintenant", () => _ = RequestAnalysisAsync(item)), StatusButton(item)));
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

        panel.Children.Add(Ui.Secondary("Retirer de la liste", () =>
        {
            Desk.Remove(item.Id);
            _selected = null;
            _draftBox = null;
            _draftOwner = null;
            RefreshList();
            RefreshDetail(force: true);
            _shell.Refresh();
        }));

        _detail.Content = panel;
    }

    private static Control Section(string label, Control content) => Ui.Column(4, Ui.Label(label), content);

    private static Control Warning(string text) => new Border
    {
        Background = new SolidColorBrush(Color.Parse("#FBEBE5")),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(12, 8),
        Child = Ui.Text(text, 12, brush: "Warn"),
    };

    private Button StatusButton(MailItem item)
        => item.Status == MailStatus.ATraiter
            ? Ui.Secondary("Marquer comme traité", () =>
            {
                SaveDraft();
                Desk.SetStatus(item.Id, MailStatus.Traite);
                _selected = null;
                RefreshList();
                RefreshDetail(force: true);
                _shell.Refresh();
            })
            : Ui.Secondary("Remettre à traiter", () =>
            {
                Desk.SetStatus(item.Id, MailStatus.ATraiter);
                RefreshList();
                RefreshDetail(force: true);
                _shell.Refresh();
            });

    private async Task CopyAsync(string text, TextBlock confirmation)
    {
        SaveDraft();
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
            confirmation.Text = "Copié. Collez-la dans votre réponse Outlook.";
        }
    }
}
