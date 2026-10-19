# Contrats d'architecture — Navigation « C » + soumission « C+ » (WP1)

> Auteur : Yoda (architecte) — lot **WP1** du plan `docs\plans\nav-c-implementation-plan.md` — statut : **ARRÊTÉ** (consommé par WP3…WP16).
> Décisions associées : `docs\plans\nav-c-adr.md`.
> Ce document ne modifie aucun code. Les signatures ci-dessous sont **normatives** : un lot qui doit s'en écarter le signale dans son rapport (« demande de changement ») au lieu de diverger silencieusement.

**Convention.** Tout nom de type, membre, fichier ou clé qui n'existe pas encore dans le code est marqué **« NOUVEAU »**. Tout ce qui n'est pas marqué existe déjà (vérifié par grep dans le dépôt). Quand le plan cite un nom qui n'existe pas tel quel, l'écart est signalé dans « Écarts de noms » (§0).

---

## 0. Existant référencé (vérifié) et écarts de noms

| Élément existant                              | Emplacement réel                                                                                                                       |
| --------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------- |
| `WeekProposal`, `DayProposal`, `DayTaskHours`, `DayFlag {Ok, UnderTarget, OverTarget}` | `src\TimeSheetHelper.Engine\Proposals\WeekProposal.cs` — ns `TimeSheetHelper.Engine.Proposals`                                      |
| `WeekService.GetWeekAsync(DateOnly monday, CancellationToken)`                         | `src\TimeSheetHelper.Engine\WeekService.cs` — ns `TimeSheetHelper.Engine` (singleton)                                              |
| `WeekProposalBuilder.BuildAsync(weekStart, classifyMissing = true, ct)`                | `TimeSheetHelper.Engine` (singleton, `AddTimesheetEngine`)                                                                          |
| `EngineOptions.TargetHoursPerDay` (`decimal`, défaut `8m`, clé `Engine:TargetHoursPerDay`) | `src\TimeSheetHelper.Engine\EngineOptions.cs`                                                                                     |
| `SubmissionReviewService(WeekProposalBuilder, ISubmissionGapService)` : `AnalyzeAsync(TimesheetDraft, ForecastFillReport?, ct)`, `static ContextOf(WeekProposal)` | `src\TimeSheetHelper.App\Services\SubmissionReviewService.cs` — ns `TimeSheetHelper.App.Services` |
| `SubmissionAnalysis(WeekStart, Gaps, Summary)`, `SubmissionGap { Date?, Severity, IsBlocking }`, `SubmissionGapSeverity` | `src\TimeSheetHelper.Forecast\Submission\SubmissionGap.cs` — ns `TimeSheetHelper.Forecast.Submission` |
| `TimesheetDraft { Id, WeekStart, SubmittedUtc, Entries }`, `TimesheetEntry { Id, TimesheetDraftId, Date, ForecastTaskId, Hours, Notes }` | `src\TimeSheetHelper.Core\Timesheets\TimesheetDraft.cs` — ns `TimeSheetHelper.Core.Timesheets` |
| `ForecastTimesheetWriter(ForecastApi, IDbContextFactory<TimeSheetDbContext>, ILogger<…>)` : `FillWeekAsync(draft, ForecastSubmitMode, days?, ignoredTaskIds?, ct)`, `SubmitForApprovalAsync(weekStart, ct)` | `src\TimeSheetHelper.Forecast\ForecastTimesheetWriter.cs` — ns `TimeSheetHelper.Forecast` |
| `ForecastToolRole { ListTasks, ListTimeEntries, CreateTimeEntry, CurrentUser }`, `ForecastToolResolver.Resolve(tools, role, overrides)`, `KnownName(role)` (internal) | `src\TimeSheetHelper.Forecast\ForecastToolResolver.cs` |
| `ForecastToolNames { ListTasks, ListTimeEntries, CreateTimeEntry, CurrentUser }` (`string?`) | `src\TimeSheetHelper.Forecast\ForecastOptions.cs` |
| `ForecastMcpSession` : `Tools`, `HasTool(string name)`, `Require(ForecastToolRole)`, `ReadTimeEntriesAsync(from, to, ct) → ForecastEntryRead(Entries, IsComplete, Detail)` | `src\TimeSheetHelper.Forecast\ForecastMcpSession.cs` |
| `ForecastRemoteEntry(TaskId?, TaskCode?, Date, Hours, PersonId?, ProjectId?, EntryId?, Notes?, ApprovalStatus?, Invoiced, ProjectName?)` | `src\TimeSheetHelper.Forecast\ForecastJson.cs` |
| `ForecastFillReport`, `ForecastSubmitMode {DryRun, Fill}`, `ForecastCellStatus {Planned, Ok, Mismatch, RowNotFound, Error, Locked, Unknown}`, `ForecastCellIssue`, `ForecastCellResult` | `src\TimeSheetHelper.Forecast\ForecastFillReport.cs` (+ fichiers voisins) |
| `StatusLevel {Idle, Ok, Warn, Danger}`, `SubmissionStatusBuilder`, `DaySelection` (`WeekOf`, `IsWholeWeek`, `Default`, `Prune`) | `src\TimeSheetHelper.App\Components\Shared\SubmissionStatus.cs`, `DaySelection.cs` — ns `TimeSheetHelper.App.Components.Shared` |
| Enregistrements DI de l'app                    | `src\TimeSheetHelper.App\AppServices.cs` → `AddTimeSheetHelper` (ligne `services.AddSingleton<SubmissionReviewService>();`) |
| Tokens CSS                                     | `src\TimeSheetHelper.App\wwwroot\app.css` (clair + sombre) : `--bg --fg --muted --border --accent --row-alt --idle --locked --ok --warn --danger --info --ok-bg --warn-bg --danger-bg --info-bg` |

**Écarts de noms (défaut §8.6 « utiliser le nom réel »)**

