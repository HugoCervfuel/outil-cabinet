using System.Globalization;

namespace Cabinet.App;

internal static class Format
{
    public static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>« 0:35 », « 1:40 ».</summary>
    public static string Clock(TimeSpan duration)
    {
        var minutes = (int)duration.TotalMinutes;
        return $"{minutes / 60}:{minutes % 60:00}";
    }

    /// <summary>« Vendredi 20 novembre ».</summary>
    public static string Day(DateOnly date)
    {
        var text = date.ToString("dddd d MMMM", French);
        return char.ToUpper(text[0], French) + text[1..];
    }
}
