namespace Cabinet.Platform.Tests;

/// <summary>
/// Les tests propres à un système ne s'exécutent que sur ce système (machines macOS et Windows de l'intégration continue).
/// </summary>
public class PlatformTests
{
    [Fact]
    public void Factory_ReturnsProbeForCurrentSystem()
    {
        var probe = PlatformServices.CreateActivityProbe();
        if (OperatingSystem.IsMacOS())
        {
            Assert.IsType<MacActivityProbe>(probe);
        }
        else if (OperatingSystem.IsWindows())
        {
            Assert.IsType<WindowsActivityProbe>(probe);
        }
        else
        {
            Assert.IsType<UnsupportedActivityProbe>(probe);
        }
    }

    [Fact]
    public void Probe_DoesNotThrow()
    {
        var probe = PlatformServices.CreateActivityProbe();
        _ = probe.ReadActiveWindow();
        _ = probe.IdleTime();
    }

    [Fact]
    public void Idle_IsMeasurableOnDesktopSystems()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.NotNull(PlatformServices.CreateActivityProbe().IdleTime());
    }

    [Fact]
    public void KeyStore_ReturnsSameKeyTwice()
    {
        var folder = Path.Combine(Path.GetTempPath(), "cabinet-key-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = PlatformServices.CreateKeyStore(folder, "OutilCabinet.Tests." + Guid.NewGuid().ToString("N"));
            var first = store.GetOrCreateKey();
            var second = store.GetOrCreateKey();
            Assert.Equal(32, first.Length);
            Assert.Equal(first, second);
            (store as IDisposableKeyStore)?.DeleteKey();
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public void DataFolder_IsInUserProfile()
        => Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), PlatformServices.DataFolder());

    [Fact]
    public void PlatformAssembly_HasNoNetworkReferences()
        => Cabinet.Diagnostic.Core.Tests.NetworkGuard.AssertNoNetworkReferences(typeof(PlatformServices).Assembly);
}
