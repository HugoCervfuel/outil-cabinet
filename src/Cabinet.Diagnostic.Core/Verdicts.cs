namespace Cabinet.Diagnostic.Core;

public enum VerdictKind
{
    TrackingOk,
    TrackingUnavailable,
    OutlookDirectOk,
    OutlookNeedsAddIn,
    OutlookNotRunning,
    OutlookNotTested,
    OutlookNotFound,
    DocumentsLocal,
    DocumentsSynced,
    DocumentsNotWritable,
}

public sealed record Verdict(CheckStatus Status, VerdictKind Kind);

/// <summary>Conclusions tirées d'un rapport de diagnostic.</summary>
public static class Verdicts
{
    public static Verdict Tracking(DiagnosticReport r)
        => r.Window is { ProcessNameRead: true } && r.IdleTime is not null
            ? new Verdict(CheckStatus.Ok, VerdictKind.TrackingOk)
            : new Verdict(CheckStatus.Failed, VerdictKind.TrackingUnavailable);

    public static Verdict Outlook(DiagnosticReport r)
    {
        if (!r.OutlookPresence.ClassicInstalled)
        {
            return new Verdict(CheckStatus.Failed, VerdictKind.OutlookNotFound);
        }

        if (r.Outlook.Connection == OutlookConnection.NotRunning)
        {
            return new Verdict(CheckStatus.Warning, VerdictKind.OutlookNotRunning);
        }

        if (r.Outlook.Connection != OutlookConnection.Connected)
        {
            return new Verdict(CheckStatus.Warning, VerdictKind.OutlookNotTested);
        }

        // La réponse de l'utilisateur ne compte qu'après une lecture réussie : sans lecture, l'outil n'a pu provoquer aucune alerte.
        if (r.GuardedAccess is GuardedAccessOutcome.Denied or GuardedAccessOutcome.TimedOut
            || (r.GuardedAccess == GuardedAccessOutcome.Succeeded && r.AlertShownByOutlook == true))
        {
            return new Verdict(CheckStatus.Warning, VerdictKind.OutlookNeedsAddIn);
        }

        if (r.GuardedAccess == GuardedAccessOutcome.Succeeded && r.AlertShownByOutlook == false)
        {
            return new Verdict(CheckStatus.Ok, VerdictKind.OutlookDirectOk);
        }

        return new Verdict(CheckStatus.Warning, VerdictKind.OutlookNotTested);
    }

    public static Verdict Documents(DiagnosticReport r)
    {
        if (!r.Documents.Exists || !r.Documents.Writable)
        {
            return new Verdict(CheckStatus.Failed, VerdictKind.DocumentsNotWritable);
        }

        return r.Documents.Sync != CloudSync.None
            ? new Verdict(CheckStatus.Warning, VerdictKind.DocumentsSynced)
            : new Verdict(CheckStatus.Ok, VerdictKind.DocumentsLocal);
    }
}
