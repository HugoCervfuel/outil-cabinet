using System.Text;

namespace Cabinet.Diagnostic.Core;

/// <summary>
/// Texte système (version de Windows, d'Outlook, nom d'antivirus) nettoyé avant affichage.
/// Seul type de chaîne autorisé dans les résultats du diagnostic : il ne doit jamais porter de donnée client.
/// </summary>
public readonly record struct SafeText
{
    private const int MaxLength = 64;
    private const string AllowedPunctuation = ".,()-_/+:";

    private readonly string? _value;

    private SafeText(string value) => _value = value;

    public string Value => _value ?? "";

    public bool IsEmpty => Value.Length == 0;

    public static SafeText Empty => default;

    public static SafeText From(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return Empty;
        }

        var builder = new StringBuilder(raw.Length);
        var pendingSpace = false;
        foreach (var c in raw)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (!IsAllowed(c))
            {
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        var text = builder.ToString();
        if (text.Length > MaxLength)
        {
            text = text[..MaxLength].TrimEnd();
        }

        return new SafeText(text);
    }

    private static bool IsAllowed(char c)
        => (char.IsLetterOrDigit(c) && c <= 'ɏ') || AllowedPunctuation.Contains(c);

    public override string ToString() => Value;
}
