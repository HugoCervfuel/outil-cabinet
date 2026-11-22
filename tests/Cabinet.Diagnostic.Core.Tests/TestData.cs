namespace Cabinet.Diagnostic.Core.Tests;

/// <summary>Données fictives partagées par les tests.</summary>
public static class TestData
{
    /// <summary>Un rapport où tout fonctionne.</summary>
    public static DiagnosticReport Sample() => new(
        System: new SystemInfo(SafeText.From("Windows 11 Pro 23H2 (build 22631)"), true, SafeText.From("Microsoft Defender Antivirus"), AntivirusState.ActiveUpToDate),
        Window: new WindowSample(true, 42),
        IdleTime: TimeSpan.FromSeconds(3),
        OutlookPresence: new OutlookPresence(true, OfficeBitness.X64, ObjectModelGuardPolicy.NotSet),
        Outlook: new OutlookFacts(OutlookConnection.Connected, SafeText.From("16.0.0.17928"), 1234, 12),
        GuardedAccess: GuardedAccessOutcome.Succeeded,
        AlertShownByOutlook: false,
        Documents: new FolderInfo(true, true, CloudSync.None));
}