1. Le plan dit « `Program.cs` (1 ligne DI) ». Les services de l'app sont enregistrés dans **`AppServices.AddTimeSheetHelper`** (`src\TimeSheetHelper.App\AppServices.cs`), pas dans `Program.cs`. ⇒ Les lignes réservées (§5) sont dans `AppServices.cs` ; `Program.cs` n'est pas touché. Le propriétaire de `AppServices.cs` est Yoda (même règle que celle prévue pour `Program.cs`).
2. Le plan dit « `session.Has(role)` ». Le code n'a que `HasTool(string)` et `Require(ForecastToolRole)`. ⇒ `ForecastMcpSession.Has(ForecastToolRole)` est **NOUVEAU** (WP9).
3. Le plan dit « tolérance définie dans `ForecastHours` ». `ForecastHours` porte l'incrément (0,25) et `RoundToQuarter` ; la tolérance de comparaison est **`ForecastFillPlanner.Tolerance = 0.01m`**. ⇒ C+ réutilise `ForecastFillPlanner.Tolerance`, sans nouvelle constante.
4. Tokens absents de `app.css` (présents dans `docs\nav-prototypes\shared\base.css`) : `--accent-bg`, `--surface`, `--shadow`, `--radius` ⇒ **NOUVEAU** (WP3, valeurs par WP2).

---

## 1. Signatures C#

### 1.1 Phase 1 — synthèse de navigation (WP4, lecture seule)

Fichier **NOUVEAU** : `src\TimeSheetHelper.App\Services\NavSummaryService.cs`.

```csharp
using TimeSheetHelper.Engine.Proposals;

namespace TimeSheetHelper.App.Services;

/// NOUVEAU — badge d'un jour dans le panneau contextuel / le bandeau de jours.
public enum DayBadge
{
    Empty,      // « Non saisi »  : TotalHours == 0 && UnclassifiedHours == 0
    Draft,      // « Brouillon »  : UnclassifiedHours > 0 (heures à classer)
    Gap,        // « Écart »      : au moins un SubmissionGap de ce jour avec Severity Blocking ou ToVerify
    Ready,      // « Prêt »       : heures classées > 0, aucun écart (l'objectif quotidien n'intervient PAS — D4)
    Sent,       // « Envoyé »     : Phase 1 = WeekProposal.IsSubmitted ; Phase 2 = DaySubmission.Status == Sent/SentUnknownFingerprint
    Modified,   // « Modifié depuis l'envoi » : Phase 2 uniquement (DayStatus.Modified)
}

/// NOUVEAU — étape du stepper Saisir → Vérifier → Soumettre.
public enum NavStep { Enter, Review, Submit }

/// NOUVEAU
public sealed record DaySummary(
    DateOnly Date,
    decimal ClassifiedHours,     // DayProposal.TotalHours
    decimal TargetHours,         // DayProposal.TargetHours (== EngineOptions.TargetHoursPerDay en semaine, 0 le week-end) — référence, jamais bloquant (D4)
    decimal UnclassifiedHours,   // DayProposal.UnclassifiedHours
    int GapCount,                // SubmissionGap dont Date == Date (Severity != Info)
    DayFlag Flag,                // DayProposal.Flag (existant)
    DayBadge Badge,
    bool IsWeekend);

/// NOUVEAU
public sealed record NavSummary(
    DateOnly WeekStart,          // lundi
    IReadOnlyList<DaySummary> Days,   // toujours 7 entrées, lundi → dimanche
    decimal ClassifiedHours,     // somme des jours
    decimal WeeklyTargetHours,   // somme des TargetHours (40 h par défaut) — c'est CET objectif qui compte (D4)
    int BlockingGapCount,
    NavStep SuggestedStep,       // Enter si UnclassifiedHours > 0 ; Review si écarts ; Submit sinon
    bool IsSubmitted);           // WeekProposal.IsSubmitted

/// NOUVEAU
public interface INavSummaryService
{
    /// Lecture seule : aucune écriture, aucun appel Forecast. Cache court par semaine (clé = weekStart).
    Task<NavSummary> GetAsync(DateOnly weekStart, CancellationToken cancellationToken = default);

    /// Oublie le cache d'une semaine (appelé après une édition dans Jour/Semaine/Soumettre).
    void Invalidate(DateOnly weekStart);
}

/// NOUVEAU — implémentation (singleton) : réutilise WeekService.GetWeekAsync + SubmissionReviewService (pas de recalcul parallèle).
public sealed class NavSummaryService(
    WeekService weeks,
    SubmissionReviewService review,
    TimeProvider timeProvider) : INavSummaryService { /* WP4 */ }
```

Règles :
- `GetAsync` sans brouillon pour la semaine ⇒ `Days` à 0 h, badges `Empty`, `GapCount = 0`.
- Le calcul des écarts appelle `SubmissionReviewService.AnalyzeAsync(draft, report: null, ct)` (aucune lecture Forecast).
- Stub pour WP3 tant que WP4 n'est pas fusionné : **NOUVEAU** `src\TimeSheetHelper.App\Components\Layout\NavSummaryStub.cs` (`internal sealed class NavSummaryStub : INavSummaryService`, renvoie 7 jours `Empty`). Supprimé par WP4 au moment de l'enregistrement DI réel.

### 1.2 Phase 1 — coquille (WP3)

Fichiers **NOUVEAU** : `src\TimeSheetHelper.App\Components\Layout\ShortcutMap.cs`, `ShellState.cs`.

