using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using Cabinet.Mail;
using MailMessage = Cabinet.Core.MailMessage;
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

    /// <summary>Court, pour essayer vite l'autre mode de connexion (STARTTLS ou SSL) quand le premier ne répond pas.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Le mode qui a marché la dernière fois (STARTTLS par défaut dans Bridge).</summary>
    private SecureSocketOptions _preferredMode = SecureSocketOptions.StartTls;

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

    /// <summary>
    /// Toujours une date plancher, même en relevé suivi : si Bridge recharge toute la boîte (première synchronisation,
    /// réparation), les vieux e-mails reçoivent de nouveaux numéros mais ne sont pas importés.
    /// </summary>
    private static SearchQuery Query(bool incremental, uint validity, uint? afterUid, DateTimeOffset since)
    {
        var recent = SearchQuery.DeliveredAfter(since.UtcDateTime.Date.AddDays(-1));
        return incremental
            ? SearchQuery.Uids(new UniqueIdRange(new UniqueId(validity, afterUid!.Value + 1), UniqueId.MaxValue)).And(recent)
            : recent;
    }

    private async Task<FetchResult> FetchInboxAsync(IMailFolder inbox, uint? afterUid, uint? uidValidity, DateTimeOffset since, int max, CancellationToken cancellationToken)
    {
        await inbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
        var validity = inbox.UidValidity;
        // Lu à l'ouverture : un e-mail arrivé ensuite aura un numéro plus grand et sera relevé la fois suivante.
        uint? highest = inbox.UidNext is { } next && next.Id > 1 ? next.Id - 1 : null;
        var incremental = afterUid is not null && uidValidity == validity;

        var found = await inbox.SearchAsync(Query(incremental, validity, afterUid, since), cancellationToken);

        // « n:* » renvoie toujours le dernier e-mail, même déjà relevé : on ne garde que les nouveaux.
        var ordered = found.Where(u => !incremental || u.Id > afterUid).OrderBy(u => u.Id).ToList();
        List<UniqueId> selected;
        uint? scannedUpTo;
        var complete = true;
        if (incremental)
        {
            // Beaucoup d'e-mails d'un coup (retour de congés) : les plus anciens d'abord, la suite au prochain relevé.
            selected = ordered.Take(max).ToList();
            complete = ordered.Count <= max;
            scannedUpTo = complete ? highest : selected[^1].Id;
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
            return new FetchResult(validity, mails, scannedUpTo, complete);
        }

        foreach (var uid in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IMessageSummary? summary = null;
            try
            {
                // Taille et date d'arrivée d'abord, e-mail par e-mail : une erreur est attribuée au bon e-mail.
                summary = (await inbox.FetchAsync(new[] { uid }, MessageSummaryItems.UniqueId | MessageSummaryItems.Size | MessageSummaryItems.InternalDate, cancellationToken))
                    .FirstOrDefault();
                if (summary is null)
                {
                    continue;
                }

                if (summary.Size > MailReader.MaxFileBytes)
                {
                    mails.Add(new FetchedMail(uid.Id, Unreadable(summary, null, validity, uid), false, Unreadable: true));
                    continue;
                }

                // Lecture seule (EXAMINE) : l'e-mail reste « non lu » dans la messagerie.
                using var mime = await inbox.GetMessageAsync(uid, cancellationToken);
                var message = MailReader.FromMime(mime);
                if (message.MessageId is null)
                {
                    message = message with { MessageId = FallbackId(validity, uid) };
                }

                mails.Add(new FetchedMail(uid.Id, message, IsBulk(mime)));
            }
            catch (MessageNotFoundException)
            {
                // Supprimé entre-temps dans la messagerie : rien à relever.
            }
            catch (Exception e) when (e is ImapCommandException or FormatException)
            {
                // Bridge refuse de fournir cet e-mail : il apparaît quand même (objet, expéditeur si possible), et le relevé continue.
                var (envelope, lost) = await TryEnvelopeAsync(inbox, uid, cancellationToken);
                mails.Add(new FetchedMail(uid.Id, Unreadable(summary, envelope, validity, uid), false, Unreadable: true));
                if (lost)
                {
                    return new FetchResult(validity, mails, uid.Id, Complete: false);
                }
            }
            catch (Exception e) when (IsConnectionLoss(e) && !cancellationToken.IsCancellationRequested)
            {
                // La connexion a coupé sur cet e-mail. Première fois : nouvel essai au prochain relevé (ce qui précède est gardé).
                // Deuxième fois : il apparaît comme illisible et le relevé passe à la suite, pour ne jamais rester bloqué.
                if (Strike(validity, uid.Id) >= MaxStrikes)
                {
                    mails.Add(new FetchedMail(uid.Id, Unreadable(summary, null, validity, uid), false, Unreadable: true));
                    return new FetchResult(validity, mails, uid.Id, Complete: false);
                }

                if (mails.Count == 0)
                {
                    throw new MailSyncException("La connexion avec Bridge a été coupée pendant la lecture d'un e-mail. Nouvel essai au prochain relevé.", e);
                }

                return new FetchResult(validity, mails, uid.Id - 1, Complete: false);
            }
        }

        return new FetchResult(validity, mails, scannedUpTo, complete);
    }

    private const int MaxStrikes = 2;

    /// <summary>Les e-mails sur lesquels la connexion a déjà coupé (en mémoire, le temps que l'outil est ouvert).</summary>
    private readonly Dictionary<(uint Validity, uint Uid), int> _strikes = new();

    private int Strike(uint validity, uint uid)
    {
        lock (_strikes)
        {
            _strikes.TryGetValue((validity, uid), out var count);
            _strikes[(validity, uid)] = ++count;
            return count;
        }
    }

    private static bool IsConnectionLoss(Exception e)
        => e is ImapProtocolException or IOException or ServiceNotConnectedException or SocketException;

    private static async Task<(Envelope? Envelope, bool ConnectionLost)> TryEnvelopeAsync(IMailFolder inbox, UniqueId uid, CancellationToken cancellationToken)
    {
        try
        {
            var summaries = await inbox.FetchAsync(new[] { uid }, MessageSummaryItems.Envelope, cancellationToken);
            return (summaries.FirstOrDefault()?.Envelope, false);
        }
        catch (ImapCommandException)
        {
            return (null, false);
        }
        catch (Exception e) when (IsConnectionLoss(e) && !cancellationToken.IsCancellationRequested)
        {
            return (null, true);
        }
    }

    private static string FallbackId(uint validity, UniqueId uid) => $"imap-{validity}-{uid.Id}";

    /// <summary>Un e-mail sans texte (l'analyse dira qu'il est illisible), avec ce que l'on connaît de lui.</summary>
    private static MailMessage Unreadable(IMessageSummary? summary, Envelope? envelope, uint validity, UniqueId uid)
    {
        var sender = envelope?.From.Mailboxes.FirstOrDefault();
        var address = sender?.Address ?? "";
        var name = !string.IsNullOrWhiteSpace(sender?.Name) ? sender.Name : address.Length > 0 ? address : "Expéditeur inconnu";
        var subject = string.IsNullOrWhiteSpace(envelope?.Subject) ? "(e-mail illisible par l'outil : à ouvrir dans Proton Mail)" : envelope.Subject.Trim();
        var id = envelope?.MessageId?.Trim().Trim('<', '>');
        return new MailMessage(name, address, subject, envelope?.Date ?? summary?.InternalDate, "", Array.Empty<string>(), string.IsNullOrEmpty(id) ? FallbackId(validity, uid) : id);
    }

    private async Task<T> Run<T>(MailAccount account, Func<ImapClient, Task<T>> work, CancellationToken cancellationToken)
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
            Timeout = (int)ConnectTimeout.TotalMilliseconds,
            CheckCertificateRevocation = false,
            ServerCertificateValidationCallback = (_, _, _, errors) => AcceptCertificate(Host, errors),
        };

        try
        {
            await ConnectAsync(client, account.Port, cancellationToken);
            client.Timeout = (int)Timeout.TotalMilliseconds;
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

    /// <summary>
    /// Bridge propose STARTTLS (par défaut) ou SSL : on essaie le mode qui a marché la dernière fois, puis l'autre ;
    /// jamais sans chiffrement. Les erreurs de cette étape disent si Bridge est fermé ou si le port est faux.
    /// </summary>
    private async Task ConnectAsync(ImapClient client, int port, CancellationToken cancellationToken)
    {
        var first = _preferredMode;
        var second = first == SecureSocketOptions.StartTls ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
        try
        {
            await client.ConnectAsync(Host, port, first, cancellationToken);
        }
        catch (SocketException e)
        {
            throw new MailSyncException($"Rien ne répond sur le port {port} de cet ordinateur. Vérifiez que Proton Mail Bridge est ouvert et que le port est bien le port IMAP indiqué dans Bridge (1143 par défaut).", e);
        }
        catch (Exception firstError) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (client.IsConnected)
                {
                    await client.DisconnectAsync(false, cancellationToken);
                }

                await client.ConnectAsync(Host, port, second, cancellationToken);
                _preferredMode = second;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new MailSyncException($"Le port {port} ne répond pas comme la messagerie de Bridge. Recopiez le port IMAP indiqué dans Bridge (1143 par défaut, pas le port SMTP) ; si c'est déjà le bon, redémarrez Bridge.", firstError);
            }
        }
    }

    private static MailSyncException Translate(Exception e) => e switch
    {
        SocketException or ServiceNotConnectedException
            => new MailSyncException("Proton Mail Bridge ne répond plus. Vérifiez qu'il est ouvert, puis réessayez.", e),
        AuthenticationException
            => new MailSyncException("Bridge a refusé le nom d'utilisateur ou le mot de passe. Recopiez ceux affichés dans Bridge (ce n'est pas le mot de passe de votre compte Proton) et vérifiez que votre compte y est bien connecté.", e),
        TimeoutException or OperationCanceledException
            => new MailSyncException("Proton Mail Bridge met trop de temps à répondre. Réessayez dans un instant.", e),
        SslHandshakeException or ImapProtocolException or ImapCommandException or IOException
            => new MailSyncException("La connexion avec Bridge a été interrompue. Réessayez dans un instant.", e),
        _ => new MailSyncException("Le relevé des e-mails a échoué. Réessayez dans un instant ; si cela continue, redémarrez Proton Mail Bridge.", e),
    };
}
