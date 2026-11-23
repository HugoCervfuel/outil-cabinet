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

        public List<DateTimeOffset> Since { get; } = new();

        public Exception? Failure { get; set; }

        public TaskCompletionSource? Gate { get; set; }

        public bool Partial { get; set; }

        public async Task<FetchResult> FetchAsync(MailAccount account, uint? afterUid, uint? uidValidity, DateTimeOffset since, int max, CancellationToken cancellationToken)
        {
            Calls.Add((afterUid, uidValidity));
            Since.Add(since);
            if (Gate is not null)
            {
                await Gate.Task;
            }

            if (Failure is not null)
            {
                throw Failure;
            }

            var mails = Mailbox.Where(m => uidValidity != UidValidity || afterUid is null || m.Uid > afterUid).TakeLast(max).ToList();
            if (Partial && mails.Count > 1)
            {
                return new FetchResult(UidValidity, mails.Take(mails.Count - 1).ToList(), mails[^2].Uid, Complete: false);
            }

            return new FetchResult(UidValidity, mails);
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

    private static readonly DateTimeOffset Now = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);

    private DateTimeOffset _now = Now;

    private (MailboxSync Sync, MailDesk Desk) Create(FakeSource source, bool configure = true)
    {
        var store = new EncryptedStore(_folder, new FixedKey());
        var desk = new MailDesk(store, () => Array.Empty<Dossier>(), new NoAssistant(), () => DateTimeOffset.Now);
        var sync = new MailboxSync(store, desk, source, () => _now);
        if (configure)
        {
            sync.SaveAccount(new MailAccount("avocat@exemple.fr", "MOT-DE-PASSE-BRIDGE"));
        }

        return (sync, desk);
    }

    private static FetchedMail Mail(uint uid, string subject, bool bulk = false, bool unreadable = false)
        => new(uid, new MailMessage("Client", "client@exemple.fr", subject, null, unreadable ? "" : "Bonjour", Array.Empty<string>(), $"<{uid}@exemple.fr>"), bulk, unreadable);

    [Fact]
    public async Task FirstRun_LooksBackSevenDays_ThenFromTheLastRun()
    {
        var source = new FakeSource();
        var (sync, _) = Create(source);
        await sync.SyncAsync(CancellationToken.None);
        await sync.SyncAsync(CancellationToken.None);
        Assert.Equal(Now.AddDays(-7), source.Since[0]);
        // Ensuite, jamais plus loin que le relevé précédent (avec une marge) : pas d'import de vieux e-mails.
        Assert.Equal(Now.AddDays(-2), source.Since[1]);
    }

    [Fact]
    public async Task PartialRun_KeepsTheEarlierDateFloor()
    {
        // Retour de congés : 3 e-mails, 2 par relevé. Le deuxième relevé doit encore chercher depuis le premier.
        var source = new FakeSource();
        var (sync, desk) = Create(source);
        await sync.SyncAsync(CancellationToken.None);
        source.Mailbox.AddRange(new[] { Mail(1, "Un"), Mail(2, "Deux"), Mail(3, "Trois") });
        source.Partial = true;
        _now = Now.AddDays(3);
        await sync.SyncAsync(CancellationToken.None);
        source.Partial = false;
        _now = Now.AddDays(6);
        await sync.SyncAsync(CancellationToken.None);
        Assert.Equal(Now.AddDays(-2), source.Since[2]);
        Assert.Equal(3, desk.Items.Count);
        // Une fois tout relevé, la date avance de nouveau.
        await sync.SyncAsync(CancellationToken.None);
        Assert.Equal(Now.AddDays(4), source.Since[3]);
    }

    [Fact]
    public async Task RemovedMail_DoesNotComeBack_EvenAfterAMailboxReset()
    {
        var source = new FakeSource();
        source.Mailbox.Add(Mail(1, "Premier"));
        var (sync, desk) = Create(source);
        await sync.SyncAsync(CancellationToken.None);
        desk.Remove(desk.Items.Single().Id);
        source.UidValidity = 8;
        await sync.SyncAsync(CancellationToken.None);
        Assert.Empty(desk.Items);
    }

    [Fact]
    public async Task UnreadableMail_IsAddedAndMentioned()
    {
        var source = new FakeSource();
        source.Mailbox.AddRange(new[] { Mail(1, "Lisible"), Mail(2, "Illisible", unreadable: true) });
        var (sync, desk) = Create(source);
        var report = await sync.SyncAsync(CancellationToken.None);
        Assert.Equal(2, desk.Items.Count);
        Assert.Equal(1, report.Unreadable);
        Assert.Contains("illisible", report.Summary);
    }

    [Fact]
    public async Task AccountChangedDuringSync_OldResultIsNotShown()
    {
        var source = new FakeSource { Gate = new TaskCompletionSource(), Failure = new MailSyncException("Ancienne erreur.") };
        var (sync, _) = Create(source);
        var running = sync.SyncAsync(CancellationToken.None);
        sync.SaveAccount(new MailAccount("autre@exemple.fr", "AUTRE"));
        source.Gate.SetResult();
        await running;
        Assert.Null(sync.LastReport);
    }

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