```csharp
namespace TimeSheetHelper.App.Components.Layout;

/// NOUVEAU
public enum ShortcutKind { None, Navigate, PreviousWeek, NextWeek, TogglePanel, ToggleTheme }

/// NOUVEAU — Route : chemin relatif sans « / » initial ("" = Semaine), utilisable tel quel par NavigationManager.NavigateTo.
public readonly record struct ShortcutAction(ShortcutKind Kind, string? Route = null);

/// NOUVEAU — description d'une touche telle que reçue de shell.js (KeyboardEvent).
public readonly record struct KeyChord(
    string Key,          // KeyboardEvent.key (ex. "1", "[", "b", "t")
    bool Alt,
    bool Ctrl,
    bool Meta,
    bool Shift,
    bool InEditableField);   // cible INPUT / TEXTAREA / SELECT / contentEditable

/// NOUVEAU — pur, sans dépendance, testé par WP5 (ShortcutMapTests).
public static class ShortcutMap
{
    /// Les 7 destinations dans l'ordre Alt+1..7 (voir §2).
    public static IReadOnlyList<NavDestination> Destinations { get; }

    public static ShortcutAction Resolve(KeyChord chord);
}

/// NOUVEAU
public sealed record NavDestination(
    int Shortcut,        // 1..7
    string Route,        // "" | "jour" | "soumettre" | "regles" | "taches" | "sources" | "activite"
    string Label,        // libellé FR (§2)
    string Icon,         // nom d'icône du prototype (§2)
    NavSection Section);

/// NOUVEAU — regroupement du rail (prototype : 4 sections).
public enum NavSection { Time, Sending, Configuration, Advanced }

/// NOUVEAU
public enum ShellTheme { Light, Dark }

/// NOUVEAU — état de la coquille, Scoped (un par circuit Blazor). Persistance localStorage via shell.js.
public sealed class ShellState
{
    public DateOnly WeekStart { get; }                 // lundi de la semaine affichée (en-tête + panneau)
    public bool PanelOpen { get; }                     // clé localStorage "tsh.panel"  ("1" / "0")
    public ShellTheme Theme { get; }                   // clé localStorage "tsh.theme"  ("light" / "dark")

    /// Emplacement d'en-tête : actions de page rendues par WeekHeader à droite du titre (WP6).
    public RenderFragment? HeaderActions { get; }
    /// Titre de page affiché par WeekHeader (le <h1> unique de la page ; FocusOnNavigate Selector="h1" reste valable).
    public string? PageTitle { get; }

    public event Action? Changed;

    public void SetWeek(DateOnly anyDayOfWeek);        // normalise au lundi
    public void ShiftWeek(int weeks);                  // « [ » = -1, « ] » = +1
    public void SetPanel(bool open);
    public void SetTheme(ShellTheme theme);
    public void SetHeader(string? title, RenderFragment? actions);   // appelé par une page (OnParametersSet) ; null/null au Dispose
}
```

Composant **NOUVEAU** d'emplacement (évite que chaque page manipule `ShellState` à la main) :

```razor
@* src\TimeSheetHelper.App\Components\Layout\PageHeader.razor — NOUVEAU *@
@* Usage (WP6) : <PageHeader Title="Règles"><Actions>…boutons existants…</Actions></PageHeader> *@
[Parameter, EditorRequired] public string Title { get; set; }
[Parameter] public RenderFragment? Actions { get; set; }
// OnParametersSet → ShellState.SetHeader(Title, Actions) ; Dispose → ShellState.SetHeader(null, null)
```

Interop **NOUVEAU** `src\TimeSheetHelper.App\wwwroot\shell.js` (module ES, sans dépendance) :

| Export JS                                    | Rôle                                                                                         |
| -------------------------------------------- | -------------------------------------------------------------------------------------------- |
| `init(dotNetRef)`                            | branche `keydown` sur `document`, appelle `dotNetRef.invokeMethodAsync("OnShortcut", chord)` |
| `getPrefs()` → `{ theme, panel }`            | lit `tsh.theme`, `tsh.panel`                                                                 |
| `setTheme(theme)` / `setPanel(open)`         | écrit `document.documentElement.dataset.theme` + localStorage                                |
| `dispose()`                                  | retire l'écouteur                                                                            |

Anti-flash : `App.razor` (WP3, **ajout seulement**) contient un petit script inline qui pose `data-theme` sur `<html>` depuis `tsh.theme` avant le rendu. `[JSInvokable] OnShortcut(KeyChord)` est **NOUVEAU** dans `MainLayout.razor` (ou un composant `ShellHost` — au choix de WP3, à noter dans son rapport).

### 1.3 Phase 2 — domaine par jour (WP8, stockage)

Fichiers **NOUVEAU** : `src\TimeSheetHelper.Core\Timesheets\DaySubmission.cs`, `SubmissionJournalEntry.cs`.

