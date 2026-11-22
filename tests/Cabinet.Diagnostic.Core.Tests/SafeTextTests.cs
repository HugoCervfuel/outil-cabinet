namespace Cabinet.Diagnostic.Core.Tests;

public class SafeTextTests
{
    [Fact]
    public void From_PlainSystemText_IsKept()
        => Assert.Equal("Windows 11 Pro 23H2 (build 22631)", SafeText.From("Windows 11 Pro 23H2 (build 22631)").Value);

    [Fact]
    public void From_AccentedLatinLetters_AreKept()
        => Assert.Equal("Sécurité Windows", SafeText.From("Sécurité Windows").Value);

    [Fact]
    public void From_DisallowedCharacters_AreRemoved()
        => Assert.Equal("Defender xy.fr", SafeText.From("Defender <x@y.fr>").Value);

    [Fact]
    public void From_WhitespaceRuns_AreCollapsedAndTrimmed()
        => Assert.Equal("a b", SafeText.From("  a \t\n b  ").Value);

    [Fact]
    public void From_LongText_IsTruncatedTo64()
        => Assert.Equal(64, SafeText.From(new string('a', 100)).Value.Length);

    [Fact]
    public void From_Null_IsEmpty()
    {
        var t = SafeText.From(null);
        Assert.True(t.IsEmpty);
        Assert.Equal("", t.Value);
    }
}

public class CoreNetworkGuardTests
{
    [Fact]
    public void CoreAssembly_HasNoNetworkReferences()
        => NetworkGuard.AssertNoNetworkReferences(typeof(SafeText).Assembly);
}
