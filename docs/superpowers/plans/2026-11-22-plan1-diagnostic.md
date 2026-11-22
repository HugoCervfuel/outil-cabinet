# Plan 1 — Fondations et outil de diagnostic : plan de développement

> **Pour les agents qui exécutent ce plan :** SOUS-COMPÉTENCE REQUISE : utiliser superpowers:subagent-driven-development (recommandé) ou superpowers:executing-plans pour réaliser ce plan tâche par tâche. Les étapes utilisent des cases à cocher (`- [ ]`).

**Objectif :** livrer un exécutable Windows autonome, `Diagnostic-OutilCabinet.exe`. L'avocat le lance sur son poste. Il vérifie que le suivi d'activité, l'accès à Outlook classique et le dossier Documents fonctionneront, puis produit un rapport sans aucune donnée client.

**Architecture :** une bibliothèque `Cabinet.Diagnostic.Core` (net10.0, sans dépendance Windows) contient le modèle, les décodeurs, les verdicts, le rapport et l'orchestration. Elle est testée sous Linux. Une bibliothèque `Cabinet.Diagnostic.Windows` (net10.0-windows) implémente les sondes : Win32, registre, WMI et COM Outlook. Une application WPF `Cabinet.Diagnostic` affiche le parcours. Une intégration continue GitHub Actions sous Windows teste, publie l'exécutable et l'attache à une « release ».

**Pile technique :** .NET 10 (SDK 10.0.4xx), C#, WPF, xUnit 2.9, System.Management (WMI), COM Outlook en liaison tardive (`dynamic`), GitHub Actions `windows-latest`.

