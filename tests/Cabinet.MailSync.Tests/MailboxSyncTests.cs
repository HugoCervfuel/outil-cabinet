using System.Net.Security;

namespace Cabinet.MailSync.Tests;

public class MailboxSyncTests : IDisposable
{
    private sealed class FixedKey : IKeyStore
    {
        public byte[] GetOrCreateKey() => Enumerable.Repeat((byte)5, 32).ToArray();
    }

    private sealed class NoAssistant : IMailAssistant
    {
        public Task<string> CompleteAsync(string system, string user, CancellationToken cancellationToken) => Task.FromResult("");
    }

    private sealed class FakeSource : IMailboxSource
    {
        public uint UidValidity { get; set; } = 7;

        public List<FetchedMail> Mailbox { get; } = new();

        public List<(uint? AfterUid, uint? UidValidity)> Calls { get; } = new();

        public Exception? Failure { get; set; }

        public Task<FetchResult> FetchAsync(MailAccount account, uint? afterUid, uint? uidValidity, DateTimeOffset since, int max, CancellationToken cancellationToken)
        {
            Calls.Add((afterUid, uidValidity));
            if (Failure is not null)
            {
                throw Failure;
            }

            var mails = Mailbox.Where(m => uidValidity != UidValidity || afterUid is null || m.Uid > afterUid).TakeLast(max).ToList();
            return Task.FromResult(new FetchResult(UidValidity, mails));
        }

        public Task TestAsync(MailAccount account, CancellationToken cancellationToken)
            => Failure is null ? Task.CompletedTask : Task.FromException(Failure);
    }

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cabinet-sync-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private (MailboxSync Sync, MailDesk Desk) Create(FakeSource source, bool configure = true)
    {
        var store = new EncryptedStore(_folder, new FixedKey());
        var desk = new MailDesk(store, () => Array.Empty<Dossier>(), new NoAssistant(), () => DateTimeOffset.Now);
        var sync = new MailboxSync(store, desk, source, () => new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero));
        if (configure)
        {
            sync.SaveAccount(new MailAccount("avocat@exemple.fr", "MOT-DE-PASSE-BRIDGE"));
        }

        return (sync, desk);
    }

    private static FetchedMail Mail(uint uid, string subject, bool bulk = false)
        => new(uid, new MailMessage("Client", "client@exemple.fr", subject, null, "Bonjour", Array.Empty<string>(), $"<{uid}@exemple.fr>"), bulk);

    [Fact]
    public async Task FirstRun_ImportsMailsAndSkipsNewsletters()
    {
        var source = new FakeSource();
        source.Mailbox.AddRange(new[] { Mail(1, "Dossier Martin"), Mail(2, "Lettre d'information", bulk: true), Mail(3, "Question") });
        var (sync, desk) = Create(source);
        var report = await sync.SyncAsync(CancellationToken.None);
        Assert.Null(report.Error);
        Assert.Equal(2, report.Added);
        Assert.Equal(1, report.SkippedBulk);
        Assert.Equal(2, desk.Items.Count);
        Assert.Null(source.Calls.Single().AfterUid);
    }

    [Fact]
    public async Task NextRun_AsksOnlyForNewerMessages()
    {
        var source = new FakeSource();
        source.Mailbox.Add(Mail(1, "Premier"));
        var (sync, desk) = Create(source);
        await sync.SyncAsync(CancellationToken.None);
        source.Mailbox.Add(Mail(2, "Second"));
        var report = await sync.SyncAsync(CancellationToken.None);
        Assert.Equal(1, report.Added);
        Assert.Equal((1u, 7u), (source.Calls[1].AfterUid!.Value, source.Calls[1].UidValidity!.Value));
        Assert.Equal(2, desk.Items.Count);
    }

    [Fact]
    public async Task MailboxReset_DoesNotDuplicateMails()
    {
        var source = new FakeSource();
        source.Mailbox.Add(Mail(1, "Premier"));
        var (sync, desk) = Create(source);
        await sync.SyncAsync(CancellationToken.None);
        source.UidValidity = 8;
        await sync.SyncAsync(CancellationToken.None);
        Assert.Single(desk.Items);
    }

    [Fact]
    public async Task Failure_IsReportedNotThrown()
    {
        var source = new FakeSource { Failure = new MailSyncException("Proton Mail Bridge ne répond pas.") };
        var (sync, _) = Create(source);
        var report = await sync.SyncAsync(CancellationToken.None);
        Assert.Equal("Proton Mail Bridge ne répond pas.", report.Error);
    }

    [Fact]
    public async Task UnexpectedFailure_GivesAGenericMessage()
    {
        var source = new FakeSource { Failure = new InvalidOperationException("détail technique") };
        var (sync, _) = Create(source);
        var report = await sync.SyncAsync(CancellationToken.None);
        Assert.NotNull(report.Error);
        Assert.DoesNotContain("détail technique", report.Error);
    }

    [Fact]
    public async Task NotConfigured_SaysSo()
    {
        var (sync, _) = Create(new FakeSource(), configure: false);
        Assert.False(sync.IsConfigured);
        Assert.NotNull((await sync.SyncAsync(CancellationToken.None)).Error);
    }

    [Fact]
    public void Account_IsStoredEncrypted()
    {
        Create(new FakeSource());
        foreach (var file in Directory.GetFiles(_folder, "*", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("MOT-DE-PASSE-BRIDGE", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(file)));
        }

        var (reopened, _) = Create(new FakeSource(), configure: false);
        Assert.Equal("avocat@exemple.fr", reopened.Account!.Address);
    }

    [Fact]
    public void Bridge_IsReachedOnThisComputerOnly()
    {
        Assert.Equal("127.0.0.1", ProtonBridgeSource.Host);
        Assert.True(ProtonBridgeSource.AcceptCertificate("127.0.0.1", SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(ProtonBridgeSource.AcceptCertificate("imap.exemple.fr", SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.True(ProtonBridgeSource.AcceptCertificate("imap.exemple.fr", SslPolicyErrors.None));
    }
}