```csharp
namespace TimeSheetHelper.Core.Timesheets;

/// NOUVEAU — statut persisté d'un jour.
public enum DayStatus
{
    NotAttempted = 0,          // « Non tenté » — jamais envoyé (défaut)
    InProgress = 1,            // « Envoi en cours » — verrou persisté (expiration : DayLock.StaleAfter)
    Sent = 2,                  // « Envoyé » — empreinte connue
    SentUnknownFingerprint = 3,// « Envoyé (empreinte inconnue) » — rétro-rempli depuis TimesheetDraft.SubmittedUtc (G11)
    Modified = 4,              // « Modifié depuis l'envoi » — calculé (empreinte ≠), persisté au prochain plan
    Partial = 5,               // « Envoi partiel » — interrompu ou au moins une cellule non écrite
    Conflict = 6,              // « Conflit » — Forecast contient des heures non écrites par l'app
    Locked = 7,                // « Verrouillé » — approuvé/facturé dans Forecast
    Error = 8,                 // « Erreur » — échec non récupérable, rien de garanti
}

/// NOUVEAU — clé unique (WeekStart, Date).
public sealed class DaySubmission
{
    public int Id { get; set; }
    public DateOnly WeekStart { get; set; }            // lundi ; == TimesheetDraft.WeekStart
    public DateOnly Date { get; set; }
    public DayStatus Status { get; set; }
    public string? Fingerprint { get; set; }           // DayFingerprint.Compute(...) ; null = inconnu (G11)
    public DateTime? SentUtc { get; set; }             // UtcDateTimeConverter
    public DateTime? InProgressSinceUtc { get; set; }  // non null ⇔ Status == InProgress
    public List<DaySubmissionCell> Cells { get; set; } = [];
}

/// NOUVEAU — ce que l'app a effectivement écrit pour une tâche ce jour-là (table enfant, pas JSON).
public sealed class DaySubmissionCell
{
    public int Id { get; set; }
    public int DaySubmissionId { get; set; }
    public int ForecastTaskId { get; set; }
    public decimal SentHours { get; set; }             // multiple de 0,25
    public string? ForecastEntryId { get; set; }       // ForecastRemoteEntry.EntryId quand connu (nécessaire à update/delete)
}

/// NOUVEAU
public enum JournalKind { Planned, Sent, Updated, Deleted, Skipped, Resolved, Interrupted, Resumed, Cancelled, Failed, Migrated }

/// NOUVEAU
public enum JournalOutcome { Ok, Partial, Failed, Unknown }

/// NOUVEAU — G12 : Message = texte FR fixe + nombres/dates/ids de tâche. Jamais de jeton, secret, corps de courriel, contenu d'activité, réponse brute MCP.
public sealed class SubmissionJournalEntry
{
    public int Id { get; set; }
    public int DaySubmissionId { get; set; }
    public DateTime TimestampUtc { get; set; }
    public JournalKind Kind { get; set; }
    public JournalOutcome Outcome { get; set; }
    public int? ForecastTaskId { get; set; }
    public decimal? Hours { get; set; }
    public string Message { get; set; } = "";          // ≤ 500 caractères
}
```

`TimeSheetDbContext` (WP8, **ajouts NOUVEAU**) : `DbSet<DaySubmission> DaySubmissions`, `DbSet<DaySubmissionCell> DaySubmissionCells`, `DbSet<SubmissionJournalEntry> SubmissionJournal` ; index unique `(WeekStart, Date)` sur `DaySubmission` ; index unique `(DaySubmissionId, ForecastTaskId)` sur `DaySubmissionCell` ; cascade `DaySubmission → Cells, Journal` ; `UtcDateTimeConverter` sur `SentUtc`, `InProgressSinceUtc`, `TimestampUtc`. Migration **NOUVEAU** `DaySubmissions` (additive) + rétro-remplissage : pour chaque `TimesheetDraft` avec `SubmittedUtc != null`, pour chaque `Date` distincte de ses `Entries` ⇒ une ligne `Status = SentUnknownFingerprint`, `Fingerprint = null`, `SentUtc = SubmittedUtc`, sans `Cells`.

### 1.4 Phase 2 — capacités Forecast (WP9)

```csharp
namespace TimeSheetHelper.Forecast;

public enum ForecastToolRole
{
    ListTasks,
    ListTimeEntries,
    CreateTimeEntry,
    CurrentUser,
    UpdateTimeEntry,   // NOUVEAU — ajouté EN FIN d'enum (valeurs existantes inchangées)
    DeleteTimeEntry,   // NOUVEAU
}

public sealed class ForecastToolNames
{
    // existants : ListTasks, ListTimeEntries, CreateTimeEntry, CurrentUser
    public string? UpdateTimeEntry { get; set; }   // NOUVEAU — clé de config "Forecast:Tools:UpdateTimeEntry"
    public string? DeleteTimeEntry { get; set; }   // NOUVEAU — "Forecast:Tools:DeleteTimeEntry"
}

// ForecastMcpSession — NOUVEAU
public bool Has(ForecastToolRole role);            // ForecastToolResolver.Resolve(...) != null, sans exception

/// NOUVEAU
public enum ForecastCapability { Available, Unavailable }

/// NOUVEAU — exposé à l'UI ; jamais supposé, toujours détecté à l'ouverture de session.
public sealed record ForecastCapabilities(
    ForecastCapability Create,
    ForecastCapability Update,
    ForecastCapability Delete,
    string? UnavailableReason)   // FR, ex. « Le serveur Forecast ne propose pas la modification d'une saisie. »
{
    public static ForecastCapabilities Unknown { get; }   // tout Unavailable (avant la 1re connexion)
}
```

`ForecastTimesheetWriter` (WP9, **ajouts NOUVEAU** ; `FillWeekAsync` inchangé en comportement) :

```csharp
public Task<ForecastCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default);

/// Relit Forecast pour (Date, TaskId) avant d'écrire ; écrit seulement si l'état relu == expectedForecastHours (± ForecastFillPlanner.Tolerance).
public Task<ForecastCellResult> UpdateCellAsync(
    DateOnly date, int forecastTaskId, string forecastEntryId, decimal expectedForecastHours, decimal newHours,
    CancellationToken cancellationToken = default);

public Task<ForecastCellResult> DeleteCellAsync(
    DateOnly date, int forecastTaskId, string forecastEntryId, decimal expectedForecastHours,
    CancellationToken cancellationToken = default);

/// Lecture seule de l'état Forecast d'un jour (réutilise ReadTimeEntriesAsync ; IsComplete == false ⇒ aucune écriture en aval, G4).
public Task<ForecastDayRead> ReadDayAsync(DateOnly date, CancellationToken cancellationToken = default);
```

```csharp
/// NOUVEAU
public sealed record ForecastDayRead(DateOnly Date, IReadOnlyList<ForecastRemoteEntry> Entries, bool IsComplete, string? Detail);
```

`ForecastCellStatus` : **ajouts NOUVEAU en fin d'enum** `Updated`, `Deleted`, `Skipped` (valeurs existantes inchangées). Outil absent ⇒ `UpdateCellAsync`/`DeleteCellAsync` renvoient `Status = Error`, `Issue = WriteRefused`, message FR, **sans aucun appel d'écriture** ; jamais de « créer + supprimer » (D3).

### 1.5 Phase 2 — moteur par jour (WP10)

