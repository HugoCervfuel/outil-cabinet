namespace Cabinet.Diagnostic.Core;

/// <summary>
/// Décode la valeur <c>productState</c> fournie par le Centre de sécurité Windows (WMI SecurityCenter2).
/// </summary>
public static class AntivirusStateDecoder
{
    public static AntivirusState Decode(int? productState)
    {
        if (productState is not { } raw)
        {
            return AntivirusState.Unknown;
        }

        var state = raw & 0xFFFFFF;
        var activity = (state >> 8) & 0xFF;
        var definitions = state & 0xFF;

        return activity switch
        {
            0x10 or 0x11 => definitions switch
            {
                0x00 => AntivirusState.ActiveUpToDate,
                0x10 => AntivirusState.ActiveOutdated,
                _ => AntivirusState.Unknown,
            },
            0x00 or 0x01 => AntivirusState.Disabled,
            _ => AntivirusState.Unknown,
        };
    }

    public static AntivirusState Combine(IEnumerable<AntivirusState> states)
    {
        var list = states.ToList();
        if (list.Count == 0)
        {
            return AntivirusState.None;
        }

        foreach (var preferred in new[] { AntivirusState.ActiveUpToDate, AntivirusState.ActiveOutdated, AntivirusState.Disabled })
        {
            if (list.Contains(preferred))
            {
                return preferred;
            }
        }

        return AntivirusState.Unknown;
    }
}
