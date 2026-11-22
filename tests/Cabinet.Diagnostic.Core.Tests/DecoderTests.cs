namespace Cabinet.Diagnostic.Core.Tests;

public class DecoderTests
{
    [Theory]
    [InlineData(397568, AntivirusState.ActiveUpToDate)]   // 0x061100
    [InlineData(397584, AntivirusState.ActiveOutdated)]   // 0x061110
    [InlineData(393472, AntivirusState.Disabled)]         // 0x060100
    [InlineData(266240, AntivirusState.ActiveUpToDate)]   // 0x041000
    [InlineData(401408, AntivirusState.Unknown)]          // 0x062000 (octet d'état inconnu)
    public void Decode_KnownStates(int state, AntivirusState expected)
        => Assert.Equal(expected, AntivirusStateDecoder.Decode(state));

    [Fact]
    public void Decode_Null_IsUnknown()
        => Assert.Equal(AntivirusState.Unknown, AntivirusStateDecoder.Decode(null));

    [Fact]
    public void Combine_AnyUpToDateWins()
        => Assert.Equal(AntivirusState.ActiveUpToDate,
            AntivirusStateDecoder.Combine(new[] { AntivirusState.Disabled, AntivirusState.ActiveUpToDate }));

    [Fact]
    public void Combine_Empty_IsNone()
        => Assert.Equal(AntivirusState.None, AntivirusStateDecoder.Combine(Array.Empty<AntivirusState>()));

    [Fact]
    public void Combine_OutdatedBeforeDisabled()
        => Assert.Equal(AntivirusState.ActiveOutdated,
            AntivirusStateDecoder.Combine(new[] { AntivirusState.Disabled, AntivirusState.ActiveOutdated }));

    [Theory]
    [InlineData(0, ObjectModelGuardPolicy.WarnWhenAntivirusInactive)]
    [InlineData(1, ObjectModelGuardPolicy.AlwaysWarn)]
    [InlineData(2, ObjectModelGuardPolicy.NeverWarn)]
    [InlineData(7, ObjectModelGuardPolicy.NotSet)]
    [InlineData(null, ObjectModelGuardPolicy.NotSet)]
    public void GuardPolicy_FromRegistryValue(int? value, ObjectModelGuardPolicy expected)
        => Assert.Equal(expected, GuardPolicyDecoder.FromRegistryValue(value));

    private static IReadOnlyDictionary<string, string?> Env(string? commercial = null, string? personal = null)
        => new Dictionary<string, string?>
        {
            ["OneDrive"] = personal,
            ["OneDriveCommercial"] = commercial,
            ["OneDriveConsumer"] = null,
        };

    [Fact]
    public void Detect_DocumentsUnderOneDriveCommercial_IsOneDrive()
        => Assert.Equal(CloudSync.OneDrive,
            CloudSyncDetector.Detect(@"C:\Users\ana\OneDrive - Cabinet\Documents", Env(commercial: @"C:\Users\ana\OneDrive - Cabinet")));

    [Fact]
    public void Detect_IgnoresCaseAndTrailingSeparator()
        => Assert.Equal(CloudSync.OneDrive,
            CloudSyncDetector.Detect(@"c:\users\ana\onedrive - cabinet\documents\", Env(commercial: @"C:\Users\Ana\OneDrive - Cabinet\")));

    [Fact]
    public void Detect_OneDriveSegmentWithoutVariable_IsOneDrive()
        => Assert.Equal(CloudSync.OneDrive, CloudSyncDetector.Detect(@"C:\Users\ana\OneDrive\Documents", Env()));

    [Fact]
    public void Detect_LocalDocuments_IsNone()
        => Assert.Equal(CloudSync.None, CloudSyncDetector.Detect(@"C:\Users\ana\Documents", Env()));

    [Fact]
    public void Detect_Dropbox_IsOtherService()
        => Assert.Equal(CloudSync.OtherService, CloudSyncDetector.Detect(@"D:\Dropbox\Dossiers", Env()));
}
