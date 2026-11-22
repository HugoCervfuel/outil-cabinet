using Cabinet.Diagnostic.Core.Tests;

namespace Cabinet.Diagnostic.Windows.Tests;

/// <summary>
/// Tests exécutés sur la machine Windows de l'intégration continue (sans Office, sans session interactive).
/// </summary>
public class ProbeSmokeTests
{
    [Fact]
    public void SystemProbe_Read_DoesNotThrow()
    {
        var s = new WindowsSystemProbe().Read();
        Assert.False(s.WindowsVersion.IsEmpty);
        Assert.True(s.Is64Bit);
    }

    [Fact]
    public void WindowProbe_DoesNotThrow()
    {
        var p = new WindowsWindowProbe();
        _ = p.SampleForeground();
        _ = p.IdleTime();
    }

    [Fact]
    public void OutlookProbe_WithoutOffice_ReportsNotInstalled()
        => Assert.False(new OutlookComProbe().DetectInstallation().ClassicInstalled);

    [Fact]
    public void OutlookProbe_WithoutOutlookProcess_ReportsNotRunning()
        => Assert.Equal(OutlookConnection.NotRunning, new OutlookComProbe().ReadFacts().Connection);

    [Fact]
    public void OutlookProbe_GuardedReadWithoutOutlook_IsFailed()
        => Assert.Equal(GuardedAccessOutcome.Failed, new OutlookComProbe().TryGuardedRead());

    [Fact]
    public void FolderProbe_Documents_IsWritable()
    {
        var f = new WindowsFolderProbe().InspectDocuments();
        Assert.True(f.Exists);
        Assert.True(f.Writable);
    }

    [Fact]
    public void WindowsAssembly_HasNoNetworkReferences()
        => NetworkGuard.AssertNoNetworkReferences(typeof(OutlookComProbe).Assembly);
}
