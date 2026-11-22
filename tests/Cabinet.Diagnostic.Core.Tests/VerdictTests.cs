using static Cabinet.Diagnostic.Core.Tests.TestData;

namespace Cabinet.Diagnostic.Core.Tests;

public class VerdictTests
{
    [Fact]
    public void Outlook_AllGood_IsDirectOk()
        => Assert.Equal(new Verdict(CheckStatus.Ok, VerdictKind.OutlookDirectOk), Verdicts.Outlook(Sample()));

    [Fact]
    public void Outlook_NotInstalled_IsNotFound()
        => Assert.Equal(VerdictKind.OutlookNotFound,
            Verdicts.Outlook(Sample() with { OutlookPresence = Sample().OutlookPresence with { ClassicInstalled = false } }).Kind);

    [Fact]
    public void Outlook_NotRunning_AsksToOpenOutlook()
        => Assert.Equal(new Verdict(CheckStatus.Warning, VerdictKind.OutlookNotRunning),
            Verdicts.Outlook(Sample() with { Outlook = Sample().Outlook with { Connection = OutlookConnection.NotRunning } }));

    [Fact]
    public void Outlook_AlertShown_NeedsAddIn()
        => Assert.Equal(VerdictKind.OutlookNeedsAddIn, Verdicts.Outlook(Sample() with { AlertShownByOutlook = true }).Kind);

    [Theory]
    [InlineData(GuardedAccessOutcome.Denied)]
    [InlineData(GuardedAccessOutcome.TimedOut)]
    public void Outlook_DeniedOrTimedOut_NeedsAddIn(GuardedAccessOutcome outcome)
        => Assert.Equal(VerdictKind.OutlookNeedsAddIn,
            Verdicts.Outlook(Sample() with { GuardedAccess = outcome, AlertShownByOutlook = null }).Kind);

    [Fact]
    public void Outlook_NoMailToTest_IsNotTested()
        => Assert.Equal(new Verdict(CheckStatus.Warning, VerdictKind.OutlookNotTested),
            Verdicts.Outlook(Sample() with { GuardedAccess = GuardedAccessOutcome.NoMailToTest }));

    [Fact]
    public void Outlook_SucceededButUnanswered_IsNotTested()
        => Assert.Equal(VerdictKind.OutlookNotTested, Verdicts.Outlook(Sample() with { AlertShownByOutlook = null }).Kind);

    [Fact]
    public void CanRunGuardedTest_FalseWhenNotConnected()
        => Assert.False((Sample() with { Outlook = Sample().Outlook with { Connection = OutlookConnection.Failed } }).CanRunGuardedTest);

    [Fact]
    public void CanRunGuardedTest_TrueWhenConnected()
        => Assert.True(Sample().CanRunGuardedTest);

    [Fact]
    public void Tracking_AllGood_IsOk()
        => Assert.Equal(new Verdict(CheckStatus.Ok, VerdictKind.TrackingOk), Verdicts.Tracking(Sample()));

    [Fact]
    public void Tracking_NoWindow_IsUnavailable()
        => Assert.Equal(new Verdict(CheckStatus.Failed, VerdictKind.TrackingUnavailable), Verdicts.Tracking(Sample() with { Window = null }));

    [Fact]
    public void Tracking_NoIdle_IsUnavailable()
        => Assert.Equal(VerdictKind.TrackingUnavailable, Verdicts.Tracking(Sample() with { IdleTime = null }).Kind);

    [Fact]
    public void Documents_Local_IsOk()
        => Assert.Equal(new Verdict(CheckStatus.Ok, VerdictKind.DocumentsLocal), Verdicts.Documents(Sample()));

    [Fact]
    public void Documents_Synced_IsWarning()
        => Assert.Equal(new Verdict(CheckStatus.Warning, VerdictKind.DocumentsSynced),
            Verdicts.Documents(Sample() with { Documents = new FolderInfo(true, true, CloudSync.OneDrive) }));

    [Fact]
    public void Documents_NotWritable_IsFailed()
        => Assert.Equal(new Verdict(CheckStatus.Failed, VerdictKind.DocumentsNotWritable),
            Verdicts.Documents(Sample() with { Documents = new FolderInfo(true, false, CloudSync.None) }));
}
