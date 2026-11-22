using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Cabinet.Diagnostic.Core;
using Cabinet.Diagnostic.Windows;
using Microsoft.Win32;

namespace Cabinet.Diagnostic;

public partial class MainWindow : Window
{
    private static readonly TimeSpan OutlookTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan GuardedTimeout = TimeSpan.FromSeconds(120);

    private readonly DiagnosticRunner _runner = new(
        new WindowsSystemProbe(),
        new WindowsWindowProbe(),
        new OutlookComProbe(),
        new WindowsFolderProbe(),
        OutlookTimeout,
        GuardedTimeout);

    private DiagnosticReport? _report;

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "Vérification en cours…");
        try
        {
            _report = await Task.Run(_runner.RunAutomaticChecks);
            ShowVerdicts(_report);
            QuestionPanel.Visibility = Visibility.Collapsed;
            GuardedCard.Visibility = _report.CanRunGuardedTest ? Visibility.Visible : Visibility.Collapsed;
            ReportCard.Visibility = Visibility.Visible;
            ReportStatus.Visibility = Visibility.Collapsed;
            StartButton.Content = "Relancer le diagnostic";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnGuardedClick(object sender, RoutedEventArgs e)
    {
        if (_report is null)
        {
            return;
        }

        SetBusy(true, "Test en cours…");
        try
        {
            var current = _report;
            _report = await Task.Run(() => _runner.RunGuardedCheck(current));
            QuestionPanel.Visibility = Visibility.Visible;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnYesClick(object sender, RoutedEventArgs e) => RecordAnswer(true);

    private void OnNoClick(object sender, RoutedEventArgs e) => RecordAnswer(false);

    private void RecordAnswer(bool alertShown)
    {
        if (_report is null)
        {
            return;
        }

        _report = DiagnosticRunner.WithUserAnswer(_report, alertShown);
        ShowVerdicts(_report);
        QuestionPanel.Visibility = Visibility.Collapsed;
        GuardedCard.Visibility = Visibility.Collapsed;
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (_report is null)
        {
            return;
        }

        try
        {
            Clipboard.SetText(ReportFormatter.ToText(_report, DateTimeOffset.Now));
            ShowReportStatus("Rapport copié. Vous pouvez le coller dans un e-mail.");
        }
        catch (COMException)
        {
            ShowReportStatus("Le presse-papiers est occupé. Réessayez dans un instant.");
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_report is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            FileName = "diagnostic-outil-cabinet.txt",
            Filter = "Fichier texte (*.txt)|*.txt",
            DefaultExt = ".txt",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, ReportFormatter.ToText(_report, DateTimeOffset.Now), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            ShowReportStatus("Rapport enregistré.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowReportStatus("Impossible d'enregistrer le rapport à cet endroit. Choisissez un autre dossier.");
        }
    }

    private void ShowVerdicts(DiagnosticReport report)
    {
        VerdictsPanel.Children.Clear();
        foreach (var verdict in new[] { Verdicts.Tracking(report), Verdicts.Outlook(report), Verdicts.Documents(report) })
        {
            VerdictsPanel.Children.Add(VerdictRow(verdict));
        }

        ResultsCard.Visibility = Visibility.Visible;
    }

    private UIElement VerdictRow(Verdict verdict)
    {
        var brushKey = verdict.Status switch
        {
            CheckStatus.Ok => "OkBrush",
            CheckStatus.Warning => "WarnBrush",
            _ => "FailBrush",
        };

        var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var dot = new Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = (Brush)FindResource(brushKey),
            Margin = new Thickness(0, 6, 10, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };

        var text = new TextBlock
        {
            Text = ReportFormatter.VerdictSentence(verdict.Kind),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            Foreground = (Brush)FindResource("Ink"),
        };
        Grid.SetColumn(text, 1);

        row.Children.Add(dot);
        row.Children.Add(text);
        return row;
    }

    private void ShowReportStatus(string message)
    {
        ReportStatus.Text = message;
        ReportStatus.Visibility = Visibility.Visible;
    }

    private void SetBusy(bool busy, string message = "")
    {
        foreach (var button in new[] { StartButton, GuardedButton, YesButton, NoButton, CopyButton, SaveButton })
        {
            button.IsEnabled = !busy;
        }

        BusyText.Text = message;
        BusyText.Margin = new Thickness(6, 0, 0, 0);
        BusyText.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }
}
