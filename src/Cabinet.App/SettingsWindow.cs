using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Cabinet.Core;
using Cabinet.MailSync;

namespace Cabinet.App;

/// <summary>Réglages : messagerie, IA locale, exclusions, inactivité, arrondi, heure du rappel.</summary>
internal sealed class SettingsWindow : Window
{
    private readonly Shell _shell;
    private readonly TextBox _exclusions = new() { AcceptsReturn = true, Height = 120, PlaceholderText = "Un mot par ligne. Ex. : banque, doctolib" };
    private readonly NumericUpDown _idle = new() { Minimum = 1, Maximum = 60, Increment = 1, FormatString = "0", Width = 140 };
    private readonly ComboBox _rounding = new() { Width = 140 };
    private readonly TextBox _reminder = new() { Width = 140, PlaceholderText = "18:30" };
    private readonly TextBlock _status = Ui.Text("", 12, brush: "Ink3");
    private readonly ComboBox _model = new() { Width = 440 };

    private readonly TextBox _mailAddress = new() { Width = 280, PlaceholderText = "Nom d'utilisateur indiqué par Bridge" };
    private readonly TextBox _mailPassword = new() { Width = 280, PasswordChar = '•', PlaceholderText = "Mot de passe indiqué par Bridge" };
    private readonly NumericUpDown _mailPort = new() { Minimum = 1, Maximum = 65535, Increment = 1, FormatString = "0", Width = 140 };
    private readonly CheckBox _mailAuto = new() { Content = "Relever automatiquement toutes les 2 minutes", IsChecked = true };
    private readonly TextBlock _mailStatus = Ui.Text("", 12, brush: "Ink3");
    private readonly Button _mailTest;

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
        Width = 580;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 760;
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

        var account = shell.Mailbox.Account;
        _mailAddress.Text = account?.Address ?? "";
        _mailPassword.Text = account?.Password ?? "";
        _mailPort.Value = account?.Port ?? ProtonBridgeSource.DefaultPort;
        _mailAuto.IsChecked = account?.AutoFetch ?? true;
        _mailTest = Ui.Secondary("Tester la connexion", () => _ = TestMailAsync());

        var mailCard = Ui.Card(Ui.Column(8,
            Ui.Text("Messagerie (Proton Mail)", 14, bold: true),
            Ui.Text("L'outil lit votre boîte de réception par Proton Mail Bridge, le logiciel de Proton installé sur cet ordinateur : vos e-mails ne passent par aucun autre service. Rien n'est marqué lu, déplacé ni supprimé.", 12, brush: "Ink2"),
            Ui.Text("Dans Bridge, cliquez sur votre compte : la partie IMAP indique le nom d'utilisateur, le mot de passe et le port à recopier ici. Ce mot de passe n'est pas celui de votre compte Proton.", 12, brush: "Ink2"),
            Line("Nom d'utilisateur", _mailAddress),
            Line("Mot de passe Bridge", _mailPassword),
            Line("Port IMAP", _mailPort),
            _mailAuto,
            _mailTest,
            _mailStatus), new Thickness(0));

        var form = Ui.Column(14,
            new TextBlock { Text = "Réglages", Classes = { "title" } },
            mailCard,
            Ui.Card(Ui.Column(8,
                Ui.Text("IA locale (courrier)", 14, bold: true),
                Ui.Text("Un modèle plus léger répond plus vite ; un modèle plus gros rédige mieux mais demande un ordinateur puissant. Tout reste sur cet ordinateur.", 12, brush: "Ink2"),
                _model), new Thickness(0)),
            Ui.Card(Ui.Column(8,
                Ui.Text("Ne jamais enregistrer", 14, bold: true),
                Ui.Text("Les applications et pages dont le nom contient un de ces mots ne sont jamais enregistrées (ni titre, ni durée).", 12, brush: "Ink2"),
                _exclusions), new Thickness(0)),
            Ui.Card(Ui.Column(10,
                Line("Arrêt après une absence de (minutes)", _idle),
                Line("Arrondi des durées", _rounding),
                Line("Rappel pour valider la journée", _reminder)), new Thickness(0)));
        form.Margin = new Thickness(20, 20, 20, 8);

