using System.Diagnostics;
using static Cabinet.Diagnostic.Core.Tests.TestData;

namespace Cabinet.Diagnostic.Core.Tests;

public class DiagnosticRunnerTests
{
    private sealed class FakeSystem(bool throws = false) : ISystemProbe
    {
        public SystemInfo Read() => throws
            ? throw new InvalidOperationException("WMI indisponible")
            : Sample().System;
    }

    private sealed class FakeWindow : IWindowProbe
    {
        public WindowSample? SampleForeground() => new(true, 10);
        public TimeSpan? IdleTime() => TimeSpan.FromSeconds(1);
    }

    private sealed class FakeFolder : IFolderProbe
    {
        public FolderInfo InspectDocuments() => new(true, true, CloudSync.None);
    }

    private sealed class FakeOutlook(
        bool installed,
        OutlookConnection connection = OutlookConnection.Connected,
        bool factsBlockForever = false,
        bool guardedBlockForever = false,
        bool guardedThrows = false) : IOutlookProbe
    {
        public int ReadFactsCalls { get; private set; }

        public OutlookPresence DetectInstallation() => new(installed, OfficeBitness.X64, ObjectModelGuardPolicy.NotSet);

        public OutlookFacts ReadFacts()
        {
            ReadFactsCalls++;
            if (factsBlockForever)
            {
                new ManualResetEventSlim(false).Wait();
            }

            return new OutlookFacts(connection, SafeText.From("16.0"), 5, 2);
        }

        public GuardedAccessOutcome TryGuardedRead()
        {
            if (guardedBlockForever)
            {
                new ManualResetEventSlim(false).Wait();
            }

            if (guardedThrows)
            {
                throw new InvalidOperationException("erreur COM simulée");
            }

            return GuardedAccessOutcome.Succeeded;
        }
    }

    private static DiagnosticRunner Runner(
        ISystemProbe? system = null,
        IOutlookProbe? outlook = null,
        TimeSpan? outlookTimeout = null,
        TimeSpan? guardedTimeout = null)
        => new(
            system ?? new FakeSystem(),
            new FakeWindow(),
            outlook ?? new FakeOutlook(installed: true),
            new FakeFolder(),
            outlookTimeout ?? TimeSpan.FromSeconds(5),
            guardedTimeout ?? TimeSpan.FromSeconds(5));

    [Fact]
    public void RunAutomaticChecks_AllGood_FillsReport()
    {
        var r = Runner().RunAutomaticChecks();
        Assert.Equal(OutlookConnection.Connected, r.Outlook.Connection);
        Assert.Null(r.GuardedAccess);
        Assert.True(r.CanRunGuardedTest);
    }

    [Fact]
    public void RunAutomaticChecks_ThrowingSystemProbe_FallsBackToUnknown()
    {
        var r = Runner(system: new FakeSystem(throws: true)).RunAutomaticChecks();
        Assert.True(r.System.WindowsVersion.IsEmpty);
        Assert.Equal(AntivirusState.Unknown, r.System.Antivirus);
    }

    [Fact]
    public void RunAutomaticChecks_OutlookNotInstalled_DoesNotReadFacts()
    {
        var outlook = new FakeOutlook(installed: false);
        var r = Runner(outlook: outlook).RunAutomaticChecks();
        Assert.Equal(0, outlook.ReadFactsCalls);
        Assert.Equal(OutlookConnection.NotAttempted, r.Outlook.Connection);
    }

    [Fact]
    public void RunAutomaticChecks_OutlookFactsBlock_ReturnsFailedConnectionWithinTimeout()
    {
        var sw = Stopwatch.StartNew();
        var r = Runner(outlook: new FakeOutlook(installed: true, factsBlockForever: true), outlookTimeout: TimeSpan.FromMilliseconds(200)).RunAutomaticChecks();
        Assert.Equal(OutlookConnection.Failed, r.Outlook.Connection);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void RunGuardedCheck_Succeeds()
    {
        var runner = Runner();
        Assert.Equal(GuardedAccessOutcome.Succeeded, runner.RunGuardedCheck(runner.RunAutomaticChecks()).GuardedAccess);
    }

    [Fact]
    public void RunGuardedCheck_Blocking_IsTimedOut()
    {
        var runner = Runner(outlook: new FakeOutlook(installed: true, guardedBlockForever: true), guardedTimeout: TimeSpan.FromMilliseconds(200));
        Assert.Equal(GuardedAccessOutcome.TimedOut, runner.RunGuardedCheck(runner.RunAutomaticChecks()).GuardedAccess);
    }

    [Fact]
    public void RunGuardedCheck_NotConnected_LeavesReportUnchanged()
    {
        var runner = Runner(outlook: new FakeOutlook(installed: true, connection: OutlookConnection.NotRunning));
        var before = runner.RunAutomaticChecks();
        Assert.Same(before, runner.RunGuardedCheck(before));
    }

    [Fact]
    public void RunGuardedCheck_ThrowingProbe_IsFailed()
    {
        var runner = Runner(outlook: new FakeOutlook(installed: true, guardedThrows: true));
        Assert.Equal(GuardedAccessOutcome.Failed, runner.RunGuardedCheck(runner.RunAutomaticChecks()).GuardedAccess);
    }

    [Fact]
    public void WithUserAnswer_SetsAnswer()
        => Assert.True(DiagnosticRunner.WithUserAnswer(Sample() with { AlertShownByOutlook = null }, true).AlertShownByOutlook);

    [Fact]
    public void TimedCall_RethrowsWorkException()
        => Assert.Throws<InvalidOperationException>(() =>
            TimedCall.Run<int>(() => throw new InvalidOperationException("x"), TimeSpan.FromSeconds(5), 0, sta: false));
}

public class ReviewFixRunnerTests
{
    private sealed class BlockingSystem : ISystemProbe
    {
        public SystemInfo Read()
        {
            new ManualResetEventSlim(false).Wait();
            return TestData.Sample().System;
        }
    }

    private sealed class FineWindow : IWindowProbe
    {
        public WindowSample? SampleForeground() => new(true, 1);
        public TimeSpan? IdleTime() => TimeSpan.Zero;
    }

    private sealed class NoOutlook : IOutlookProbe
    {
        public OutlookPresence DetectInstallation() => new(false, OfficeBitness.Unknown, ObjectModelGuardPolicy.NotSet);
        public OutlookFacts ReadFacts() => throw new InvalidOperationException();
        public GuardedAccessOutcome TryGuardedRead() => throw new InvalidOperationException();
    }

    private sealed class FineFolder : IFolderProbe
    {
        public FolderInfo InspectDocuments() => new(true, true, CloudSync.None);
    }

    [Fact]
    public void RunAutomaticChecks_BlockingSystemProbe_FallsBackWithinTimeout()
    {
        var runner = new DiagnosticRunner(new BlockingSystem(), new FineWindow(), new NoOutlook(), new FineFolder(),
            TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var report = runner.RunAutomaticChecks();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Equal(AntivirusState.Unknown, report.System.Antivirus);
        Assert.True(report.Documents.Writable);
    }
}