Fichiers **NOUVEAU** : `src\TimeSheetHelper.Forecast\Submission\DayPlanner.cs`, `DayFingerprint.cs`, `ConflictResolution.cs` ; `src\TimeSheetHelper.App\Services\DaySubmissionService.cs`, `DayLock.cs`.

```csharp
namespace TimeSheetHelper.Forecast.Submission;

/// NOUVEAU — état d'une cellule (tâche × jour). Correspond 1:1 aux états du prototype (submit.js cellState).
public enum CellState
{
    New,           // D && !F            : à écrire
    Unchanged,     // D == F             : déjà dans Forecast, ignoré (pas de doublon)
    Modified,      // F == S && D != F   : l'app l'a écrit, le brouillon a changé depuis
    Deleted,       // !D && S            : présent à l'envoi, retiré du brouillon
    Conflict,      // F != S (ou F présent sans S) && D != F : Forecast modifié ailleurs
    Ignored,       // tâche exclue (ExcludeFromSubmission / WeekTaskChoice.Ignore / ignoredTaskIds)
    Locked,        // ForecastRemoteEntry.ApprovalStatus approuvé ou Invoiced == true
    NotAttempted,  // envoi interrompu avant cette cellule (ou lecture incomplète, G4)
}
// D = heures du brouillon, F = heures relues dans Forecast, S = DaySubmissionCell.SentHours.
// Égalité = |a − b| ≤ ForecastFillPlanner.Tolerance. Ordre d'évaluation : Ignored > Locked > NotAttempted > Deleted > Unchanged > New > Modified > Conflict.

/// NOUVEAU — opération prévue pour une cellule.
public enum CellOperation { None, Create, Update, Delete, Complement, AcceptForecast }

/// NOUVEAU
public enum ConflictResolution
{
    Keep,            // « Garder Forecast tel quel » — aucune écriture, la cellule reste signalée
    AcceptForecast,  // « Accepter Forecast » — le brouillon prend F (aucune écriture Forecast)
    Complement,      // « Compléter » — crée seulement le delta D − F (> 0), risqué : confirmation
    Remove,          // « Retirer » — supprime l'entrée Forecast (Delete requis), risqué : confirmation
}

/// NOUVEAU
public sealed record CellPlan(
    int ForecastTaskId,
    string TaskDisplayName,
    decimal DraftHours,          // D
    decimal? ForecastHours,      // F (null = rien dans Forecast)
    decimal? SentHours,          // S (null = jamais envoyé)
    string? ForecastEntryId,
    CellState State,
    CellOperation Operation,     // opération par défaut ; Conflict ⇒ None tant qu'aucune résolution
    IReadOnlyList<ConflictResolution> AllowedResolutions,   // vide sauf Conflict
    string? BlockedReason);      // FR, ex. capacité Update indisponible

/// NOUVEAU — gate de jour (libellés FR §3.2).
public enum DayGate { NothingToSend, NothingNew, ReadForecastFirst, ResolveConflicts, Send, SendChanges, Resume, InProgress, Locked }

/// NOUVEAU
public sealed record DayPlan(
    DateOnly WeekStart,
    DateOnly Date,
    DayStatus Status,                    // TimeSheetHelper.Core.Timesheets.DayStatus
    string? CurrentFingerprint,
    string? SentFingerprint,
    IReadOnlyList<CellPlan> Cells,
    DayGate Gate,
    bool ForecastReadComplete,
    ForecastCapabilities Capabilities)
{
    public bool HasWork => Cells.Any(c => c.Operation != CellOperation.None);
    public bool IsReady => Gate is DayGate.Send or DayGate.SendChanges or DayGate.Resume;
}

/// NOUVEAU — pur (aucune EF, aucun réseau).
public static class DayPlanner
{
    public static DayPlan Plan(
        DateOnly weekStart, DateOnly date,
        IReadOnlyList<TimesheetEntry> draftEntries,       // du jour
        ForecastDayRead forecast,
        DaySubmission? previous,
        IReadOnlySet<int> ignoredTaskIds,
        IReadOnlyDictionary<int, ForecastTask> tasks,
        ForecastCapabilities capabilities);
}

/// NOUVEAU — SHA-256, hex minuscule. Forme canonique : lignes "yyyy-MM-dd|<taskId>|<heures F2 InvariantCulture>\n"
/// triées par taskId, heures nulles exclues, encodage UTF-8 sans BOM. Indépendant de la culture et du fuseau (G10).
public static class DayFingerprint
{
    public static string Compute(DateOnly date, IEnumerable<(int ForecastTaskId, decimal Hours)> cells);
}
```

