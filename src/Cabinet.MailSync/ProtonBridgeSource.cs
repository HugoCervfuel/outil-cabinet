using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using Cabinet.Mail;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace Cabinet.MailSync;

/// <summary>
/// Relève la boîte de réception via Proton Mail Bridge, le petit logiciel de Proton qui déchiffre les e-mails sur cet
/// ordinateur et les présente comme une messagerie locale. La connexion ne quitte jamais l'ordinateur (127.0.0.1) ;
/// la boîte est ouverte en lecture seule et les e-mails ne sont pas marqués lus.
/// </summary>
public sealed class ProtonBridgeSource : IMailboxSource
{
    /// <summary>Toujours cet ordinateur : aucune autre adresse n'est possible.</summary>
    public const string Host = "127.0.0.1";

    public const int DefaultPort = 1143;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Bridge utilise un certificat qu'il a lui-même créé : accepté seulement pour cet ordinateur.
    /// Toute autre adresse exige un certificat valide.
    /// </summary>
    public static bool AcceptCertificate(string host, SslPolicyErrors errors)
        => errors == SslPolicyErrors.None
           || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));

    /// <summary>Lettre d'information ou liste de diffusion : rien à traiter pour un dossier.</summary>
    public static bool IsBulk(MimeMessage message)
    {
        if (message.Headers.Contains(HeaderId.ListUnsubscribe) || message.Headers.Contains(HeaderId.ListId))
        {
            return true;
        }

        var precedence = message.Headers[HeaderId.Precedence]?.Trim().ToLowerInvariant();
        return precedence is "bulk" or "list" or "junk";
    }

    public async Task TestAsync(MailAccount account, CancellationToken cancellationToken)
    {
        await Run(account, async client =>
        {
            await client.Inbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
            return 0;
        }, cancellationToken);
    }

    public Task<FetchResult> FetchAsync(MailAccount account, uint? afterUid, uint? uidValidity, DateTimeOffset since, int max, CancellationToken cancellationToken)
        => Run(account, client => FetchInboxAsync(client.Inbox, afterUid, uidValidity, since, max, cancellationToken), cancellationToken);

    private static async Task<FetchResult> FetchInboxAsync(IMailFolder inbox, uint? afterUid, uint? uidValidity, DateTimeOffset since, int max, CancellationToken cancellationToken)
    {
        await inbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
        var validity = inbox.UidValidity;
        // Lu à l'ouverture : un e-mail arrivé ensuite aura un numéro plus grand et sera relevé la fois suivante.
        uint? highest = inbox.UidNext is { } next && next.Id > 1 ? next.Id - 1 : null;
        var incremental = afterUid is not null && uidValidity == validity;

        IList<UniqueId> found = incremental
            ? await inbox.SearchAsync(SearchQuery.Uids(new UniqueIdRange(new UniqueId(validity, afterUid!.Value + 1), UniqueId.MaxValue)), cancellationToken)
            : await inbox.SearchAsync(SearchQuery.DeliveredAfter(since.UtcDateTime.Date.AddDays(-1)), cancellationToken);

        // « n:* » renvoie toujours le dernier e-mail, même déjà relevé : on ne garde que les nouveaux.
        var ordered = found.Where(u => !incremental || u.Id > afterUid).OrderBy(u => u.Id).ToList();
        List<UniqueId> selected;
        uint? scannedUpTo;
        if (incremental)
        {
            // Beaucoup d'e-mails d'un coup : les plus anciens d'abord, la suite au prochain relevé.
            selected = ordered.Take(max).ToList();
            scannedUpTo = ordered.Count > max ? selected[^1].Id : highest;
        }
        else
        {
            // Premier relevé : les plus récents seulement.
            selected = ordered.Skip(Math.Max(0, ordered.Count - max)).ToList();
            scannedUpTo = highest;
        }

        var mails = new List<FetchedMail>();
        if (selected.Count == 0)
        {
            return new FetchResult(validity, mails, scannedUpTo);
        }

        var sizes = (await inbox.FetchAsync(selected, MessageSummaryItems.UniqueId | MessageSummaryItems.Size, cancellationToken))
            .ToDictionary(s => s.UniqueId.Id, s => (long)(s.Size ?? 0));
        foreach (var uid in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sizes.TryGetValue(uid.Id, out var size) && size > MailReader.MaxFileBytes)
            {
                continue;
            }

            // Lecture seule (EXAMINE) : l'e-mail reste « non lu » dans la messagerie.
            using var mime = await inbox.GetMessageAsync(uid, cancellationToken);
            var message = MailReader.FromMime(mime);
            if (message.MessageId is null)
            {
                message = message with { MessageId = $"imap-{validity}-{uid.Id}" };
            }

            mails.Add(new FetchedMail(uid.Id, message, IsBulk(mime)));
        }

        return new FetchResult(validity, mails, scannedUpTo);
    }

    private static async Task<T> Run<T>(MailAccount account, Func<ImapClient, Task<T>> work, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(account.Address) || string.IsNullOrEmpty(account.Password))
        {
            throw new MailSyncException("Indiquez l'adresse et le mot de passe affichés par Proton Mail Bridge.");
        }

        if (account.Port is < 1 or > 65535)
        {
            throw new MailSyncException("Le port indiqué n'est pas valable (Bridge utilise 1143 par défaut).");
        }

        using var client = new ImapClient
        {
            Timeout = (int)Timeout.TotalMilliseconds,
            CheckCertificateRevocation = false,
            ServerCertificateValidationCallback = (_, _, _, errors) => AcceptCertificate(Host, errors),
        };

        try
        {
            await ConnectAsync(client, account.Port, cancellationToken);
            await client.AuthenticateAsync(account.Address.Trim(), account.Password, cancellationToken);
            return await work(client);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is not MailSyncException)
        {
            throw Translate(e);
        }
        finally
        {
            if (client.IsConnected)
            {
                try
                {
                    await client.DisconnectAsync(true, CancellationToken.None);
                }
                catch (Exception)
                {
                    // La connexion se ferme de toute façon.
                }
            }
        }
    }

    /// <summary>Bridge propose STARTTLS (par défaut) ou SSL : on essaie l'un puis l'autre, jamais sans chiffrement.</summary>
    private static async Task ConnectAsync(ImapClient client, int port, CancellationToken cancellationToken)
    {
        try
        {
            await client.ConnectAsync(Host, port, SecureSocketOptions.StartTls, cancellationToken);
        }
        catch (Exception first) when (first is NotSupportedException or SslHandshakeException or ImapProtocolException or IOException
                                          && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (client.IsConnected)
                {
                    await client.DisconnectAsync(false, cancellationToken);
                }

                await client.ConnectAsync(Host, port, SecureSocketOptions.SslOnConnect, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Les deux modes échouent : la première erreur (mode par défaut de Bridge) est la plus parlante.
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(first);
            }
        }
    }

    private static MailSyncException Translate(Exception e) => e switch
    {
        SocketException or ServiceNotConnectedException
            => new MailSyncException("Proton Mail Bridge ne répond pas sur cet ordinateur. Ouvrez Bridge, vérifiez que votre compte y est connecté, puis réessayez.", e),
        AuthenticationException
            => new MailSyncException("Bridge a refusé l'adresse ou le mot de passe. Utilisez le mot de passe affiché dans Bridge (ce n'est pas celui de votre compte Proton).", e),
        SslHandshakeException
            => new MailSyncException("La connexion chiffrée avec Bridge a échoué. Vérifiez le port et le mode de connexion indiqués dans Bridge.", e),
        TimeoutException or OperationCanceledException
            => new MailSyncException("Proton Mail Bridge met trop de temps à répondre. Réessayez dans un instant.", e),
        NotSupportedException
            => new MailSyncException("Ce port ne correspond pas à Proton Mail Bridge. Bridge utilise 1143 par défaut.", e),
        ImapProtocolException or ImapCommandException or IOException
            => new MailSyncException("La connexion avec Bridge a été interrompue. Réessayez dans un instant.", e),
        _ => new MailSyncException("Le relevé des e-mails a échoué. Réessayez dans un instant ; si cela continue, redémarrez Proton Mail Bridge.", e),
    };
}
