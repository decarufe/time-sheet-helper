# ADR — Navigation « C » (rail) + soumission « C+ » jour par jour

> Auteur : Yoda (architecte) — lot **WP1** — date : 2026-10-19 — statut : **ACCEPTÉ** (plan approuvé par Éric, défauts D1–D6 et §8.6 appliqués).
> Plan : `docs\plans\nav-c-implementation-plan.md`. Contrats normatifs : `docs\plans\nav-c-contracts.md`.

## Contexte

La navigation actuelle (`src\TimeSheetHelper.App\Components\Layout\MainLayout.razor`) est une simple barre de `NavLink`. Le prototype « C » (`docs\nav-prototypes\c-rail\`) propose un rail d'icônes, un panneau contextuel, un en-tête de semaine, un stepper, un thème clair/sombre et des raccourcis ; le prototype « C+ » (`submit.js`) propose l'envoi à Forecast jour par jour avec états de cellule et garanties anti-doublon. Aujourd'hui, l'état « soumis » n'existe qu'au niveau de la semaine (`TimesheetDraft.SubmittedUtc`) et l'écriture Forecast ne sait que **créer** (`ForecastToolRole` : `ListTasks`, `ListTimeEntries`, `CreateTimeEntry`, `CurrentUser`).

## Décisions

### D1 — Branche et commits

- Branche **`squad/nav-c`**, créée depuis la branche courante (déjà active).
- **Un commit par vague** (W1…W6, + `P1-gate`), fait **par le coordinateur uniquement**, fichiers ajoutés **par chemin explicite** (jamais `git add -A`) ; message `feat(nav-c): W<n> — <résumé>` + trailer `Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>`.
- Aucun push automatique ; vérifier les hooks `pre-push` avant tout push ; `--no-verify` interdit.
- Les changements non liés déjà présents dans l'arbre de travail ne sont ni annulés, ni reformatés, ni inclus dans les commits de vague.

### D2 — Persistance par jour

- Nouvelles entités `DaySubmission` (clé unique `(WeekStart, Date)`), `DaySubmissionCell` (heures réellement envoyées par tâche + `ForecastEntryId`) et `SubmissionJournalEntry` — **NOUVEAU**, signatures au §1.3 des contrats.
- Une seule migration, **additive**, `DaySubmissions` (WP8) ; `TimesheetDraft`/`TimesheetEntry` inchangés ; `SubmittedUtc` conservé.
- Rétro-remplissage : jours d'une semaine déjà soumise ⇒ `DayStatus.SentUnknownFingerprint` (« Envoyé (empreinte inconnue) »), `Fingerprint = null` ⇒ jamais re-proposés comme « nouveaux » (G11) ; le premier envoi suivant compare d'abord avec Forecast.
- Cellules en **table enfant** (pas JSON) : nécessaire pour l'unicité `(DaySubmissionId, ForecastTaskId)` et pour retrouver l'`EntryId` Forecast à mettre à jour/supprimer.

### D3 — Mise à jour / suppression dans Forecast

- Nouveaux rôles `ForecastToolRole.UpdateTimeEntry` / `DeleteTimeEntry` (ajoutés en fin d'enum), surcharges `ForecastToolNames.UpdateTimeEntry` / `DeleteTimeEntry`, détection `ForecastMcpSession.Has(role)` — **NOUVEAU**, WP9.
- Capacités **détectées, jamais supposées** : `ForecastCapabilities` exposé à l'UI. Outil absent ⇒ `Unavailable`, action désactivée avec raison en français + lien « Modifier dans Forecast ».
- **Jamais** de contournement « créer + supprimer », jamais de création de remplacement pour une cellule modifiée (G5).
- Toute écriture est précédée d'une **relecture** de Forecast ; lecture incomplète ⇒ aucune écriture (G4).

### D4 — Objectif quotidien

- Objectif quotidien = `EngineOptions.TargetHoursPerDay` (clé `Engine:TargetHoursPerDay`, 8 h par défaut), **pas** les 7,5 h du prototype.
- Décision d'Éric : l'objectif quotidien est une **référence, jamais bloquante**. L'objectif qui compte est **hebdomadaire (40 h)**. Conséquences :
  - le badge « Prêt » d'un jour ne dépend pas de l'atteinte de l'objectif quotidien ;
  - la garde « Écart d'heures » du prototype devient un avertissement d'information ; elle ne désactive jamais l'envoi et ne produit jamais `DayGate.ResolveConflicts` ;
  - le panneau/en-tête met en évidence le total hebdomadaire vs `NavSummary.WeeklyTargetHours`, sans bloquer non plus.

### D5 — Palette Ctrl+K

Hors périmètre. `ShortcutMap.Resolve` renvoie `None` pour Ctrl+K. WP17 reste réservé.

### D6 — Éléments de démonstration du prototype

- Barre mobile (≤ 860 px) portée telle que dessinée.
- **Exclus** : cartes de scénario, légende de démonstration, sélecteur de scénarios.
- Les garde-fous « pédagogiques » deviennent un **texte d'aide** sur la page Soumettre (WP11).
- Les scénarios `neuf/inchange/modifie/conflit/partielle/double` servent uniquement de jeux de données de tests (WP12) et de captures sur faux Forecast (WP15).

### Décisions d'architecture complémentaires (WP1)

| #   | Décision                                                                                                                                                                                                                                                                  | Raison                                                                              |
| --- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------- |
| A1  | Les lignes DI réservées sont dans **`src\TimeSheetHelper.App\AppServices.cs`** (`AddTimeSheetHelper`, après `AddSingleton<SubmissionReviewService>()`), pas dans `Program.cs`.                                                                                             | C'est là que l'app enregistre ses services (§8.6 : nom réel prime).                 |
| A2  | `NavSummaryService` est **lecture seule**, singleton, cache court par semaine, réutilise `WeekService` + `SubmissionReviewService` (analyse sans rapport Forecast).                                                                                                       | Phase 1 sans backend d'écriture ni appel réseau.                                    |
| A3  | `ShellState` est **Scoped** (un par circuit) ; thème/panneau persistés en localStorage `tsh.theme` / `tsh.panel` via `wwwroot\shell.js` ; `data-theme` posé avant rendu (anti-flash).                                                                                    | État d'interface par utilisateur/onglet, pas de donnée serveur.                     |
| A4  | Emplacement d'en-tête : composant **`PageHeader`** (titre + `RenderFragment Actions`) qui alimente `ShellState.SetHeader` ; `WeekHeader` rend le `<h1>` unique (le `FocusOnNavigate Selector="h1"` de `Routes.razor` reste valide).                                       | Les pages gardent leurs handlers ; migration WP6 « à l'identique ».                 |
| A5  | `ShortcutMap` pur (aucune dépendance Blazor/JS) ; `shell.js` ne fait que transmettre un `KeyChord`.                                                                                                                                                                       | Testable unitairement (WP5).                                                        |
| A6  | Logique d'état de cellule dans **`DayPlanner` pur** (`TimeSheetHelper.Forecast.Submission`) ; orchestration dans `DaySubmissionService` (App) ; seul point d'écriture : `ForecastTimesheetWriter`.                                                                       | Testabilité sans EF ni réseau ; un seul chemin d'écriture à auditer.                |
| A7  | Comparaison d'heures : `ForecastFillPlanner.Tolerance` (0,01) existant ; pas de nouvelle constante.                                                                                                                                                                      | Cohérence avec `FillWeekAsync`.                                                     |
| A8  | Empreinte `DayFingerprint` : SHA-256 hex sur forme canonique triée, heures `F2` invariantes, UTF-8 sans BOM.                                                                                                                                                              | G10 (stabilité culture/fuseau/ordre).                                               |
| A9  | `DayLock` : `SemaphoreSlim` par `(WeekStart, Date)` + `DaySubmission.InProgressSinceUtc` persisté, expiration `StaleAfter = 10 min` ; acquisition non bloquante (`TryAcquireAsync`).                                                                                       | G2 (double clic, deux onglets) et reprise après crash (G3).                         |
| A10 | Le journal (`SubmissionJournalEntry.Message`) ne contient que du texte FR fixe + nombres/dates/ids ; jamais de réponse MCP brute.                                                                                                                                         | G12.                                                                                |
| A11 | Divergence prototype vs app : l'app prime pour la **logique**, le prototype pour le **visuel** ; tokens uniquement dans `wwwroot\app.css` (ajouts `--accent-bg`, `--surface`, `--shadow`, `--radius` par WP3).                                                         | §8.6 ; principe §2.7.                                                               |
| A12 | L'envoi pour approbation du manager (`SubmitForApprovalAsync`) reste une action **explicite et séparée**, jamais déclenchée par l'envoi d'un jour.                                                                                                                         | Inchangé (plan §1.2 #16).                                                           |

## Ordre de fusion des fichiers partagés

| Fichier                                                                  | Propriétaire                      | Ordre                                                                            |
| ------------------------------------------------------------------------ | --------------------------------- | -------------------------------------------------------------------------------- |
| `Components\Layout\MainLayout.razor(.css)` + `Layout\*` NOUVEAU          | Cendrillon (WP3)                  | WP3 → fin                                                                        |
| `wwwroot\app.css`                                                        | Cendrillon                        | WP3 → WP6 → WP11 (blocs `/* === nav-c: <lot> === */`, séquentiel)               |
| `Components\App.razor`                                                   | Cendrillon (WP3)                  | ajout seulement                                                                  |
| `Pages\Submit.razor(.css)`                                               | Cendrillon                        | WP6 → WP11, jamais en parallèle                                                  |
| `Storage\TimeSheetDbContext.cs` + `Migrations\*`                         | Glados (WP8)                      | WP8 unique                                                                       |
| `src\TimeSheetHelper.App\AppServices.cs` (bloc DI réservé)               | Yoda (réservation WP1)            | WP3 (`ShellState` + stub) → WP4 (remplace le stub) → WP10                        |
| `Forecast\ForecastTimesheetWriter.cs`, `ForecastToolResolver.cs`, `ForecastOptions.cs`, `ForecastMcpSession.cs` | Glados (WP9) | WP9 → WP10 (consomme)                                                 |
| Tests `SubmitPage*Tests.cs`                                              | Gadget                            | suit `Submit.razor`                                                              |
| `.squad\decisions\inbox\*`                                               | chaque agent, son propre fichier  | Scribe fusionne                                                                  |

Vagues : W0 (WP1 ∥ WP2) → W1 (WP3 ∥ WP4 ∥ WP8) → W2 (WP5 ∥ WP6 ∥ WP9) → W3 (WP7 ∥ WP10, porte P1) → W4 (WP11 ∥ WP12) → W5 (WP13 ∥ WP14 ∥ WP15) → W6 (WP16).

## Conséquences

- Phase 1 livrable et réversible seule (revenir sur `MainLayout*` + `Layout\*`).
- Phase 2 dépend du spike WP9 : si Forecast MCP n'expose ni mise à jour ni suppression, les états `Modified`/`Deleted`/`Remove` restent visibles mais leurs actions sont désactivées avec explication — le produit reste sûr (aucun doublon), seulement moins automatisé.
- Ancien flux de soumission conservé derrière la nouvelle section tant que la parité n'est pas validée (WP11).

## Alternatives rejetées

- **Statut par jour stocké en JSON dans `TimesheetDraft`** : pas d'unicité ni d'index, migration moins lisible.
- **Créer + supprimer pour simuler une mise à jour** : fenêtre de doublon et perte possible ⇒ interdit (D3).
- **Bloquer l'envoi sous l'objectif quotidien** (prototype) : contraire à D4.
- **Enregistrer les services dans `Program.cs`** : contraire à la structure existante (`AppServices`).
