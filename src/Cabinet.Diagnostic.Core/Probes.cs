namespace Cabinet.Diagnostic.Core;

/// <summary>Lit les informations système (version de Windows, antivirus).</summary>
public interface ISystemProbe
{
    SystemInfo Read();
}

/// <summary>Observe la fenêtre active et l'inactivité, sans jamais lire de titre.</summary>
public interface IWindowProbe
{
    WindowSample? SampleForeground();

    TimeSpan? IdleTime();
}

/// <summary>Interroge Outlook classique en lecture seule.</summary>
public interface IOutlookProbe
{
    OutlookPresence DetectInstallation();

    OutlookFacts ReadFacts();

    GuardedAccessOutcome TryGuardedRead();
}

/// <summary>Inspecte le dossier Documents de l'utilisateur.</summary>
public interface IFolderProbe
{
    FolderInfo InspectDocuments();
}
