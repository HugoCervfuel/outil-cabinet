using Cabinet.Core;
using Cabinet.Platform;

namespace Cabinet.App;

/// <summary>Assemble le stockage chiffré, le suivi d'activité et la journée de travail.</summary>
internal sealed class AppServices : IDisposable
{
    private readonly FileStream _instanceLock;

    private AppServices(WorkSession session, FileStream instanceLock, bool demo)
    {
        Session = session;
        _instanceLock = instanceLock;
        Demo = demo;
    }

    public WorkSession Session { get; }

    public bool Demo { get; }

    /// <summary>Null si l'outil est déjà ouvert (une seule instance à la fois).</summary>
    public static AppServices? Create()
    {
        var demo = Environment.GetEnvironmentVariable("CABINET_DEMO") == "1";
        var folder = demo ? Path.Combine(Path.GetTempPath(), "OutilCabinet-demo") : PlatformServices.DataFolder();
        Directory.CreateDirectory(folder);

        FileStream instanceLock;
        try
        {
            instanceLock = new FileStream(Path.Combine(folder, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }

        IKeyStore keys = demo ? new FileKeyStore(Path.Combine(folder, "cle.bin")) : PlatformServices.CreateKeyStore(folder);
        var journal = new Journal(new EncryptedStore(folder, keys));
        IActivityProbe probe = demo ? new DemoProbe() : PlatformServices.CreateActivityProbe();
        if (demo)
        {
            DemoData.Seed(journal);
        }

        return new AppServices(new WorkSession(journal, probe, () => DateTimeOffset.Now), instanceLock, demo);
    }

    public void Dispose() => _instanceLock.Dispose();
}

/// <summary>Mode démonstration (variable CABINET_DEMO=1) : données fictives, pour les captures d'écran.</summary>
internal sealed class DemoProbe : IActivityProbe
{
    public ActiveWindow? ReadActiveWindow() => new("Microsoft Word", "Conclusions_MARTIN_v3.docx");

    public TimeSpan? IdleTime() => TimeSpan.Zero;
}

internal static class DemoData
{
    public static void Seed(Journal journal)
    {
        if (journal.LoadDossiers().Count > 0)
        {
            return;
        }

        var martin = Dossier.Create("MARTIN c/ Axa", new[] { "martin", "axa" }, "2026-0142");
        var dupont = Dossier.Create("DUPONT / SCI Les Lys", new[] { "dupont", "lys" }, "2026-0098");
        var bernard = Dossier.Create("BERNARD (divorce)", new[] { "bernard" }, "2025-0311");
        journal.SaveDossiers(new[] { martin, dupont, bernard });

        var today = DateOnly.FromDateTime(DateTime.Now);
        var start = new DateTimeOffset(today.ToDateTime(new TimeOnly(9, 0)), DateTimeOffset.Now.Offset);
        Segment S(int from, int minutes, string app, string title) => new(start.AddMinutes(from), start.AddMinutes(from + minutes), app, title, null);
        journal.SaveDay(new DayRecord(today, new List<Segment>
        {
            S(0, 62, "Microsoft Word", "Assignation_DUPONT_SCI_Lys.docx"),
            S(62, 14, "Microsoft Outlook", "RE: SCI Les Lys - pièces"),
            S(80, 38, "Aperçu", "Pièce 12 - Expertise Dupont.pdf"),
            S(130, 27, "Microsoft Outlook", "Martin / Axa : proposition"),
            S(160, 9, "Finder", "Documents"),
            S(170, 25, "Google Chrome", "Légifrance - Code des assurances"),
        }, null));
    }
}
