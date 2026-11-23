using Cabinet.Core;

namespace Cabinet.MailSync;

/// <summary>
/// La boîte aux lettres à relever. Pour Proton Mail : l'adresse et le mot de passe affichés par Proton Mail Bridge
/// (pas le mot de passe du compte Proton). Enregistré chiffré sur cet ordinateur.
/// </summary>
public sealed record MailAccount(string Address, string Password, int Port = ProtonBridgeSource.DefaultPort, bool AutoFetch = true)
{
    /// <summary>Le mot de passe n'apparaît jamais dans un texte affiché ou journalisé.</summary>
    public override string ToString() => $"MailAccount {{ Address = {Address}, Port = {Port}, AutoFetch = {AutoFetch} }}";
}

/// <summary>
/// Un e-mail relevé, avec son numéro dans la boîte et l'indication « lettre d'information ».
/// Illisible : Bridge n'a pas pu le fournir ; il apparaît quand même dans le courrier (objet, expéditeur), sans texte.
/// </summary>
public sealed record FetchedMail(uint Uid, MailMessage Message, bool IsBulk, bool Unreadable = false);

/// <param name="UidValidity">Identifie la boîte : s'il change, les numéros repartent de zéro.</param>
/// <param name="ScannedUpTo">Plus grand numéro examiné (e-mails anciens ou ignorés compris), pour ne pas les relire.</param>
/// <param name="Complete">Faux si une partie reste à relever (trop d'e-mails d'un coup, connexion coupée) :
/// la date du relevé n'avance pas, pour que la suite ne soit pas écartée comme « trop ancienne ».</param>
public sealed record FetchResult(uint UidValidity, IReadOnlyList<FetchedMail> Mails, uint? ScannedUpTo = null, bool Complete = true);

/// <summary>Une source d'e-mails : Proton Mail Bridge, ou une source fictive pour les essais.</summary>
public interface IMailboxSource
{
    /// <summary>Les e-mails arrivés après <paramref name="afterUid"/> ; au premier relevé, ceux reçus depuis <paramref name="since"/>.</summary>
    Task<FetchResult> FetchAsync(MailAccount account, uint? afterUid, uint? uidValidity, DateTimeOffset since, int max, CancellationToken cancellationToken);

    /// <summary>Vérifie la connexion et le mot de passe, sans rien relever.</summary>
    Task TestAsync(MailAccount account, CancellationToken cancellationToken);
}

/// <summary>Un échec de relevé, avec un message en français destiné à l'avocat.</summary>
public sealed class MailSyncException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Le compte rendu d'un relevé.</summary>
public sealed record SyncReport(DateTimeOffset At, IReadOnlyList<Guid> AddedIds, int SkippedBulk, string? Error, int Unreadable = 0)
{
    public int Added => AddedIds.Count;

    /// <summary>Une phrase pour l'écran Courrier.</summary>
    public string Summary
    {
        get
        {
            if (Error is not null)
            {
                return Error;
            }

            var time = At.LocalDateTime.ToString("HH' h 'mm");
            var news = Added switch
            {
                0 => "aucun nouvel e-mail",
                1 => "1 nouvel e-mail",
                _ => $"{Added} nouveaux e-mails",
            };
            var bulk = SkippedBulk switch
            {
                0 => "",
                1 => ", 1 lettre d'information ignorée",
                _ => $", {SkippedBulk} lettres d'information ignorées",
            };
            var unreadable = Unreadable switch
            {
                0 => "",
                1 => " Un e-mail est illisible par l'outil : ouvrez-le dans Proton Mail.",
                _ => $" {Unreadable} e-mails sont illisibles par l'outil : ouvrez-les dans Proton Mail.",
            };
            return $"Relevé à {time} : {news}{bulk}.{unreadable}";
        }
    }
}

/// <summary>Où en est le relevé : la boîte, le dernier numéro examiné et l'heure du dernier relevé réussi.</summary>
public sealed record SyncState(uint UidValidity, uint LastUid, DateTimeOffset? SyncedAt = null);

