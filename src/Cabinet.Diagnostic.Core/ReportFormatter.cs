using System.Globalization;
using System.Text;

namespace Cabinet.Diagnostic.Core;

/// <summary>
/// Met en forme le rapport en texte français. N'écrit que des valeurs typées et des textes système nettoyés.
/// </summary>
public static class ReportFormatter
{
    private const string NewLine = "\r\n";
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    public static string VerdictSentence(VerdictKind kind) => kind switch
    {
        VerdictKind.TrackingOk => "Suivi d'activité : possible sur cet ordinateur.",
        VerdictKind.TrackingUnavailable => "Suivi d'activité : impossible de lire la fenêtre active.",
        VerdictKind.OutlookDirectOk => "Outlook : accès direct possible, sans alerte.",
        VerdictKind.OutlookNeedsAddIn => "Outlook : il faudra installer le module dans Outlook.",
        VerdictKind.OutlookNotRunning => "Outlook : ouvrez Outlook, puis relancez le diagnostic.",
        VerdictKind.OutlookNotTested => "Outlook : le test n'a pas pu aller jusqu'au bout.",
        VerdictKind.OutlookNotFound => "Outlook : Outlook classique introuvable sur cet ordinateur.",
        VerdictKind.DocumentsLocal => "Dossier Documents : sur cet ordinateur, non synchronisé.",
        VerdictKind.DocumentsSynced => "Dossier Documents : synchronisé avec un service en ligne (à éviter pour les pièces).",
        VerdictKind.DocumentsNotWritable => "Dossier Documents : écriture impossible.",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static string StatusMark(CheckStatus status) => status switch
    {
        CheckStatus.Ok => "[OK] ",
        CheckStatus.Warning => "[!]  ",
        _ => "[X]  ",
    };

    public static string ToText(DiagnosticReport report, DateTimeOffset generatedAt)
    {
        var text = new StringBuilder();
        void Line(string value = "") => text.Append(value).Append(NewLine);

        Line("Diagnostic — Outil de cabinet");
        Line($"Date : {generatedAt.ToString("dd/MM/yyyy HH:mm", French)}");
        Line("Ce rapport ne contient aucune donnée client.");
        Line();

        foreach (var verdict in new[] { Verdicts.Tracking(report), Verdicts.Outlook(report), Verdicts.Documents(report) })
        {
            Line(StatusMark(verdict.Status) + VerdictSentence(verdict.Kind));
        }

        Line();
        Line("Détails techniques");

        var system = report.System;
        Line($"Windows : {OrUnknown(system.WindowsVersion, "version inconnue")} ({(system.Is64Bit ? "64 bits" : "32 bits")})");
        Line($"Antivirus : {OrUnknown(system.AntivirusName, "nom inconnu")} — {Describe(system.Antivirus)}");
        Line($"Fenêtre active : {(report.Window is { ProcessNameRead: true } ? "lisible" : "illisible")}");
        Line($"Inactivité : {(report.IdleTime is null ? "non mesurable" : "mesurable")}");

        var presence = report.OutlookPresence;
        Line($"Outlook classique : {(presence.ClassicInstalled ? "installé" : "non trouvé")} ({Describe(presence.Bitness)})");
        Line($"Règle d'accès des programmes : {Describe(presence.GuardPolicy)}");

        var outlook = report.Outlook;
        Line($"Connexion à Outlook : {Describe(outlook.Connection)}");
        Line($"Version d'Outlook : {OrUnknown(outlook.Version, "inconnue")}");
        Line($"Boîte de réception : {Count(outlook.InboxItemCount, "éléments")}");
        Line($"Agenda (7 prochains jours) : {Count(outlook.CalendarEventsNext7Days, "événements")}");
        Line($"Accès protégé : {Describe(report.GuardedAccess)}");
        Line($"Alerte affichée par Outlook : {report.AlertShownByOutlook switch { true => "oui", false => "non", null => "non renseigné" }}");

        var documents = report.Documents;
        Line($"Dossier Documents : {(documents.Exists ? "présent" : "absent")}, écriture {(documents.Writable ? "possible" : "impossible")}, synchronisation : {Describe(documents.Sync)}");

        return text.ToString();
    }

    private static string OrUnknown(SafeText value, string unknown) => value.IsEmpty ? unknown : value.Value;

    private static string Count(int? value, string unit) => value is { } n ? $"{n.ToString(French)} {unit}" : "non lu";

    private static string Describe(AntivirusState state) => state switch
    {
        AntivirusState.ActiveUpToDate => "actif et à jour",
        AntivirusState.ActiveOutdated => "actif, définitions anciennes",
        AntivirusState.Disabled => "désactivé",
        AntivirusState.None => "aucun détecté",
        _ => "état inconnu",
    };

    private static string Describe(OfficeBitness bitness) => bitness switch
    {
        OfficeBitness.X64 => "64 bits",
        OfficeBitness.X86 => "32 bits",
        _ => "architecture inconnue",
    };

    private static string Describe(ObjectModelGuardPolicy policy) => policy switch
    {
        ObjectModelGuardPolicy.WarnWhenAntivirusInactive => "alerte si antivirus inactif",
        ObjectModelGuardPolicy.AlwaysWarn => "alerte systématique",
        ObjectModelGuardPolicy.NeverWarn => "jamais d'alerte",
        _ => "par défaut",
    };

    private static string Describe(OutlookConnection connection) => connection switch
    {
        OutlookConnection.Connected => "réussie",
        OutlookConnection.NotRunning => "Outlook fermé",
        OutlookConnection.Failed => "échec",
        _ => "non tentée",
    };

    private static string Describe(GuardedAccessOutcome? outcome) => outcome switch
    {
        GuardedAccessOutcome.Succeeded => "réussi",
        GuardedAccessOutcome.Denied => "refusé",
        GuardedAccessOutcome.TimedOut => "sans réponse",
        GuardedAccessOutcome.NoMailToTest => "aucun e-mail à tester",
        GuardedAccessOutcome.Failed => "échec",
        _ => "non testé",
    };

    private static string Describe(CloudSync sync) => sync switch
    {
        CloudSync.OneDrive => "OneDrive",
        CloudSync.OtherService => "autre service en ligne",
        _ => "aucune",
    };
}
