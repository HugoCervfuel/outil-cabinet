using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Cabinet.Core;

namespace Cabinet.App;

/// <summary>La pastille : toujours visible, discrète, ouvre le panneau au clic.</summary>
public partial class PastilleWindow : Window
{
    private static readonly BoxShadows NormalShadow = BoxShadows.Parse("0 6 18 0 #3A142A3F");
    private static readonly BoxShadows ReminderShadow = BoxShadows.Parse("0 0 0 3 #80B8862F, 0 6 18 0 #3A142A3F");

    private readonly Shell? _shell;
    private bool _movedByUser;
    private Size _placedSize;

    public PastilleWindow()
    {
        InitializeComponent();
    }

    internal PastilleWindow(Shell shell) : this()
    {
        _shell = shell;
        OpenButton.Click += (_, _) => _shell.TogglePanel();
        PauseButton.Click += (_, _) => _shell.TogglePause();
        Grip.PointerPressed += OnGripPressed;
        LayoutUpdated += (_, _) => PlaceInCorner();
    }

    public void Refresh(PastilleState state, bool reminder)
    {
        LabelText.Text = state.Label;
        ElapsedText.Text = state.Status is PastilleStatus.Recording or PastilleStatus.Unrecognized ? Format.Clock(state.CurrentElapsed) : "";
        DayText.Text = "Jour " + Durations.Format(state.DayTotal);
        PauseButton.Content = state.Status == PastilleStatus.Paused ? "▶" : "❚❚";
        ToolTip.SetTip(PauseButton, state.Status == PastilleStatus.Paused ? "Reprendre" : "Pause");
        Dot.Fill = (IBrush)this.FindResource(state.Status switch
        {
            PastilleStatus.Recording => "Run",
            PastilleStatus.Paused => "Amber",
            _ => "Grey",
        })!;
        Pill.BoxShadow = reminder ? ReminderShadow : NormalShadow;
        ToolTip.SetTip(OpenButton, reminder ? "Pensez à valider votre journée" : "Ouvrir le panneau");
    }

    private void OnGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _movedByUser = true;
            BeginMoveDrag(e);
        }
    }

    /// <summary>
    /// En bas à droite de l'écran, au-dessus de la barre des tâches ou du Dock. Le bord droit reste fixe quand
    /// le nom du dossier change de longueur, jusqu'à ce que l'utilisateur déplace la pastille.
    /// </summary>
    private void PlaceInCorner()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary ?? Screens.All.FirstOrDefault();
        if (_movedByUser || Bounds.Width <= 0 || Bounds.Size == _placedSize || screen is null)
        {
            return;
        }

        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var width = (int)Math.Ceiling(Bounds.Width * scale);
        var height = (int)Math.Ceiling(Bounds.Height * scale);
        Position = new PixelPoint(area.Right - width - (int)(8 * scale), area.Bottom - height - (int)(8 * scale));
        _placedSize = Bounds.Size;
    }
}