/// <summary>
/// Relève la boîte de réception et dépose les nouveaux e-mails dans le courrier. Lecture seule : rien n'est marqué lu,
/// déplacé ou supprimé dans la messagerie. Les lettres d'information sont ignorées.
/// </summary>
public sealed class MailboxSync
{
    public const int FirstRunDays = 7;
    public const int FirstRunMax = 50;
    public const int BatchMax = 100;

    /// <summary>Marge prise avant le relevé précédent (e-mails datés un peu avant leur arrivée).</summary>
    public static readonly TimeSpan LookBackMargin = TimeSpan.FromDays(2);

    /// <summary>Nombre d'identifiants d'e-mails déjà importés gardés en mémoire, pour ne jamais les réimporter.</summary>
    public const int SeenCapacity = 5000;

    private const string AccountName = "messagerie/compte";
    private const string StateName = "messagerie/etat";
    private const string SeenName = "messagerie/vus";
    private const string NotConfiguredError = "La messagerie n'est pas encore reliée : renseignez-la dans les réglages.";
    private const string GenericError = "Le relevé des e-mails a échoué. Réessayez dans un instant ; si cela continue, redémarrez Proton Mail Bridge.";

    private readonly EncryptedStore _store;
    private readonly MailDesk _desk;
    private readonly IMailboxSource _source;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly object _gate = new();
    private readonly List<string> _seenOrder;
    private readonly HashSet<string> _seen;
    private MailAccount? _account;
    private SyncState? _state;
    private SyncReport? _lastReport;

    public MailboxSync(EncryptedStore store, MailDesk desk, IMailboxSource source, Func<DateTimeOffset> clock)
    {
        _store = store;
        _desk = desk;
        _source = source;
        _clock = clock;
        _account = store.Load<MailAccount>(AccountName);
        _state = store.Load<SyncState>(StateName);
        _seenOrder = store.Load<List<string>>(SeenName) ?? new List<string>();
        _seen = new HashSet<string>(_seenOrder, StringComparer.Ordinal);
    }

    public MailAccount? Account
    {
        get
        {
            lock (_gate)
            {
                return _account;
            }
        }
    }

    public bool IsConfigured => Account is { } a && !string.IsNullOrWhiteSpace(a.Address) && !string.IsNullOrEmpty(a.Password);

    /// <summary>Relevé automatique demandé et possible.</summary>
    public bool AutoFetch => IsConfigured && Account!.AutoFetch;

    public bool IsRunning => _running.CurrentCount == 0;

    public SyncReport? LastReport
    {
        get
        {
            lock (_gate)
            {
                return _lastReport;
            }
        }
    }

    /// <summary>Enregistre la boîte. Une autre adresse ou un autre port : le relevé repart des 7 derniers jours.</summary>
    public void SaveAccount(MailAccount account)
    {
        account = account with { Address = account.Address.Trim() };
        lock (_gate)
        {
            var otherMailbox = _account is null
                               || !string.Equals(_account.Address, account.Address, StringComparison.OrdinalIgnoreCase)
                               || _account.Port != account.Port;
            _store.Save(AccountName, account);
            _account = account;
            if (otherMailbox)
            {
                _store.Save<SyncState?>(StateName, null);
                _state = null;
                _lastReport = null;
            }
        }
    }

    /// <summary>Vérifie l'adresse, le mot de passe et le port avant de les enregistrer. Null si tout va bien.</summary>
    public async Task<string?> TestAsync(MailAccount account, CancellationToken cancellationToken)
    {
        try
        {
            await _source.TestAsync(account, cancellationToken);
            return null;
        }
        catch (MailSyncException e)
        {
            return e.Message;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return GenericError;
        }
    }

