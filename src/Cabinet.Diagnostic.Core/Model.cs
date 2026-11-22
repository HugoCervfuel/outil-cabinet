namespace Cabinet.Diagnostic.Core;

public enum AntivirusState { Unknown, ActiveUpToDate, ActiveOutdated, Disabled, None }

public enum ObjectModelGuardPolicy { NotSet, WarnWhenAntivirusInactive, AlwaysWarn, NeverWarn }

public enum CloudSync { None, OneDrive, OtherService }

public enum CheckStatus { Ok, Warning, Failed }

public enum OfficeBitness { Unknown, X86, X64 }

public enum OutlookConnection { NotAttempted, Connected, NotRunning, Failed }

public enum GuardedAccessOutcome { Succeeded, Denied, TimedOut, NoMailToTest, Failed }

public sealed record SystemInfo(SafeText WindowsVersion, bool Is64Bit, SafeText AntivirusName, AntivirusState Antivirus);

/// <summary>Échantillon de la fenêtre active : jamais son titre, seulement sa longueur.</summary>
public sealed record WindowSample(bool ProcessNameRead, int TitleLength);

public sealed record OutlookPresence(bool ClassicInstalled, OfficeBitness Bitness, ObjectModelGuardPolicy GuardPolicy);

public sealed record OutlookFacts(OutlookConnection Connection, SafeText Version, int? InboxItemCount, int? CalendarEventsNext7Days);

public sealed record FolderInfo(bool Exists, bool Writable, CloudSync Sync);

public sealed record DiagnosticReport(
    SystemInfo System,
    WindowSample? Window,
    TimeSpan? IdleTime,
    OutlookPresence OutlookPresence,
    OutlookFacts Outlook,
    GuardedAccessOutcome? GuardedAccess,
    bool? AlertShownByOutlook,
    FolderInfo Documents)
{
    public bool CanRunGuardedTest => OutlookPresence.ClassicInstalled && Outlook.Connection == OutlookConnection.Connected;
}
