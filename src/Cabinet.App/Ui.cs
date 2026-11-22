using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Cabinet.App;

/// <summary>Petits constructeurs d'éléments d'interface, pour des fenêtres cohérentes.</summary>
internal static class Ui
{
    public static IBrush Brush(string key)
        => Application.Current!.TryGetResource(key, Application.Current.ActualThemeVariant, out var value) && value is IBrush brush
            ? brush
            : Brushes.Black;

    public static TextBlock Label(string text) => new() { Text = text.ToUpperInvariant(), Classes = { "label" } };

    public static TextBlock Text(string text, double size = 13, bool bold = false, string? brush = null) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
        Foreground = brush is null ? Brush("Ink") : Brush(brush),
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static Border Card(Control content, Thickness? margin = null) => new()
    {
        Classes = { "card" },
        Child = content,
        Margin = margin ?? new Thickness(0, 0, 0, 12),
    };

    public static Button Primary(string text, Action onClick)
    {
        var button = new Button { Content = text, Classes = { "primary" } };
        button.Click += (_, _) => onClick();
        return button;
    }

    public static Button Secondary(string text, Action onClick)
    {
        var button = new Button { Content = text, Classes = { "secondary" } };
        button.Click += (_, _) => onClick();
        return button;
    }

    public static StackPanel Row(double spacing, params Control[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        foreach (var child in children)
        {
            panel.Children.Add(child);
        }

        return panel;
    }

    public static StackPanel Column(double spacing, params Control[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical, Spacing = spacing };
        foreach (var child in children)
        {
            panel.Children.Add(child);
        }

        return panel;
    }

    /// <summary>Une ligne « libellé ……… valeur ».</summary>
    public static Grid KeyValue(string key, string value, bool bold = false)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 2) };
        var left = Text(key, 13, bold);
        left.TextTrimming = TextTrimming.CharacterEllipsis;
        left.TextWrapping = TextWrapping.NoWrap;
        var right = Text(value, 13, bold, "Ink2");
        right.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }

    public static Border Separator() => new() { Height = 1, Background = Brush("Line"), Margin = new Thickness(0, 8) };
}