        var footer = Ui.Column(6, Ui.Row(8, Ui.Primary("Enregistrer", Save), Ui.Secondary("Fermer", Close)), _status);
        footer.Margin = new Thickness(20, 8, 20, 16);
        DockPanel.SetDock(footer, Dock.Bottom);

        // Les boutons restent visibles ; le reste défile sur un petit écran.
        Content = new DockPanel { Children = { footer, new ScrollViewer { Content = form } } };

        Opened += (_, _) =>
        {
            if (Screens.ScreenFromWindow(this) is { } screen)
            {
                MaxHeight = Math.Max(420, screen.WorkingArea.Height / screen.Scaling - 60);
            }
        };
    }

    private static Grid Line(string label, Control control)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var text = Ui.Text(label, 13);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(text);
        grid.Children.Add(control);
        return grid;
    }

    /// <summary>La boîte saisie ; null si les deux champs sont vides (pas de messagerie reliée).</summary>
    private (MailAccount? Account, string? Error) ReadAccount()
    {
        var address = (_mailAddress.Text ?? "").Trim();
        var password = _mailPassword.Text ?? "";
        if (address.Length == 0 && password.Length == 0)
        {
            return (null, null);
        }

        if (address.Length == 0 || password.Length == 0)
        {
            return (null, "Indiquez le nom d'utilisateur et le mot de passe affichés par Bridge (ou videz les deux champs pour ne pas relier de messagerie).");
        }

        return (new MailAccount(address, password, (int)(_mailPort.Value ?? ProtonBridgeSource.DefaultPort), _mailAuto.IsChecked == true), null);
    }

    private async Task TestMailAsync()
    {
        var (account, error) = ReadAccount();
        if (account is null)
        {
            SetMailStatus(error ?? "Indiquez d'abord le nom d'utilisateur et le mot de passe affichés par Bridge.", "Warn");
            return;
        }

        _mailTest.IsEnabled = false;
        SetMailStatus("Connexion à Proton Mail Bridge…", "Ink3");
        var result = await Task.Run(() => _shell.Mailbox.TestAsync(account, CancellationToken.None));
        _mailTest.IsEnabled = true;
        SetMailStatus(result ?? "Connexion réussie : la boîte de réception est accessible. Cliquez sur « Enregistrer ».", result is null ? "Run" : "Warn");
    }

    private void SetMailStatus(string text, string brush)
    {
        _mailStatus.Text = text;
        _mailStatus.Foreground = Ui.Brush(brush);
    }

    private void Save()
    {
        if (!TimeOnly.TryParse(_reminder.Text?.Replace('h', ':'), Format.French, out var reminder))
        {
            _status.Text = "Heure du rappel illisible. Écrivez-la par exemple « 18:30 ».";
            _status.Foreground = Ui.Brush("Warn");
            return;
        }

        var (account, mailError) = ReadAccount();
        if (mailError is not null)
        {
            _status.Text = mailError;
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

        var mailbox = _shell.Mailbox;
        var previous = mailbox.Account;
        var message = "Réglages enregistrés.";
        if (account is not null && account != previous)
        {
            mailbox.SaveAccount(account);
            if (account.AutoFetch)
            {
                _ = _shell.SyncMailAsync();
                message = "Réglages enregistrés. Relevé de la boîte en cours : les e-mails arrivent dans l'écran Courrier.";
            }
        }
        else if (account is null && previous is { } old && (old.Address.Length > 0 || old.Password.Length > 0))
        {
            // Champs vidés : la messagerie n'est plus reliée.
            mailbox.SaveAccount(new MailAccount("", ""));
            message = "Réglages enregistrés. La messagerie n'est plus reliée.";
        }

        _status.Text = message;
        _status.Foreground = Ui.Brush("Run");
        _shell.MailboxSettingsChanged();
        _shell.Refresh();
    }
}
