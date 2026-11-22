namespace Cabinet.Diagnostic.Core;

/// <summary>
/// Enchaîne les vérifications. Une sonde qui échoue ou qui bloque donne une valeur de repli, jamais un plantage.
/// </summary>
public sealed class DiagnosticRunner(
    ISystemProbe system,
    IWindowProbe window,
    IOutlookProbe outlook,
    IFolderProbe folder,
    TimeSpan outlookTimeout,
    TimeSpan guardedTimeout)
{
    private static readonly OutlookFacts FailedFacts = new(OutlookConnection.Failed, SafeText.Empty, null, null);

    public DiagnosticReport RunAutomaticChecks()
    {
        var systemInfo = Timed(system.Read,
            new SystemInfo(SafeText.Empty, Environment.Is64BitOperatingSystem, SafeText.Empty, AntivirusState.Unknown));
        var sample = Timed(window.SampleForeground, null);
        var idle = Timed(window.IdleTime, null);
        var presence = Safe(outlook.DetectInstallation,
            new OutlookPresence(false, OfficeBitness.Unknown, ObjectModelGuardPolicy.NotSet));

        var facts = presence.ClassicInstalled
            ? Safe(() => TimedCall.Run(outlook.ReadFacts, outlookTimeout, FailedFacts, sta: true), FailedFacts)
            : new OutlookFacts(OutlookConnection.NotAttempted, SafeText.Empty, null, null);

        var documents = Timed(folder.InspectDocuments, new FolderInfo(false, false, CloudSync.None));

        return new DiagnosticReport(systemInfo, sample, idle, presence, facts, null, null, documents);
    }

    public DiagnosticReport RunGuardedCheck(DiagnosticReport current)
    {
        if (!current.CanRunGuardedTest)
        {
            return current;
        }

        var outcome = Safe(
            () => TimedCall.Run(outlook.TryGuardedRead, guardedTimeout, GuardedAccessOutcome.TimedOut, sta: true),
            GuardedAccessOutcome.Failed);

        return current with { GuardedAccess = outcome };
    }

    public static DiagnosticReport WithUserAnswer(DiagnosticReport current, bool alertShown)
        => current with { AlertShownByOutlook = alertShown };

    /// <summary>Sonde hors Outlook : même délai maximal, même valeur de repli en cas d'erreur ou de blocage.</summary>
    private T Timed<T>(Func<T> read, T fallback)
        => Safe(() => TimedCall.Run(read, outlookTimeout, fallback, sta: false), fallback);

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return fallback;
        }
    }
}
