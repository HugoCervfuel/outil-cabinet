namespace Cabinet.Diagnostic.Core;

/// <summary>
/// Traduit la valeur de registre <c>ObjectModelGuard</c> d'Outlook (« Accès par programme » du Centre de gestion de la confidentialité).
/// </summary>
public static class GuardPolicyDecoder
{
    public static ObjectModelGuardPolicy FromRegistryValue(int? value) => value switch
    {
        0 => ObjectModelGuardPolicy.WarnWhenAntivirusInactive,
        1 => ObjectModelGuardPolicy.AlwaysWarn,
        2 => ObjectModelGuardPolicy.NeverWarn,
        _ => ObjectModelGuardPolicy.NotSet,
    };
}