```csharp
namespace TimeSheetHelper.App.Services;

/// NOUVEAU — verrou par jour : SemaphoreSlim par (WeekStart, Date) + DaySubmission.InProgressSinceUtc persisté.
public sealed class DayLock
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);   // InProgress plus vieux ⇒ considéré interrompu (crash)
    /// null si déjà tenu (même processus) ou InProgress non expiré (autre onglet/processus) — jamais d'attente bloquante.
    public Task<IAsyncDisposable?> TryAcquireAsync(DateOnly weekStart, DateOnly date, CancellationToken cancellationToken = default);
}

/// NOUVEAU
public sealed record DaySendResult(
    DateOnly Date,
    DayStatus Status,
    int Written,                 // créations + mises à jour + suppressions effectivement faites
    int Skipped,
    IReadOnlyList<ForecastCellResult> Cells,
    string Message);             // FR

/// NOUVEAU
public sealed record SendReadyResult(IReadOnlyList<DaySendResult> Sent, IReadOnlyList<(DateOnly Date, string Reason)> SkippedDays);

/// NOUVEAU
public interface IDaySubmissionService
{
    Task<ForecastCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default);

    /// Lit brouillon + Forecast (relecture complète) + DaySubmission ; n'écrit rien.
    Task<DayPlan> PlanAsync(DateOnly date, CancellationToken cancellationToken = default);

    /// Prend le verrou, re-planifie (relecture Forecast AVANT écriture), applique les opérations + résolutions, journalise.
    /// Gate NothingNew/NothingToSend ⇒ 0 écriture. Verrou non obtenu ⇒ Status InProgress, 0 écriture.
    Task<DaySendResult> SendAsync(
        DateOnly date, IReadOnlyDictionary<int, ConflictResolution> resolutions, CancellationToken cancellationToken = default);

    /// N'envoie que les jours dont DayPlan.IsReady ; les autres sont rapportés dans SkippedDays (G9).
    Task<SendReadyResult> SendReadyAsync(DateOnly weekStart, CancellationToken cancellationToken = default);

    /// Annule l'envoi en cours de ce jour (CancellationToken interne) ; le jour passe Partial, cellules restantes NotAttempted.
    Task CancelAsync(DateOnly date);

    Task<IReadOnlyList<SubmissionJournalEntry>> GetJournalAsync(DateOnly date, CancellationToken cancellationToken = default);
}

/// NOUVEAU — singleton.
public sealed class DaySubmissionService(
    IDbContextFactory<TimeSheetDbContext> dbFactory,
    ForecastTimesheetWriter writer,
    DayLock dayLock,
    TimeProvider timeProvider,
    ILogger<DaySubmissionService> logger) : IDaySubmissionService { /* WP10 */ }
```

Machine à états (WP10) :

```
NotAttempted ──Send──► InProgress ──► Sent | Partial | Error
SentUnknownFingerprint ──Plan──► (comparaison Forecast obligatoire) ──► Sent (si D == F partout) | Modified | Conflict
Sent ──Plan, empreinte ≠──► Modified ──Send──► InProgress ──► Sent | Partial | Error
Partial ──Send (Resume)──► InProgress ──► …   (relecture d'abord : cellules déjà présentes ⇒ Unchanged, jamais recréées — G3)
* ──Plan, F ≠ S hors app──► Conflict ;  * ──Plan, entrée approuvée/facturée──► Locked (terminal pour ce jour)
InProgress (expiré > DayLock.StaleAfter) ──Plan──► Partial
```

---

## 2. Routes ↔ raccourcis Alt+1..7

Routes lues dans les `@page` réels de `src\TimeSheetHelper.App\Components\Pages\*.razor`. Ordre = clés de `docs\nav-prototypes\shared\pages.js`.

| Alt | Route réelle (`@page`)            | Page               | `Route` pour `NavigateTo` | Libellé rail (FR) | Icône (prototype) | Section rail (prototype)   | `NavLink.Match` |
| --- | --------------------------------- | ------------------ | ------------------------- | ----------------- | ----------------- | -------------------------- | --------------- |
| 1   | `/`                               | `Week.razor`       | `""`                      | Semaine           | `semaine`         | `Time` — « Ma semaine »    | `All`           |
| 2   | `/jour`, `/jour/{Date}`           | `Day.razor`        | `"jour"`                  | Jour              | `jour`            | `Time` — « Ma semaine »    | `Prefix`        |
| 3   | `/soumettre`                      | `Submit.razor`     | `"soumettre"`             | Soumettre         | `soumettre`       | `Sending` — « Soumettre »  | `Prefix`        |
| 4   | `/regles`                         | `Rules.razor`      | `"regles"`                | Règles            | `regles`          | `Configuration`            | `Prefix`        |
| 5   | `/taches`                         | `ForecastTasks.razor` | `"taches"`             | Tâches            | `taches`          | `Configuration`            | `Prefix`        |
| 6   | `/sources`                        | `Sources.razor`    | `"sources"`               | Sources           | `sources`         | `Configuration`            | `Prefix`        |
| 7   | `/activite`                       | `ActivityLog.razor`| `"activite"`              | Activité brute    | `activite`        | `Advanced` — « Avancé »    | `Prefix`        |

Hors rail : `/not-found` (`NotFound.razor`), `/Error` (`Error.razor`).

Autres raccourcis (`ShortcutMap.Resolve`) :

