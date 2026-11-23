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

/// <summary>Un e-mail relevé, avec son numéro dans la boîte et l'indication « lettre d'information ».</summary>
public sealed record FetchedMail(uint Uid, MailMessage Message, bool IsBulk);

/// <param name="UidValidity">Identifie la boîte : s'il change, les numéros repartent de zéro.</param>
/// <param name="ScannedUpTo">Plus grand numéro examiné (e-mails anciens ou ignorés compris), pour ne pas les relire.</param>
public sealed record FetchResult(uint UidValidity, IReadOnlyList<FetchedMail> Mails, uint? ScannedUpTo = null);

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
public sealed record SyncReport(DateTimeOffset At, IReadOnlyList<Guid> AddedIds, int SkippedBulk, string? Error)
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
            return $"Relevé à {time} : {news}{bulk}.";
        }
    }
}

/// <summary>Où en est le relevé : la boîte et le dernier numéro examiné.</summary>
public sealed record SyncState(uint UidValidity, uint LastUid);

/// <summary>
/// Relève la boîte de réception et dépose les nouveaux e-mails dans le courrier. Lecture seule : rien n'est marqué lu,
/// déplacé ou supprimé dans la messagerie. Les lettres d'information sont ignorées.
/// </summary>
public sealed class MailboxSync
{
    public const int FirstRunDays = 7;
    public const int FirstRunMax = 50;
    public const int BatchMax = 100;

    private const string AccountName = "messagerie/compte";
    private const string StateName = "messagerie/etat";
    private const string NotConfiguredError = "La messagerie n'est pas encore reliée : renseignez-la dans les réglages.";
    private const string GenericError = "Le relevé des e-mails a échoué. Réessayez dans un instant ; si cela continue, redémarrez Proton Mail Bridge.";

    private readonly EncryptedStore _store;
    private readonly MailDesk _desk;
    private readonly IMailboxSource _source;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly object _gate = new();
    private MailAccount? _account;
    private SyncState? _state;

    public MailboxSync(EncryptedStore store, MailDesk desk, IMailboxSource source, Func<DateTimeOffset> clock)
    {
        _store = store;
        _desk = desk;
        _source = source;
        _clock = clock;
        _account = store.Load<MailAccount>(AccountName);
        _state = store.Load<SyncState>(StateName);
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

    public SyncReport? LastReport { get; private set; }

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
                LastReport = null;
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
                return Finish(Array.Empty<Guid>(), 0, NotConfiguredError);
            }

            SyncState? state;
            lock (_gate)
            {
                state = _state;
            }

            FetchResult result;
            try
            {
                result = await _source.FetchAsync(
                    account!,
                    state?.LastUid,
                    state?.UidValidity,
                    _clock() - TimeSpan.FromDays(FirstRunDays),
                    state is null ? FirstRunMax : BatchMax,
                    cancellationToken);
            }
            catch (MailSyncException e)
            {
                return Finish(Array.Empty<Guid>(), 0, e.Message);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return Finish(Array.Empty<Guid>(), 0, GenericError);
            }

            var added = new List<Guid>();
            var bulk = 0;
            try
            {
                foreach (var mail in result.Mails.OrderBy(m => m.Uid))
                {
                    if (mail.IsBulk)
                    {
                        bulk++;
                    }
                    else if (_desk.AddIfNew(mail.Message) is { } item)
                    {
                        added.Add(item.Id);
                    }
                }

                var sameMailbox = state is not null && state.UidValidity == result.UidValidity;
                var last = Math.Max(
                    sameMailbox ? state!.LastUid : 0,
                    Math.Max(result.ScannedUpTo ?? 0, result.Mails.Count > 0 ? result.Mails.Max(m => m.Uid) : 0));
                var next = new SyncState(result.UidValidity, last);
                lock (_gate)
                {
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
                return Finish(added, bulk, "Les nouveaux e-mails n'ont pas pu être enregistrés sur cet ordinateur. Vérifiez l'espace disque.");
            }

            return Finish(added, bulk, null);
        }
        finally
        {
            _running.Release();
        }
    }

    private SyncReport Finish(IReadOnlyList<Guid> added, int bulk, string? error)
    {
        var report = new SyncReport(_clock(), added, bulk, error);
        LastReport = report;
        return report;
    }
}
