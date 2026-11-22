namespace Cabinet.Diagnostic.Core;

/// <summary>
/// Distingue un refus d'Outlook (sa garde de sécurité) d'une simple erreur technique lors de la lecture protégée.
/// </summary>
public static class GuardedErrorClassifier
{
    private const int EAbort = unchecked((int)0x80004004);        // « Refuser » dans l'alerte d'Outlook
    private const int EAccessDenied = unchecked((int)0x80070005); // refus imposé par une règle

    public static GuardedAccessOutcome Classify(int hresult) => hresult switch
    {
        EAbort or EAccessDenied => GuardedAccessOutcome.Denied,
        _ => GuardedAccessOutcome.Failed,
    };
}