**Spécification :** `docs/specs/2026-11-22-etape1-temps-design.md` (§ 6.4 « Garde de sécurité d'Outlook », § 7, § 10). Ce plan est le premier des trois prévus par la spécification. Suivront le plan 2 (pastille, suivi, rattachement, récapitulatif, stockage, sauvegarde, installateur) et le plan 3 (Outlook), que le résultat de ce diagnostic oriente.

## Contraintes globales

- Cible : Windows 10/11 x64. Exécutable **autonome** en un seul fichier (`SelfContained`, `PublishSingleFile`, `EnableCompressionInSingleFile`), sans installation ni droits administrateur.
- .NET 10 : `net10.0` pour Core, `net10.0-windows` pour Windows et l'application. `global.json` fixe le SDK `10.0.100` avec `rollForward: latestFeature`.
- `Nullable` activé, `TreatWarningsAsErrors` à `true` dans `Directory.Build.props`.
- **Aucune connexion réseau.** Aucun assemblage de `src/` ne référence `System.Net.Http`, `System.Net.Sockets`, `System.Net.Requests`, `System.Net.WebClient`, `System.Net.WebSockets`, `System.Net.Mail` ou `System.Net.NetworkInformation`. Un test le vérifie pour chaque assemblage.
- **Aucune donnée client** n'est affichée, journalisée ni écrite dans le rapport. Les types de résultats des sondes ne portent **aucune chaîne libre**, à l'exception de `SafeText`, réservé aux textes système (version de Windows, d'Outlook, nom de l'antivirus). Le titre des fenêtres n'est jamais lu, seule sa longueur l'est. L'adresse d'expéditeur lue pour le test protégé est jetée immédiatement.
- **Outlook en lecture seule.** Rien n'est créé, modifié, déplacé ni envoyé. L'outil ne démarre jamais Outlook : s'il est fermé, il le signale.
- Les appels COM Outlook passent par un thread STA d'arrière-plan avec un délai maximal, et jamais par le thread de l'interface.
- Interface entièrement en français, avec les textes exacts fixés dans ce plan.
- Tests : uniquement des données fictives.

## Points de vigilance pour la revue

1. **Outlook ouvert mais occupé** (boîte de dialogue ouverte, synchronisation en cours) : l'appel COM bloque. Comportement attendu : abandon au bout du délai, verdict « le test n'a pas pu aller jusqu'au bout », sans figer l'interface. → test `RunAutomaticChecks_OutlookFactsBlock_ReturnsFailedConnectionWithinTimeout` (tâche 4).
2. **Boîte de réception sans e-mail ordinaire parmi les plus récents** (accusés de réception, invitations) : `NoMailToTest` doit mener à « non testé », jamais à « accès direct » ni à « module nécessaire ». → test `Outlook_NoMailToTest_IsNotTested` (tâche 3).
3. **WMI `SecurityCenter2` indisponible** (Windows Server, antivirus tiers mal déclaré) : l'antivirus est en état `Unknown`, sans plantage. → tests `Decode_Null_IsUnknown` (tâche 2) et `SystemProbe_Read_DoesNotThrow` (tâche 5, sous Windows).
4. **Variantes de chemins du dossier Documents** : casse différente, barre oblique finale, variable OneDrive absente, dossier « OneDrive - Cabinet ». La détection doit rester juste. → tests `Detect_*` (tâche 2).
5. **Textes système inattendus** (caractères spéciaux, longueur excessive, adresse e-mail glissée dans un nom de produit) : `SafeText` filtre et tronque. → tests `From_*` (tâche 1).

## Prérequis (avant la tâche 1, à faire par Claude avec l'utilisateur)

- L'utilisateur crée un dépôt **privé et vide** `outil-cabinet` sur son compte GitHub et donne son identifiant.
- Claude rattache le dépôt à la session en écriture (`add_repo`, accès `push`), puis initialise le dépôt local dans `/home/user/outil-cabinet` (y compris `docs/` et `maquettes/`), configure `origin`, et pousse la branche `main`. Le développement se fait sur la branche `plan1-diagnostic`.
- SDK local : `export DOTNET_ROOT=/opt/dotnet10x/usr/share/dotnet PATH=/opt/dotnet10x/usr/share/dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1` (SDK Microsoft 10.0.404, qui compile WPF pour Windows depuis Linux).

## Structure des fichiers

```
outil-cabinet/
  global.json
  Directory.Build.props
  .gitignore
  outil-cabinet.sln
  src/
    Cabinet.Diagnostic.Core/          (net10.0) modèle, décodeurs, verdicts, rapport, orchestration
      SafeText.cs
      Model.cs                        enums et records de résultats
      AntivirusStateDecoder.cs
      GuardPolicyDecoder.cs
      CloudSyncDetector.cs
      Verdicts.cs
      ReportFormatter.cs
      Probes.cs                       interfaces des sondes
      TimedCall.cs
      DiagnosticRunner.cs
    Cabinet.Diagnostic.Windows/       (net10.0-windows) sondes réelles
      NativeMethods.cs
      WindowsSystemProbe.cs
      WindowsWindowProbe.cs
      OutlookComProbe.cs
      WindowsFolderProbe.cs
    Cabinet.Diagnostic/               (net10.0-windows, WPF, WinExe) application
      App.xaml(.cs)  MainWindow.xaml(.cs)  app.manifest
  tests/
    Cabinet.Diagnostic.Core.Tests/    (net10.0) exécutés partout
      NetworkGuard.cs                 aide partagée (liée par les autres projets de tests)
    Cabinet.Diagnostic.Windows.Tests/ (net10.0-windows) exécutés seulement sous Windows (CI)
  .github/workflows/build.yml
```

---

### Task 1: squelette de la solution, `SafeText` et garde « aucun réseau »

**Fichiers :**
- Créer : `global.json`, `Directory.Build.props`, `.gitignore` (gabarit `dotnet new gitignore`), `outil-cabinet.sln`
- Créer : `src/Cabinet.Diagnostic.Core/Cabinet.Diagnostic.Core.csproj`, `src/Cabinet.Diagnostic.Core/SafeText.cs`
- Créer : `tests/Cabinet.Diagnostic.Core.Tests/Cabinet.Diagnostic.Core.Tests.csproj` (xunit 2.9.x, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk), `tests/Cabinet.Diagnostic.Core.Tests/NetworkGuard.cs`, `tests/Cabinet.Diagnostic.Core.Tests/SafeTextTests.cs`

**Interfaces :**
- Produit : `public readonly record struct SafeText` dans `Cabinet.Diagnostic.Core`, avec `string Value`, `bool IsEmpty`, `static SafeText From(string? raw)`, `static SafeText Empty`, `override string ToString() => Value`.
- Produit (tests) : `public static class NetworkGuard { public static void AssertNoNetworkReferences(System.Reflection.Assembly assembly); }`

- [ ] **Étape 1 : écrire les tests qui échouent**

```csharp
public class SafeTextTests
{
    [Fact] public void From_PlainSystemText_IsKept()
        => Assert.Equal("Windows 11 Pro 23H2 (build 22631)", SafeText.From("Windows 11 Pro 23H2 (build 22631)").Value);
    [Fact] public void From_AccentedLatinLetters_AreKept()
        => Assert.Equal("Sécurité Windows", SafeText.From("Sécurité Windows").Value);
    [Fact] public void From_DisallowedCharacters_AreRemoved()
        => Assert.Equal("Defender xy.fr", SafeText.From("Defender <x@y.fr>").Value);
    [Fact] public void From_WhitespaceRuns_AreCollapsedAndTrimmed()
        => Assert.Equal("a b", SafeText.From("  a \t\n b  ").Value);
    [Fact] public void From_LongText_IsTruncatedTo64()
        => Assert.Equal(64, SafeText.From(new string('a', 100)).Value.Length);
    [Fact] public void From_Null_IsEmpty()
        { var t = SafeText.From(null); Assert.True(t.IsEmpty); Assert.Equal("", t.Value); }
}
public class CoreNetworkGuardTests
{
    [Fact] public void CoreAssembly_HasNoNetworkReferences()
        => NetworkGuard.AssertNoNetworkReferences(typeof(SafeText).Assembly);
}
```

- [ ] **Étape 2 : lancer les tests et constater l'échec**

Commande : `dotnet test tests/Cabinet.Diagnostic.Core.Tests`
Attendu : échec de compilation (`SafeText` n'existe pas).

- [ ] **Étape 3 : implémenter**

`SafeText.From` : conserver les lettres et chiffres (`char.IsLetterOrDigit`) dont le code est inférieur ou égal à U+024F, l'espace et les caractères `.,()-_/+:`. Supprimer tout autre caractère, sans le remplacer. Les blancs deviennent des espaces, les suites d'espaces sont réduites à un seul, puis le texte est rogné et tronqué à 64 caractères.
`NetworkGuard` : échoue (avec `Assert.DoesNotContain`) si `assembly.GetReferencedAssemblies()` contient un nom de la liste des contraintes globales.
`Directory.Build.props` : `Nullable=enable`, `TreatWarningsAsErrors=true`, `ImplicitUsings=enable`, `LangVersion=latest`.

- [ ] **Étape 4 : lancer les tests, qui doivent passer**

Commande : `dotnet test tests/Cabinet.Diagnostic.Core.Tests`
Attendu : 7 tests réussis.

- [ ] **Étape 5 : valider (commit)**

```bash
git add -A && git commit -m "Squelette de la solution, SafeText et garde réseau"
```

---

### Task 2: décodeurs purs (antivirus, règle Outlook, synchronisation cloud)

**Fichiers :**
- Créer : `src/Cabinet.Diagnostic.Core/Model.cs` (enums de cette tâche), `AntivirusStateDecoder.cs`, `GuardPolicyDecoder.cs`, `CloudSyncDetector.cs`
- Test : `tests/Cabinet.Diagnostic.Core.Tests/DecoderTests.cs`

**Interfaces :**
- Produit :
```csharp
public enum AntivirusState { Unknown, ActiveUpToDate, ActiveOutdated, Disabled, None }
public enum ObjectModelGuardPolicy { NotSet, WarnWhenAntivirusInactive, AlwaysWarn, NeverWarn }
public enum CloudSync { None, OneDrive, OtherService }
public static class AntivirusStateDecoder {
    public static AntivirusState Decode(int? productState);
    public static AntivirusState Combine(IEnumerable<AntivirusState> states);
}
public static class GuardPolicyDecoder { public static ObjectModelGuardPolicy FromRegistryValue(int? value); }
public static class CloudSyncDetector {
    public static CloudSync Detect(string folderPath, IReadOnlyDictionary<string, string?> environment);
}
```

- [ ] **Étape 1 : écrire les tests qui échouent**

```csharp
[Theory]
[InlineData(397568, AntivirusState.ActiveUpToDate)]   // 0x061100
[InlineData(397584, AntivirusState.ActiveOutdated)]   // 0x061110
[InlineData(393472, AntivirusState.Disabled)]         // 0x060100
[InlineData(266240, AntivirusState.ActiveUpToDate)]   // 0x041000
[InlineData(401408, AntivirusState.Unknown)]          // 0x062000 (octet d'état inconnu)
public void Decode_KnownStates(int state, AntivirusState expected) => Assert.Equal(expected, AntivirusStateDecoder.Decode(state));
[Fact] public void Decode_Null_IsUnknown() => Assert.Equal(AntivirusState.Unknown, AntivirusStateDecoder.Decode(null));
[Fact] public void Combine_AnyUpToDateWins() => Assert.Equal(AntivirusState.ActiveUpToDate,
    AntivirusStateDecoder.Combine(new[] { AntivirusState.Disabled, AntivirusState.ActiveUpToDate }));
[Fact] public void Combine_Empty_IsNone() => Assert.Equal(AntivirusState.None, AntivirusStateDecoder.Combine(Array.Empty<AntivirusState>()));
[Fact] public void Combine_OutdatedBeforeDisabled() => Assert.Equal(AntivirusState.ActiveOutdated,
    AntivirusStateDecoder.Combine(new[] { AntivirusState.Disabled, AntivirusState.ActiveOutdated }));

[Theory]
[InlineData(0, ObjectModelGuardPolicy.WarnWhenAntivirusInactive)]
[InlineData(1, ObjectModelGuardPolicy.AlwaysWarn)]
[InlineData(2, ObjectModelGuardPolicy.NeverWarn)]
[InlineData(7, ObjectModelGuardPolicy.NotSet)]
[InlineData(null, ObjectModelGuardPolicy.NotSet)]
public void GuardPolicy_FromRegistryValue(int? v, ObjectModelGuardPolicy expected) => Assert.Equal(expected, GuardPolicyDecoder.FromRegistryValue(v));

static IReadOnlyDictionary<string, string?> Env(string? commercial = null, string? personal = null) => new Dictionary<string, string?>
    { ["OneDrive"] = personal, ["OneDriveCommercial"] = commercial, ["OneDriveConsumer"] = null };
[Fact] public void Detect_DocumentsUnderOneDriveCommercial_IsOneDrive() => Assert.Equal(CloudSync.OneDrive,
    CloudSyncDetector.Detect(@"C:\Users\ana\OneDrive - Cabinet\Documents", Env(commercial: @"C:\Users\ana\OneDrive - Cabinet")));
[Fact] public void Detect_IgnoresCaseAndTrailingSeparator() => Assert.Equal(CloudSync.OneDrive,
    CloudSyncDetector.Detect(@"c:\users\ana\onedrive - cabinet\documents\", Env(commercial: @"C:\Users\Ana\OneDrive - Cabinet\")));
[Fact] public void Detect_OneDriveSegmentWithoutVariable_IsOneDrive() => Assert.Equal(CloudSync.OneDrive,
    CloudSyncDetector.Detect(@"C:\Users\ana\OneDrive\Documents", Env()));
[Fact] public void Detect_LocalDocuments_IsNone() => Assert.Equal(CloudSync.None,
    CloudSyncDetector.Detect(@"C:\Users\ana\Documents", Env()));
[Fact] public void Detect_Dropbox_IsOtherService() => Assert.Equal(CloudSync.OtherService,
    CloudSyncDetector.Detect(@"D:\Dropbox\Dossiers", Env()));
```

- [ ] **Étape 2 : lancer les tests et constater l'échec**

Commande : `dotnet test tests/Cabinet.Diagnostic.Core.Tests --filter "FullyQualifiedName~DecoderTests"`
Attendu : échec de compilation.

- [ ] **Étape 3 : implémenter**

`Decode` : `s = productState & 0xFFFFFF`, `etat = (s >> 8) & 0xFF`, `defs = s & 0xFF`. Si `etat` vaut 0x10 ou 0x11 (actif) : `defs == 0x00` donne `ActiveUpToDate`, `defs == 0x10` donne `ActiveOutdated`, toute autre valeur donne `Unknown`. Si `etat` vaut 0x00 ou 0x01 : `Disabled`. Sinon : `Unknown`.
`Combine` : ordre de priorité `ActiveUpToDate` > `ActiveOutdated` > `Disabled` > `Unknown`. Une liste vide donne `None`.
`Detect` : normaliser (`/` remplacé par `\`, suppression des `\` finaux, comparaison insensible à la casse). Le résultat est `OneDrive` si le chemin est égal à l'une des trois variables, ou commence par elle suivie de `\`. Il est aussi `OneDrive` si un segment du chemin commence par `OneDrive`. Il est `OtherService` si un segment est égal à `Dropbox`, `Google Drive`, `Mon Drive`, `My Drive` ou `iCloudDrive`. Sinon, `None`.

- [ ] **Étape 4 : lancer les tests, qui doivent passer**

Commande : `dotnet test tests/Cabinet.Diagnostic.Core.Tests`
Attendu : tous les tests réussis.

- [ ] **Étape 5 : valider (commit)** — `git commit -am "Décodeurs antivirus, règle Outlook et synchronisation"` (après `git add` des nouveaux fichiers)

---

### Task 3: modèle du rapport et verdicts

**Fichiers :**
- Modifier : `src/Cabinet.Diagnostic.Core/Model.cs`
- Créer : `src/Cabinet.Diagnostic.Core/Verdicts.cs`
- Test : `tests/Cabinet.Diagnostic.Core.Tests/VerdictTests.cs`

**Interfaces :**
- Consomme : `SafeText`, `AntivirusState`, `ObjectModelGuardPolicy`, `CloudSync` (tâches 1 et 2).
- Produit :
```csharp
public enum CheckStatus { Ok, Warning, Failed }
public enum OfficeBitness { Unknown, X86, X64 }
public enum OutlookConnection { NotAttempted, Connected, NotRunning, Failed }
public enum GuardedAccessOutcome { Succeeded, Denied, TimedOut, NoMailToTest, Failed }
public sealed record SystemInfo(SafeText WindowsVersion, bool Is64Bit, SafeText AntivirusName, AntivirusState Antivirus);
public sealed record WindowSample(bool ProcessNameRead, int TitleLength);
public sealed record OutlookPresence(bool ClassicInstalled, OfficeBitness Bitness, ObjectModelGuardPolicy GuardPolicy);
public sealed record OutlookFacts(OutlookConnection Connection, SafeText Version, int? InboxItemCount, int? CalendarEventsNext7Days);
public sealed record FolderInfo(bool Exists, bool Writable, CloudSync Sync);
public sealed record DiagnosticReport(
    SystemInfo System, WindowSample? Window, TimeSpan? IdleTime,
    OutlookPresence OutlookPresence, OutlookFacts Outlook,
    GuardedAccessOutcome? GuardedAccess, bool? AlertShownByOutlook, FolderInfo Documents)
{
    public bool CanRunGuardedTest { get; }   // ClassicInstalled && Connection == Connected
}
public enum VerdictKind { TrackingOk, TrackingUnavailable, OutlookDirectOk, OutlookNeedsAddIn, OutlookNotRunning,
                          OutlookNotTested, OutlookNotFound, DocumentsLocal, DocumentsSynced, DocumentsNotWritable }
public sealed record Verdict(CheckStatus Status, VerdictKind Kind);
public static class Verdicts {
    public static Verdict Tracking(DiagnosticReport r);
    public static Verdict Outlook(DiagnosticReport r);
    public static Verdict Documents(DiagnosticReport r);
}
```

- [ ] **Étape 1 : écrire les tests qui échouent**

Une fabrique de test `TestData.Sample()` (fichier `tests/Cabinet.Diagnostic.Core.Tests/TestData.cs`, importée par `using static TestData;`) construit un rapport « tout va bien » : fenêtre lisible, inactivité 3 s, Outlook installé et connecté, `GuardedAccess = Succeeded`, `AlertShownByOutlook = false`, Documents local et accessible en écriture. Chaque test le modifie avec `with`.

```csharp
[Fact] public void Outlook_AllGood_IsDirectOk() => Assert.Equal(new Verdict(CheckStatus.Ok, VerdictKind.OutlookDirectOk), Verdicts.Outlook(Sample()));
[Fact] public void Outlook_NotInstalled_IsNotFound() => Assert.Equal(VerdictKind.OutlookNotFound,
    Verdicts.Outlook(Sample() with { OutlookPresence = Sample().OutlookPresence with { ClassicInstalled = false } }).Kind);
[Fact] public void Outlook_NotRunning_AsksToOpenOutlook() => Assert.Equal(new Verdict(CheckStatus.Warning, VerdictKind.OutlookNotRunning),
    Verdicts.Outlook(Sample() with { Outlook = Sample().Outlook with { Connection = OutlookConnection.NotRunning } }));
[Fact] public void Outlook_AlertShown_NeedsAddIn() => Assert.Equal(VerdictKind.OutlookNeedsAddIn,
    Verdicts.Outlook(Sample() with { AlertShownByOutlook = true }).Kind);
[Theory][InlineData(GuardedAccessOutcome.Denied)][InlineData(GuardedAccessOutcome.TimedOut)]
public void Outlook_DeniedOrTimedOut_NeedsAddIn(GuardedAccessOutcome o) => Assert.Equal(VerdictKind.OutlookNeedsAddIn,
    Verdicts.Outlook(Sample() with { GuardedAccess = o, AlertShownByOutlook = null }).Kind);
[Fact] public void Outlook_NoMailToTest_IsNotTested() => Assert.Equal(new Verdict(CheckStatus.Warning, VerdictKind.OutlookNotTested),
    Verdicts.Outlook(Sample() with { GuardedAccess = GuardedAccessOutcome.NoMailToTest }));
[Fact] public void Outlook_SucceededButUnanswered_IsNotTested() => Assert.Equal(VerdictKind.OutlookNotTested,
    Verdicts.Outlook(Sample() with { AlertShownByOutlook = null }).Kind);
[Fact] public void CanRunGuardedTest_FalseWhenNotConnected() => Assert.False(
    (Sample() with { Outlook = Sample().Outlook with { Connection = OutlookConnection.Failed } }).CanRunGuardedTest);
[Fact] public void Tracking_NoWindow_IsUnavailable() => Assert.Equal(new Verdict(CheckStatus.Failed, VerdictKind.TrackingUnavailable),
    Verdicts.Tracking(Sample() with { Window = null }));
[Fact] public void Tracking_NoIdle_IsUnavailable() => Assert.Equal(VerdictKind.TrackingUnavailable, Verdicts.Tracking(Sample() with { IdleTime = null }).Kind);
[Fact] public void Documents_Synced_IsWarning() => Assert.Equal(new Verdict(CheckStatus.Warning, VerdictKind.DocumentsSynced),
    Verdicts.Documents(Sample() with { Documents = new FolderInfo(true, true, CloudSync.OneDrive) }));
[Fact] public void Documents_NotWritable_IsFailed() => Assert.Equal(new Verdict(CheckStatus.Failed, VerdictKind.DocumentsNotWritable),
    Verdicts.Documents(Sample() with { Documents = new FolderInfo(true, false, CloudSync.None) }));
```

- [ ] **Étape 2 : lancer les tests et constater l'échec** — `dotnet test tests/Cabinet.Diagnostic.Core.Tests --filter "FullyQualifiedName~VerdictTests"` : échec de compilation.

- [ ] **Étape 3 : implémenter**

`Verdicts.Outlook` applique ce tableau dans l'ordre, et la première règle vraie l'emporte :

| # | Condition | Verdict |
|---|---|---|
| 1 | `!ClassicInstalled` | `Failed`, `OutlookNotFound` |
| 2 | `Connection == NotRunning` | `Warning`, `OutlookNotRunning` |
| 3 | `Connection != Connected` | `Warning`, `OutlookNotTested` |
| 4 | `AlertShownByOutlook == true` ou `GuardedAccess` ∈ {`Denied`, `TimedOut`} | `Warning`, `OutlookNeedsAddIn` |
| 5 | `GuardedAccess == Succeeded` et `AlertShownByOutlook == false` | `Ok`, `OutlookDirectOk` |
| 6 | sinon | `Warning`, `OutlookNotTested` |

`Tracking` : `Ok`/`TrackingOk` si `Window is { ProcessNameRead: true }` et `IdleTime != null`, sinon `Failed`/`TrackingUnavailable`.
`Documents` : `!Exists || !Writable` donne `Failed`/`DocumentsNotWritable`, `Sync != None` donne `Warning`/`DocumentsSynced`, sinon `Ok`/`DocumentsLocal`.

- [ ] **Étape 4 : lancer les tests, qui doivent passer** — `dotnet test tests/Cabinet.Diagnostic.Core.Tests` : tous réussis.
- [ ] **Étape 5 : valider (commit)** — « Modèle du rapport et verdicts »

---

### Task 4: rapport texte et orchestration (avec délai maximal)

**Fichiers :**
- Créer : `src/Cabinet.Diagnostic.Core/ReportFormatter.cs`, `Probes.cs`, `TimedCall.cs`, `DiagnosticRunner.cs`
- Test : `tests/Cabinet.Diagnostic.Core.Tests/ReportFormatterTests.cs`, `DiagnosticRunnerTests.cs`

**Interfaces :**
- Consomme : modèle et verdicts (tâche 3).
- Produit :
```csharp
public static class ReportFormatter {
    public static string VerdictSentence(VerdictKind kind);
    public static string ToText(DiagnosticReport report, DateTimeOffset generatedAt);
}
public interface ISystemProbe  { SystemInfo Read(); }
public interface IWindowProbe  { WindowSample? SampleForeground(); TimeSpan? IdleTime(); }
public interface IOutlookProbe { OutlookPresence DetectInstallation(); OutlookFacts ReadFacts(); GuardedAccessOutcome TryGuardedRead(); }
public interface IFolderProbe  { FolderInfo InspectDocuments(); }
public static class TimedCall { public static T Run<T>(Func<T> work, TimeSpan timeout, T onTimeout, bool sta); }
public sealed class DiagnosticRunner {
    public DiagnosticRunner(ISystemProbe system, IWindowProbe window, IOutlookProbe outlook, IFolderProbe folder,
                            TimeSpan outlookTimeout, TimeSpan guardedTimeout);
    public DiagnosticReport RunAutomaticChecks();
    public DiagnosticReport RunGuardedCheck(DiagnosticReport current);
    public static DiagnosticReport WithUserAnswer(DiagnosticReport current, bool alertShown);
}
```

**Textes exacts de `VerdictSentence` :**

| VerdictKind | Phrase |
|---|---|
| TrackingOk | `Suivi d'activité : possible sur cet ordinateur.` |
| TrackingUnavailable | `Suivi d'activité : impossible de lire la fenêtre active.` |
| OutlookDirectOk | `Outlook : accès direct possible, sans alerte.` |
| OutlookNeedsAddIn | `Outlook : il faudra installer le module dans Outlook.` |
| OutlookNotRunning | `Outlook : ouvrez Outlook, puis relancez le diagnostic.` |
| OutlookNotTested | `Outlook : le test n'a pas pu aller jusqu'au bout.` |
| OutlookNotFound | `Outlook : Outlook classique introuvable sur cet ordinateur.` |
| DocumentsLocal | `Dossier Documents : sur cet ordinateur, non synchronisé.` |
| DocumentsSynced | `Dossier Documents : synchronisé avec un service en ligne (à éviter pour les pièces).` |
| DocumentsNotWritable | `Dossier Documents : écriture impossible.` |

**Forme de `ToText`** : les lignes 1 à 3 sont exactement `Diagnostic — Outil de cabinet`, `Date : {generatedAt:dd/MM/yyyy HH:mm}` et `Ce rapport ne contient aucune donnée client.`. Viennent ensuite une ligne vide, puis les trois verdicts (suivi, Outlook, Documents), chacun préfixé par `[OK] `, `[!]  ` ou `[X]  ` selon le statut. Après une nouvelle ligne vide vient `Détails techniques`, puis une ligne `Libellé : valeur` par champ du rapport, dans l'ordre du record. Vocabulaire des valeurs :
- antivirus : `actif et à jour` / `actif, définitions anciennes` / `désactivé` / `aucun détecté` / `état inconnu` ;
- règle d'accès : `par défaut` / `alerte si antivirus inactif` / `alerte systématique` / `jamais d'alerte` ;
- connexion : `non tentée` / `réussie` / `Outlook fermé` / `échec` ;
- accès protégé : `réussi` / `refusé` / `sans réponse` / `aucun e-mail à tester` / `échec` / `non testé` ;
- alerte affichée : `oui` / `non` / `non renseigné` ;
- fenêtre active : `lisible` / `illisible` (la longueur du titre n'est **pas** écrite) ;
- nombres absents : `non lu`.

- [ ] **Étape 1 : écrire les tests qui échouent**

```csharp
// ReportFormatterTests (réutilise TestData.Sample() de la tâche 3 ; dates formatées avec CultureInfo.GetCultureInfo("fr-FR"))
[Fact] public void ToText_StartsWithFixedHeader() {
    var lines = ReportFormatter.ToText(TestData.Sample(), new DateTimeOffset(2026, 11, 22, 15, 4, 0, TimeSpan.Zero)).Split('\n');
    Assert.Equal("Diagnostic — Outil de cabinet", lines[0].TrimEnd('\r'));
    Assert.Equal("Date : 22/11/2026 15:04", lines[1].TrimEnd('\r'));
    Assert.Equal("Ce rapport ne contient aucune donnée client.", lines[2].TrimEnd('\r')); }
[Fact] public void ToText_ContainsVerdictLinesWithMarks() {
    var text = ReportFormatter.ToText(TestData.Sample() with { AlertShownByOutlook = true }, DateTimeOffset.Now);
    Assert.Contains("[OK] Suivi d'activité : possible sur cet ordinateur.", text);
    Assert.Contains("[!]  Outlook : il faudra installer le module dans Outlook.", text); }
[Fact] public void ToText_DoesNotWriteTitleLength() {
    var text = ReportFormatter.ToText(TestData.Sample() with { Window = new WindowSample(true, 4242) }, DateTimeOffset.Now);
    Assert.DoesNotContain("4242", text); }

// DiagnosticRunnerTests : sondes factices configurables (FakeSystem, FakeWindow, FakeOutlook, FakeFolder)
[Fact] public void RunAutomaticChecks_ThrowingSystemProbe_FallsBackToUnknown() {
    var r = Runner(system: new FakeSystem(throws: true)).RunAutomaticChecks();
    Assert.True(r.System.WindowsVersion.IsEmpty); Assert.Equal(AntivirusState.Unknown, r.System.Antivirus); }
[Fact] public void RunAutomaticChecks_OutlookNotInstalled_DoesNotReadFacts() {
    var outlook = new FakeOutlook(installed: false);
    var r = Runner(outlook: outlook).RunAutomaticChecks();
    Assert.Equal(0, outlook.ReadFactsCalls); Assert.Equal(OutlookConnection.NotAttempted, r.Outlook.Connection); }
[Fact] public void RunAutomaticChecks_OutlookFactsBlock_ReturnsFailedConnectionWithinTimeout() {
    var sw = Stopwatch.StartNew();
    var r = Runner(outlook: new FakeOutlook(installed: true, factsBlockForever: true), outlookTimeout: TimeSpan.FromMilliseconds(200)).RunAutomaticChecks();
    Assert.Equal(OutlookConnection.Failed, r.Outlook.Connection); Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2)); }
[Fact] public void RunGuardedCheck_Blocking_IsTimedOut() {
    var runner = Runner(outlook: new FakeOutlook(installed: true, guardedBlockForever: true), guardedTimeout: TimeSpan.FromMilliseconds(200));
    Assert.Equal(GuardedAccessOutcome.TimedOut, runner.RunGuardedCheck(runner.RunAutomaticChecks()).GuardedAccess); }
[Fact] public void RunGuardedCheck_NotConnected_LeavesReportUnchanged() {
    var runner = Runner(outlook: new FakeOutlook(installed: true, connection: OutlookConnection.NotRunning));
    var before = runner.RunAutomaticChecks();
    Assert.Same(before, runner.RunGuardedCheck(before)); }
[Fact] public void RunGuardedCheck_ThrowingProbe_IsFailed() {
    var runner = Runner(outlook: new FakeOutlook(installed: true, guardedThrows: true));
    Assert.Equal(GuardedAccessOutcome.Failed, runner.RunGuardedCheck(runner.RunAutomaticChecks()).GuardedAccess); }
[Fact] public void WithUserAnswer_SetsAnswer()
    => Assert.True(DiagnosticRunner.WithUserAnswer(TestData.Sample() with { AlertShownByOutlook = null }, true).AlertShownByOutlook);
```

- [ ] **Étape 2 : lancer les tests et constater l'échec** — `dotnet test tests/Cabinet.Diagnostic.Core.Tests` : échec de compilation.

- [ ] **Étape 3 : implémenter**

`TimedCall.Run` : exécute `work` sur un nouveau `Thread` avec `IsBackground = true`. Si `sta && OperatingSystem.IsWindows()`, il appelle `SetApartmentState(ApartmentState.STA)`. Il attend avec `Join(timeout)` et renvoie `onTimeout` si le travail n'est pas terminé. Une exception levée dans `work` est capturée et relancée chez l'appelant (`ExceptionDispatchInfo.Throw`).
`DiagnosticRunner` : chaque appel de sonde est protégé par `try/catch`. Valeurs de repli : `SystemInfo(SafeText.Empty, Environment.Is64BitOperatingSystem, SafeText.Empty, Unknown)`, fenêtre `null`, inactivité `null`, `OutlookPresence(false, Unknown, NotSet)`, `OutlookFacts(Failed, Empty, null, null)`, accès protégé `Failed`, `FolderInfo(false, false, None)`. `ReadFacts` n'est appelé que si `ClassicInstalled`, sinon on obtient `OutlookFacts(NotAttempted, Empty, null, null)`. Il passe par `TimedCall` (sta : true, `outlookTimeout`) avec `OutlookFacts(Failed, …)` en cas de délai dépassé. `TryGuardedRead` passe par `TimedCall` (sta : true, `guardedTimeout`) avec `TimedOut` en cas de délai dépassé. `RunGuardedCheck` renvoie `current` inchangé si `!current.CanRunGuardedTest`. Valeurs utilisées par l'application : `outlookTimeout` = 20 s, `guardedTimeout` = 120 s.

- [ ] **Étape 4 : lancer les tests, qui doivent passer** — `dotnet test tests/Cabinet.Diagnostic.Core.Tests` : tous réussis.
- [ ] **Étape 5 : valider (commit)** — « Rapport texte et orchestration du diagnostic »

---

### Task 5: sondes Windows

**Fichiers :**
- Créer : `src/Cabinet.Diagnostic.Windows/Cabinet.Diagnostic.Windows.csproj` (net10.0-windows, `AllowUnsafeBlocks`, `EnableWindowsTargeting`, référence `System.Management` 10.0.x, référence de projet Core)
- Créer : `NativeMethods.cs`, `WindowsSystemProbe.cs`, `WindowsWindowProbe.cs`, `OutlookComProbe.cs`, `WindowsFolderProbe.cs`
- Test : `tests/Cabinet.Diagnostic.Windows.Tests/` (net10.0-windows, lie `NetworkGuard.cs` avec `<Compile Include=... Link=...>`), `ProbeSmokeTests.cs`

**Interfaces :**
- Consomme : `ISystemProbe`, `IWindowProbe`, `IOutlookProbe`, `IFolderProbe`, décodeurs (tâches 2 et 4).
- Produit : `WindowsSystemProbe : ISystemProbe`, `WindowsWindowProbe : IWindowProbe`, `OutlookComProbe : IOutlookProbe`, `WindowsFolderProbe : IFolderProbe`, chacun avec un constructeur sans paramètre.

- [ ] **Étape 1 : écrire les tests (exécutés sur la machine Windows de la CI, sans Office)**

```csharp
[Fact] public void SystemProbe_Read_DoesNotThrow() { var s = new WindowsSystemProbe().Read(); Assert.False(s.WindowsVersion.IsEmpty); Assert.True(s.Is64Bit); }
[Fact] public void WindowProbe_DoesNotThrow() { var p = new WindowsWindowProbe(); _ = p.SampleForeground(); _ = p.IdleTime(); }
[Fact] public void OutlookProbe_WithoutOffice_ReportsNotInstalled() => Assert.False(new OutlookComProbe().DetectInstallation().ClassicInstalled);
[Fact] public void OutlookProbe_WithoutOutlookProcess_ReportsNotRunning() => Assert.Equal(OutlookConnection.NotRunning, new OutlookComProbe().ReadFacts().Connection);
[Fact] public void FolderProbe_Documents_IsWritable() { var f = new WindowsFolderProbe().InspectDocuments(); Assert.True(f.Exists); Assert.True(f.Writable); }
[Fact] public void WindowsAssembly_HasNoNetworkReferences() => NetworkGuard.AssertNoNetworkReferences(typeof(OutlookComProbe).Assembly);
```

- [ ] **Étape 2 : vérifier que le projet de tests compile depuis Linux sans les sondes** — `dotnet build tests/Cabinet.Diagnostic.Windows.Tests` : échec de compilation attendu (types manquants).

- [ ] **Étape 3 : implémenter les sondes**

- `NativeMethods` (`[LibraryImport]`, user32) : `GetForegroundWindow`, `GetWindowTextLengthW`, `GetWindowThreadProcessId`, `GetLastInputInfo` (struct `LASTINPUTINFO { uint cbSize; uint dwTime; }`). **Jamais** `GetWindowText`.
- `WindowsWindowProbe` : renvoie `null` si aucune fenêtre n'est au premier plan. `ProcessNameRead` vaut vrai si `Process.GetProcessById(pid).ProcessName` est lu sans exception (la valeur est jetée). `IdleTime` = `Environment.TickCount64` moins `dwTime`, en tenant compte du passage du compteur 32 bits à zéro. La valeur est `null` en cas d'échec.
- `WindowsSystemProbe` : la version vient du registre `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion`. Le nom est « Windows 11 » si `CurrentBuild` ≥ 22000, sinon « Windows 10 », suivi de `DisplayVersion` et de `(build N)`, le tout passé par `SafeText.From`. L'antivirus vient de la requête WMI `root\SecurityCenter2` : `SELECT displayName, productState FROM AntiVirusProduct`. On combine les états avec `Combine` et on retient le nom du premier produit `ActiveUpToDate`, sinon du premier produit. Une `ManagementException` ou un espace de noms absent donnent `Unknown` avec un nom vide.
- `OutlookComProbe` :
  - `DetectInstallation` : `ClassicInstalled` si la clé `HKCR\Outlook.Application\CLSID` existe. L'architecture se lit dans `HKLM\SOFTWARE\Microsoft\Office\ClickToRun\Configuration` (valeur `Platform`), puis dans `HKLM\SOFTWARE\Microsoft\Office\16.0\Outlook` (valeur `Bitness`) : `x64` ou `x86`. La règle d'accès est le premier DWORD `ObjectModelGuard` trouvé dans `HKLM\SOFTWARE\Microsoft\Office\16.0\Outlook\Security`, puis dans `HKLM\SOFTWARE\Microsoft\Office\ClickToRun\REGISTRY\MACHINE\Software\Microsoft\Office\16.0\Outlook\Security`, passé à `GuardPolicyDecoder`.
  - `ReadFacts` : si aucun processus `OUTLOOK` n'existe, renvoie `NotRunning` **sans** appel COM. Sinon : `Activator.CreateInstance(Type.GetTypeFromProgID("Outlook.Application")!)` en `dynamic`, `Version` via `SafeText.From`, puis `GetNamespace("MAPI")`. On lit `GetDefaultFolder(6).Items.Count` pour la boîte de réception. Pour l'agenda, sur `GetDefaultFolder(9).Items` : `IncludeRecurrences = true`, `Sort("[Start]")`, `Restrict` avec `[Start] >= '{début:g}' AND [Start] < '{fin:g}'` (culture courante, du moment présent à +7 jours), puis on compte en parcourant les éléments, avec un maximum de 500. Chaque objet COM obtenu est libéré dans un `finally` (`Marshal.ReleaseComObject`).
  - `TryGuardedRead` : boîte de réception, `Items.Sort("[ReceivedTime]", true)`, examen des 20 premiers éléments. Au premier élément dont `Class == 43` (e-mail), on lit `SenderEmailAddress` dans une variable locale, on garde seulement sa longueur et on renvoie `Succeeded` si elle est supérieure à 0, `Failed` sinon. Une `COMException` pendant cette lecture donne `Denied`. Si aucun e-mail n'est trouvé : `NoMailToTest`. Si Outlook est fermé : `Failed`.
- `WindowsFolderProbe` : chemin `Environment.GetFolderPath(MyDocuments)`. Écriture testée en créant puis supprimant `cabinet-diagnostic-{Guid:N}.tmp`. Synchronisation via `CloudSyncDetector.Detect` avec les variables `OneDrive`, `OneDriveCommercial` et `OneDriveConsumer`.

- [ ] **Étape 4 : compiler depuis Linux** — `dotnet build tests/Cabinet.Diagnostic.Windows.Tests -c Release` : `0 Error(s)`. (L'exécution des tests a lieu en CI à la tâche 7.)
- [ ] **Étape 5 : valider (commit)** — « Sondes Windows du diagnostic »

---

### Task 6: application WPF

**Fichiers :**
- Créer : `src/Cabinet.Diagnostic/Cabinet.Diagnostic.csproj` (`WinExe`, net10.0-windows, `UseWPF`, `AssemblyName=Diagnostic-OutilCabinet`, `RuntimeIdentifier=win-x64`, `SelfContained`, `PublishSingleFile`, `EnableCompressionInSingleFile`, `IncludeNativeLibrariesForSelfExtract`, `ApplicationManifest=app.manifest`), `app.manifest` (`asInvoker`, DPI `PerMonitorV2`), `App.xaml(.cs)`, `MainWindow.xaml(.cs)`
- Modifier : `tests/Cabinet.Diagnostic.Windows.Tests` (référence de l'application, test `AppAssembly_HasNoNetworkReferences`)

**Interfaces :**
- Consomme : `DiagnosticRunner`, `Verdicts`, `ReportFormatter` (tâche 4) ; les quatre sondes (tâche 5).

**Parcours et textes exacts** (une seule fenêtre de 560×640, style de la maquette : fond `#F6F4EF`, cartes blanches arrondies, accent `#1F3A5F`, police Segoe UI) :

1. Titre `Diagnostic de l'outil de cabinet`. Texte : `Ce diagnostic vérifie que l'outil pourra fonctionner sur votre ordinateur. Il ne lit, n'enregistre et n'envoie aucune donnée client. Ouvrez Outlook avant de commencer.` Bouton `Lancer le diagnostic`.
2. Pendant les vérifications automatiques (`Task.Run`), le bouton est désactivé et le texte `Vérification en cours…` s'affiche. Viennent ensuite trois lignes de verdict (pastille verte, ambre ou rouge suivie de `VerdictSentence`).
3. Si `CanRunGuardedTest` : une carte s'affiche avec le texte `Dernier test : l'outil va lire l'adresse d'un expéditeur dans Outlook, sans l'afficher ni la conserver. Si Outlook affiche une fenêtre demandant d'autoriser l'accès, cliquez sur « Refuser ».` et le bouton `Lancer ce test`. Après le test, la question `Outlook a-t-il affiché une fenêtre d'alerte ?` s'affiche avec les boutons `Oui` et `Non`, qui appellent `WithUserAnswer`. Le verdict Outlook est alors recalculé.
4. Fin : boutons `Copier le rapport` (`Clipboard.SetText(ToText(...))`) et `Enregistrer le rapport…` (`SaveFileDialog`, nom proposé `diagnostic-outil-cabinet.txt`, UTF-8 avec BOM). Texte : `Envoyez ce rapport au développeur : il ne contient aucune donnée client.`

Tous les boutons sont désactivés pendant un test en cours. Fermer la fenêtre pendant un test termine l'application sans erreur (threads d'arrière-plan).

- [ ] **Étape 1 : ajouter le test de garde réseau de l'application** dans `ProbeSmokeTests.cs` : `NetworkGuard.AssertNoNetworkReferences(typeof(Cabinet.Diagnostic.MainWindow).Assembly)`.
- [ ] **Étape 2 : implémenter l'application** selon le parcours ci-dessus.
- [ ] **Étape 3 : publier depuis Linux** — `dotnet publish src/Cabinet.Diagnostic -c Release -o publish` : attendu `publish/Diagnostic-OutilCabinet.exe`, de taille inférieure à 100 Mo.
- [ ] **Étape 4 : valider (commit)** — « Application WPF du diagnostic »

---

### Task 7: intégration continue Windows et publication

**Fichiers :**
- Créer : `.github/workflows/build.yml`

**Contenu attendu du workflow :**
- Déclencheurs : `push` sur toutes les branches, et `push` des étiquettes `diagnostic-v*`.
- Tâche `build` sur `windows-latest`, avec `permissions: contents: write`. Étapes : `actions/checkout@v4` ; `actions/setup-dotnet@v4` avec `dotnet-version: 10.0.x` ; variables `DOTNET_CLI_TELEMETRY_OPTOUT=1` et `DOTNET_NOLOGO=1` ; `dotnet test -c Release` (lance les deux projets de tests) ; `dotnet publish src/Cabinet.Diagnostic -c Release -o publish` ; `actions/upload-artifact@v4` pour `publish/Diagnostic-OutilCabinet.exe`.
- Seulement pour une étiquette `diagnostic-v*` : `softprops/action-gh-release@v2`, qui attache `publish/Diagnostic-OutilCabinet.exe` à la release.

- [ ] **Étape 1 : écrire le workflow.**
- [ ] **Étape 2 : pousser la branche** — `git push -u origin plan1-diagnostic`. Attendu : l'exécution GitHub Actions réussit, tous les tests passent sous Windows et l'artefact est présent. Vérification avec `gh run list --branch plan1-diagnostic` si `gh` est authentifié, sinon en demandant à l'utilisateur de consulter l'onglet *Actions*.
- [ ] **Étape 3 : corriger jusqu'au vert**, en relançant après chaque correction.
- [ ] **Étape 4 : fusionner dans `main`, étiqueter `diagnostic-v0.1.0` et pousser l'étiquette.** Attendu : une release `diagnostic-v0.1.0` contenant `Diagnostic-OutilCabinet.exe`.
- [ ] **Étape 5 : remettre à l'utilisateur** le lien de la release et le mode d'emploi en français. Windows affichera « Windows a protégé votre ordinateur » : il faut cliquer sur *Informations complémentaires*, puis sur *Exécuter quand même*, car l'exécutable n'est pas signé. Ensuite, ouvrir Outlook, lancer le diagnostic et renvoyer le rapport.