    /// <summary>Relève les nouveaux e-mails. Ne lève pas d'erreur : le compte rendu dit ce qui s'est passé.</summary>
    public async Task<SyncReport> SyncAsync(CancellationToken cancellationToken)
    {
        await _running.WaitAsync(cancellationToken);
        try
        {
            var account = Account;
            if (!IsConfigured)
            {
                return Finish(account, Array.Empty<Guid>(), 0, 0, NotConfiguredError);
            }

            SyncState? state;
            lock (_gate)
            {
                state = _state;
            }

            var startedAt = _clock();
            // Premier relevé : 7 jours. Ensuite, depuis le relevé précédent : même si Bridge recharge toute
            // la boîte (première synchronisation, réparation), les vieux e-mails ne sont pas importés.
            var since = state?.SyncedAt is { } previousRun ? previousRun - LookBackMargin : startedAt - TimeSpan.FromDays(FirstRunDays);

            FetchResult result;
            try
            {
                result = await _source.FetchAsync(
                    account!,
                    state?.LastUid,
                    state?.UidValidity,
                    since,
                    state is null ? FirstRunMax : BatchMax,
                    cancellationToken);
            }
            catch (MailSyncException e)
            {
                return Finish(account, Array.Empty<Guid>(), 0, 0, e.Message);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return Finish(account, Array.Empty<Guid>(), 0, 0, GenericError);
            }

            var added = new List<Guid>();
            var bulk = 0;
            var unreadable = 0;
            try
            {
                foreach (var mail in result.Mails.OrderBy(m => m.Uid))
                {
                    if (mail.IsBulk)
                    {
                        bulk++;
                        continue;
                    }

                    var id = mail.Message.MessageId;
                    if (id is not null && IsSeen(id))
                    {
                        // Déjà importé un jour (même retiré de la liste depuis) : jamais réimporté.
                        continue;
                    }

                    if (_desk.AddIfNew(mail.Message) is { } item)
                    {
                        added.Add(item.Id);
                        unreadable += mail.Unreadable ? 1 : 0;
                    }

                    if (id is not null)
                    {
                        MarkSeen(id);
                    }
                }

                var sameMailbox = state is not null && state.UidValidity == result.UidValidity;
                var last = Math.Max(
                    sameMailbox ? state!.LastUid : 0,
                    Math.Max(result.ScannedUpTo ?? 0, result.Mails.Count > 0 ? result.Mails.Max(m => m.Uid) : 0));
                var syncedAt = result.Complete ? startedAt : sameMailbox ? state!.SyncedAt : null;
                var next = new SyncState(result.UidValidity, last, syncedAt);
                lock (_gate)
                {
                    _store.Save(SeenName, _seenOrder);
                    // La boîte a pu changer dans les réglages pendant le relevé : on ne mélange pas les deux.
                    if (ReferenceEquals(_account, account))
                    {
                        _store.Save(StateName, next);
                        _state = next;
                    }
                }
            }
            catch (Exception)
            {
                // Disque plein, par exemple : les e-mails non enregistrés seront relus au prochain relevé, sans doublon.
                return Finish(account, added, bulk, unreadable, "Les nouveaux e-mails n'ont pas pu être enregistrés sur cet ordinateur. Vérifiez l'espace disque.");
            }

            return Finish(account, added, bulk, unreadable, null);
        }
        finally
        {
            _running.Release();
        }
    }

    private bool IsSeen(string id)
    {
        lock (_gate)
        {
            return _seen.Contains(id);
        }
    }

    private void MarkSeen(string id)
    {
        lock (_gate)
        {
            if (!_seen.Add(id))
            {
                return;
            }

            _seenOrder.Add(id);
            if (_seenOrder.Count > SeenCapacity)
            {
                _seen.Remove(_seenOrder[0]);
                _seenOrder.RemoveAt(0);
            }
        }
    }

    /// <summary>Le compte rendu n'est affiché que s'il concerne encore la boîte enregistrée.</summary>
    private SyncReport Finish(MailAccount? account, IReadOnlyList<Guid> added, int bulk, int unreadable, string? error)
    {
        var report = new SyncReport(_clock(), added, bulk, error, unreadable);
        lock (_gate)
        {
            if (ReferenceEquals(_account, account))
            {
                _lastReport = report;
            }
        }

        return report;
    }
}