| Touche                       | Action                     | Conditions                                                                                   |
| ---------------------------- | -------------------------- | -------------------------------------------------------------------------------------------- |
| Alt+1 … Alt+7                | `Navigate(Route)`          | ignorés si Ctrl ou Meta enfoncé ; **actifs** même dans un champ (comme le prototype)         |
| Alt+T                        | `ToggleTheme`              | idem                                                                                         |
| `[` / `]`                    | `PreviousWeek` / `NextWeek`| ignorés si `InEditableField`, ou si Ctrl/Meta/Alt enfoncé                                    |
| Ctrl+B / Meta+B              | `TogglePanel`              | ignoré si `InEditableField` (ne vole pas le gras d'un éditeur)                               |
| Ctrl+K                       | — (`None`)                 | hors périmètre (D5)                                                                          |

Stepper (`StepIndicator`, `<ol aria-label="Parcours de la semaine">`, étape courante `aria-current="step"`) : **Saisir** → `/jour` · **Vérifier** → `/` · **Soumettre** → `/soumettre`.

---

## 3. Matrices d'états → libellé FR / icône / token / actions

Libellés repris de `docs\nav-prototypes\c-rail\submit.js`. Icônes = noms du jeu d'icônes du prototype (`check`, `warn`, `soumettre`, `jour`, `plus`, `refresh`…). Tokens = `wwwroot\app.css` (couleur de texte/bordure + fond `*-bg`). **Jamais la couleur seule** : icône + libellé toujours rendus. Implémentation pure **NOUVEAU** `src\TimeSheetHelper.App\Components\Shared\DayStateLabels.cs` (WP11), testée par WP12.

### 3.1 États de cellule (`CellState`)

| État (§1.2 #12) | `CellState`    | Libellé FR (badge)                                  | Icône      | Token              | Opération par défaut                 | Actions permises (UI)                                                                                         |
| --------------- | -------------- | --------------------------------------------------- | ---------- | ------------------ | ------------------------------------ | ------------------------------------------------------------------------------------------------------------- |
| nouveau         | `New`          | « Nouveau — sera écrit »                            | `plus`     | `--ok` / `--ok-bg` | `Create`                             | Envoyer ; « Ne pas envoyer » (ignorer la tâche)                                                               |
| inchangé        | `Unchanged`    | « Déjà dans Forecast — ignoré (pas de doublon) »    | `check`    | `--info` / `--info-bg` | `None`                           | Aucune (lecture seule)                                                                                        |
| modifié         | `Modified`     | « Modifié depuis l'envoi »                          | `refresh`  | `--warn` / `--warn-bg` | `Update` (si capacité `Update`)  | Envoyer les changements (confirmation) ; si `Update` indisponible : bouton désactivé + raison + lien « Modifier dans Forecast » (G5, D3) |
| supprimé        | `Deleted`      | « Supprimé localement »                             | `warn`     | `--danger` / `--danger-bg` | `Delete` (si capacité `Delete`) | Retirer de Forecast (confirmation obligatoire) ; Garder dans Forecast ; si `Delete` indisponible : désactivé + lien Forecast |
| conflit         | `Conflict`     | « Conflit — Forecast modifié ailleurs »             | `warn`     | `--danger` / `--danger-bg` | `None` jusqu'au choix        | Garder (`Keep`) · Accepter Forecast (`AcceptForecast`) · Compléter +Δ (`Complement`, si D > F, confirmation) · Retirer (`Remove`, si `Delete` dispo, confirmation) — G6 |
| ignoré          | `Ignored`      | « Ignoré (Ne pas envoyer) »                         | — (texte barré + libellé) | `--muted` / `--idle` | `None`             | Réinclure la tâche (existant : `WeekTaskChoice`)                                                              |
| verrouillé      | `Locked`       | « Verrouillé — non écrit »                          | `warn`     | `--locked`         | `None`                               | Aucune (G7) ; lien « Voir dans Forecast »                                                                    |
| non tenté       | `NotAttempted` | « Non tenté (envoi interrompu) »                    | `refresh`  | `--warn` / `--warn-bg` | `Create`/`Update` après relecture | Rejouer (relecture d'abord — G3)                                                                              |

### 3.2 Statut de jour (`DayStatus`) et bouton d'envoi (`DayGate`)

| `DayStatus`              | Libellé FR (bandeau)              | Icône      | Token        |
| ------------------------ | --------------------------------- | ---------- | ------------ |
| `NotAttempted`           | « Prêt » (si heures) / « Non saisi » (sinon) — dérivé de `DayBadge` | `jour` | `--accent` / `--idle` |
| `InProgress`             | « Envoi en cours… »               | `refresh`  | `--info`     |
| `Sent`                   | « Envoyé »                        | `check`    | `--ok`       |
| `SentUnknownFingerprint` | « Envoyé (empreinte inconnue) »   | `check`    | `--ok` (+ texte d'aide « sera comparé à Forecast avant tout nouvel envoi ») |
| `Modified`               | « Modifié depuis l'envoi »        | `refresh`  | `--warn`     |
| `Partial`                | « Envoi partiel »                 | `warn`     | `--warn`     |
| `Conflict`               | « Écart à résoudre »              | `warn`     | `--danger`   |
| `Locked`                 | « Verrouillé »                    | `warn`     | `--locked`   |
| `Error`                  | « Erreur d'envoi »                | `warn`     | `--danger`   |

| `DayGate`           | Libellé du bouton (prototype)                    | Activé ? |
| ------------------- | ------------------------------------------------ | -------- |
| `InProgress`        | « Envoi en cours… n/N » (+ `aria-live="polite"`) | non      |
| `NothingToSend`     | « Rien à envoyer »                               | non      |
| `ResolveConflicts`  | « Écart à résoudre »                             | non      |
| `ReadForecastFirst` | « Relire Forecast d'abord »                      | oui (déclenche `PlanAsync`, n'écrit rien) |
| `NothingNew`        | « Rien de nouveau à envoyer »                    | non      |
| `Send`              | « Soumettre ce jour »                            | oui      |
| `SendChanges`       | « Envoyer les changements »                      | oui (confirmation si Update/Delete) |
| `Resume`            | « Rejouer (écrit seulement ce qui manque) »      | oui      |
| `Locked`            | « Verrouillé »                                   | non      |

**D4** : l'écart à l'objectif quotidien (« Écart d'heures » du prototype) est un **avertissement d'information** affiché dans le panneau du jour ; il **ne produit jamais** `DayGate.ResolveConflicts` ni ne désactive l'envoi. Seul l'objectif hebdomadaire (`NavSummary.WeeklyTargetHours`, 40 h par défaut) est mis en évidence au niveau semaine, toujours sans bloquer.

---

## 4. Scénarios du prototype → tests (WP12)

Fake : **NOUVEAU** `tests\TimeSheetHelper.Tests\App\FakeForecastWriteApi.cs` (compteur d'appels d'écriture, lecture tronquée, timeout à la n-ième écriture, entrée préexistante, jour verrouillé, outils absents, barrière de concurrence).

| Scénario `submit.js` | Données de départ (fake)                                                    | Garantie | Test(s) NOUVEAU                                                                                       | Assertion clé                                                                                           | Fichier                              |
| -------------------- | --------------------------------------------------------------------------- | -------- | ----------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------- | ------------------------------------ |
| `neuf`               | brouillon D, Forecast vide, aucun `DaySubmission`                           | G1       | `G1_SecondSendWritesNothing`                                                                          | 1er envoi : N créations ; 2ᵉ envoi : compteur d'écritures inchangé, statut `Sent`                       | `DaySubmissionIdempotenceTests.cs`   |
| `inchange`           | `DaySubmission` `Sent`, empreinte == brouillon, Forecast == D               | G1       | `G1_UnchangedDay_ShowsNothingToSend`                                                                  | `PlanAsync` ⇒ `Gate == NothingNew`, toutes cellules `Unchanged` ; `SendAsync` ⇒ 0 écriture             | `DaySubmissionIdempotenceTests.cs`   |
| `modifie`            | `Sent` avec S, brouillon changé (D ≠ F == S), `Update` disponible           | G5       | `G5_ModifiedDay_UpdatesExistingEntry`                                                                 | 1 appel `Update` sur `ForecastEntryId`, 0 `Create`                                                      | `DaySubmissionIdempotenceTests.cs`   |
| `modifie`            | idem, outil `Update` absent                                                 | G5, D3   | `G5_UpdateToolMissing_BlocksAndNeverCreates`                                                          | cellule `Modified` avec `BlockedReason` ; 0 écriture de quelque type que ce soit                        | `DaySubmissionIdempotenceTests.cs`   |
| `conflit`            | Forecast F ≠ S (modifié hors app)                                           | G6       | `G6_Conflict_Keep_WritesNothing`, `G6_Conflict_AcceptForecast_UpdatesDraftOnly`, `G6_Conflict_Complement_CreatesOnlyDelta`, `G6_Conflict_Remove_DeletesAfterConfirmation`, `G6_Conflict_WithoutChoice_NeverOverwrites` | aucune écriture sans résolution ; `Complement` crée exactement D − F ; `Remove` = 1 `Delete` | `ConflictResolutionTests.cs`         |
| `partielle`          | timeout à la 2ᵉ écriture sur 3, puis reprise                                | G3       | `G3_InterruptedSend_ResumeDoesNotDuplicate`                                                           | après reprise : total créations == 3 (pas 4) ; statut `Partial` puis `Sent`                             | `DaySubmissionIdempotenceTests.cs`   |
| `double`             | deux `SendAsync` concurrents sur le même jour (barrière, pas de `Sleep`)    | G2       | `G2_ConcurrentSends_SingleWrite`                                                                      | une seule série d'écritures ; l'autre appel renvoie `InProgress` avec 0 écriture                        | `DayLockTests.cs`                    |

Autres garanties (non liées à un scénario) : `G4_IncompleteRead_WritesNothing`, `G7_LockedDay_NeverWritten`, `G8_IgnoredTask_NeverWritten`, `G9_SendReady_SkipsConflictAndLockedDays`, `G9_SendReady_RerunIsIdempotent`, `G10_Fingerprint_StableAcrossOrderAndCulture`, `G10_Fingerprint_ChangesWithHoursTaskOrDay`, `G11_MigratedSubmittedWeek_NeverProposedAsNew` (fichiers selon §4 du plan : `DayPlannerTests.cs`, `DayFingerprintTests.cs`, `SendReadyDaysTests.cs`, `Storage\DaySubmissionMigrationTests.cs`). G12 : revue Mafia Boy (WP13).

---

## 5. Lignes DI réservées

Fichier : `src\TimeSheetHelper.App\AppServices.cs`, méthode `AddTimeSheetHelper`, **immédiatement après** `services.AddSingleton<SubmissionReviewService>();`, dans cet ordre (une ligne par lot, ajout seulement ; ordre de fusion WP4 → WP10) :

```csharp
        services.AddSingleton<INavSummaryService, NavSummaryService>();        // WP4 — NOUVEAU
        services.AddSingleton<DayLock>();                                       // WP10 — NOUVEAU
        services.AddSingleton<IDaySubmissionService, DaySubmissionService>();   // WP10 — NOUVEAU
```

`ShellState` est **Scoped** (un par circuit) et enregistré par WP3 dans le même bloc : `services.AddScoped<ShellState>();` (WP3 — NOUVEAU, première ligne du bloc). Tant que WP4 n'est pas fusionné, WP3 enregistre `services.AddSingleton<INavSummaryService, NavSummaryStub>();` à l'emplacement de la ligne WP4, que WP4 **remplace**. `ForecastTimesheetWriter` reste enregistré par `AddForecast` (`src\TimeSheetHelper.Forecast\ServiceCollectionExtensions.cs`) ; WP9 n'ajoute aucune ligne DI. `Program.cs` n'est pas modifié.

---

## 6. Garde-fous de fichiers (rappel §3 du plan, noms réels)

| Fichier                                                         | Seul éditeur              | Règle                                                                     |
| --------------------------------------------------------------- | ------------------------- | ------------------------------------------------------------------------- |
| `Components\Layout\MainLayout.razor(.css)`, `Layout\*` NOUVEAU   | Cendrillon (WP3)          | WP6 : lecture seule                                                       |
| `wwwroot\app.css`                                               | Cendrillon                | WP3 tokens ; WP6/WP11 uniquement dans `/* === nav-c: <lot> === */`        |
| `Components\App.razor`                                          | Cendrillon (WP3)          | ajout seulement (fichier déjà modifié dans l'arbre de travail)            |
| `Pages\Submit.razor(.css)`                                      | Cendrillon                | WP6 puis WP11, jamais en parallèle                                         |
| `AppServices.cs` (bloc §5)                                      | Yoda (réservation)        | une ligne par lot, ordre WP3 → WP4 → WP10                                 |
| `Storage\TimeSheetDbContext.cs`, `Migrations\*`                 | Glados (WP8)              | migration unique `DaySubmissions`                                          |
| `Forecast\ForecastTimesheetWriter.cs`, `ForecastToolResolver.cs`, `ForecastOptions.cs` (`ForecastToolNames`), `ForecastMcpSession.cs` (`Has`) | Glados (WP9) | WP10 consomme, ne modifie pas |
| Tests de câblage `SubmitPage*Tests.cs`                          | Gadget                    | Cendrillon : assertions rendues obsolètes uniquement                      |
