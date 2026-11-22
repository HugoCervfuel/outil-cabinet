using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cabinet.Core;

/// <summary>Fournit la clé de chiffrement des données, conservée par le système (Trousseau macOS, DPAPI Windows).</summary>
public interface IKeyStore
{
    byte[] GetOrCreateKey();
}

/// <summary>
/// Stockage local chiffré (AES-256-GCM). Chaque fichier : version (1 octet), nonce (12), étiquette (16), données chiffrées.
/// Le nom du fichier est lié au chiffrement : un fichier déplacé ou modifié est refusé.
/// </summary>
public sealed class EncryptedStore
{
    private const byte FormatVersion = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly string _folder;
    private readonly Lazy<byte[]> _key;

    public EncryptedStore(string folder, IKeyStore keys)
    {
        _folder = folder;
        _key = new Lazy<byte[]>(keys.GetOrCreateKey);
    }

    public string Folder => _folder;

    public void Save<T>(string name, T value)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(_key.Value, TagSize))
        {
            aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(name));
        }

        CryptographicOperations.ZeroMemory(plain);

        var path = PathFor(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        using (var stream = File.Create(temp))
        {
            stream.WriteByte(FormatVersion);
            stream.Write(nonce);
            stream.Write(tag);
            stream.Write(cipher);
        }

        File.Move(temp, path, overwrite: true);
    }

    public T? Load<T>(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path))
        {
            return default;
        }

        try
        {
            var data = File.ReadAllBytes(path);
            if (data.Length < 1 + NonceSize + TagSize || data[0] != FormatVersion)
            {
                return default;
            }

            var nonce = data.AsSpan(1, NonceSize);
            var tag = data.AsSpan(1 + NonceSize, TagSize);
            var cipher = data.AsSpan(1 + NonceSize + TagSize);
            var plain = new byte[cipher.Length];
            using (var aes = new AesGcm(_key.Value, TagSize))
            {
                aes.Decrypt(nonce, cipher, tag, plain, Encoding.UTF8.GetBytes(name));
            }

            return JsonSerializer.Deserialize<T>(plain, Json);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
        {
            return default;
        }
    }

    public IEnumerable<string> List(string subfolder)
    {
        var directory = Path.Combine(_folder, subfolder);
        return Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*.bin").Select(f => subfolder + "/" + Path.GetFileNameWithoutExtension(f))
            : Enumerable.Empty<string>();
    }

    private string PathFor(string name) => Path.Combine(_folder, name.Replace('/', Path.DirectorySeparatorChar) + ".bin");
}

/// <summary>Le journal de l'utilisateur : dossiers, réglages et journées.</summary>
public sealed class Journal(EncryptedStore store)
{
    private const string DaysFolder = "jours";

    public List<Dossier> LoadDossiers() => store.Load<List<Dossier>>("dossiers") ?? new List<Dossier>();

    public void SaveDossiers(IEnumerable<Dossier> dossiers) => store.Save("dossiers", dossiers.ToList());

    public Settings LoadSettings() => store.Load<Settings>("reglages") ?? new Settings();

    public void SaveSettings(Settings settings) => store.Save("reglages", settings);

    public DayRecord LoadDay(DateOnly date) => store.Load<DayRecord>(DayName(date)) ?? DayRecord.Empty(date);

    public void SaveDay(DayRecord day) => store.Save(DayName(day.Date), day);

    /// <summary>Jours passés dont l'activité n'a pas été validée.</summary>
    public IReadOnlyList<DateOnly> UnvalidatedDaysBefore(DateOnly today)
        => store.List(DaysFolder)
            .Select(n => DateOnly.TryParseExact(n[(DaysFolder.Length + 1)..], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : (DateOnly?)null)
            .OfType<DateOnly>()
            .Where(d => d < today)
            .Select(LoadDay)
            .Where(day => !day.IsValidated && day.Segments.Count > 0)
            .Select(day => day.Date)
            .OrderBy(d => d)
            .ToList();

    /// <summary>Efface le détail brut de l'activité au-delà de la durée de conservation ; garde les temps validés.</summary>
    public void PurgeRawActivity(DateOnly today, int retentionDays)
    {
        var limit = today.AddDays(-retentionDays);
        foreach (var name in store.List(DaysFolder))
        {
            if (!DateOnly.TryParseExact(name[(DaysFolder.Length + 1)..], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || date >= limit)
            {
                continue;
            }

            var day = LoadDay(date);
            if (day.Segments.Count > 0)
            {
                SaveDay(day with { Segments = new List<Segment>() });
            }
        }
    }

    private static string DayName(DateOnly date) => $"{DaysFolder}/{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
}
