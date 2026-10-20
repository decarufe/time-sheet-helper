# Plan d'implémentation — Navigation « C » (rail) + soumission « C+ » jour par jour

> Auteur : Yoda (architecte) — destinataire : Éric — statut : **PROPOSÉ, en attente d'approbation (étape 0)**
> Prototypes de référence : `docs\nav-prototypes\c-rail\index.html`, `docs\nav-prototypes\c-rail\submit.js`, `docs\nav-prototypes\c-rail\submit.css`, `docs\nav-prototypes\shared\base.css`, `docs\nav-prototypes\shared\pages.js`.
> Ce document ne modifie **aucun** code. Il est conçu pour être exécuté **entièrement par des sous-agents** une fois approuvé.

---

## 0. Résumé exécutif (≤ 1 page)

**Objectif.** Remplacer la navigation actuelle (`MainLayout.razor`) par le prototype **C** : rail d'icônes, panneau contextuel (jours de la semaine, objectif, badges), en-tête de semaine, stepper _Saisir → Vérifier → Soumettre_, thème clair/sombre (Alt+T), responsive ≤ 860 px, raccourcis (Alt+1..7, `[` `]`, Ctrl+B). Puis porter la soumission **C+** (route `#/soumettre` du prototype) : envoi **jour par jour**, avec états par cellule et garanties anti-doublon.

**Deux phases, un seul plan.**

|          | Phase 1 — Coquille de navigation                                                                | Phase 2 — Soumission C+ « À construire »                                                                                                            |
| -------- | ----------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------- |
| Nature   | Surtout front (Razor/CSS/JS) + 1 service de synthèse en lecture seule                           | Domaine + stockage (migration EF) + écriture Forecast + UI                                                                                          |
| Risque   | Faible (aucune donnée nouvelle, aucun envoi)                                                    | **Élevé** (écriture dans Forecast : un doublon d'heures est un incident réel)                                                                       |
| Livrable | Application navigable avec la nouvelle coquille ; pages existantes inchangées fonctionnellement | Envoi par jour, détection « modifié depuis l'envoi », mise à jour/suppression, conflits, journal, annulation/reprise, « Soumettre les jours prêts » |

**Déroulement.** Étape 0 : _vous approuvez ce plan_ (et répondez, si vous le souhaitez, aux 6 décisions de §9 — des défauts sont proposés). Ensuite tout est autonome : 7 vagues (W0…W6), ~17 lots de travail (WP) répartis entre Yoda, Morpheus, Martha, Cendrillon, Glados, Inspecteur Gadget, Mafia Boy, Jimmy. Chaque lot passe une **porte de revue** (Gadget + `code-review`) ; chaque vague se termine par `dotnet build` + `dotnet test tests\TimeSheetHelper.Tests`. Phase 1 est vérifiée visuellement avec Playwright (large/étroit, clair/sombre) avant de commencer l'UI de la Phase 2. Branche proposée `squad/nav-c`, **un commit par vague** (jamais sans votre feu vert implicite via l'approbation du plan — voir §8.1).

**Ce qui existe déjà** (réutilisé, pas reconstruit) : lecture de la semaine (`WeekService`, `WeekProposalBuilder`), brouillon `TimesheetDraft`/`TimesheetEntry` (avec `SubmittedUtc` au niveau semaine), écriture Forecast _créer seulement_ (`ForecastTimesheetWriter.FillWeekAsync` avec filtre `days`), évaluation des cellules (`ForecastCellEvaluator`), écarts (`SubmissionGap`, `SubmissionGapService`, `SubmissionReviewService`), UI Soumettre « lot 1 » (`SubmissionStatusBar`, `DraftGrid`, `GapList/GapCard/GapBoard`, `GapActionPlanner`, `FillGuard`, `SubmitDialogs`, `SessionGapState`), sélection de jours (`DaySelection`).

**Ce qu'il faut construire** : persistance par jour (statut, empreinte, horodatage, heures envoyées) + migration ; détection « modifié depuis l'envoi » ; _mise à jour_ d'une saisie existante au lieu d'ajouter ; _suppression_ dans Forecast ; résolution de conflit (garder / accepter / compléter / retirer) ; journal d'envoi par jour ; annuler / reprendre ; « Soumettre les jours prêts » ; bandeau de jours ; états de cellule ; idempotence ; verrou pendant l'envoi ; « Rien de nouveau à envoyer ».

**Décisions pour vous (défauts proposés, détail §9)** : D1 branche/commits ; D2 table par jour (migration additive) ; D3 mise à jour/suppression Forecast conditionnées aux outils MCP disponibles ; D4 objectif quotidien = configuration de l'app (8 h, pas 7,5 h du prototype) ; D5 palette Ctrl+K hors périmètre ; D6 cartes de démonstration du prototype exclues.

---

## 1. Périmètre — EXISTE vs À CONSTRUIRE

### 1.1 Phase 1 — Coquille (utilise l'existant)

| Élément du prototype C                  | Source existante réutilisée                                                         | À créer                                                                    |
| --------------------------------------- | ----------------------------------------------------------------------------------- | -------------------------------------------------------------------------- |
| Rail d'icônes + 7 destinations          | Routes des pages `Pages\*.razor`                                                    | `Layout\Rail.razor`                                                        |
| Panneau contextuel (jours, objectif)    | `WeekService`, `WeekProposalBuilder`, `SubmissionReviewService`, `DatePickerButton` | `Layout\ContextPanel.razor`, service **lecture seule** `NavSummaryService` |
| En-tête de semaine + navigation `[` `]` | `WeekService`, `DatePickerButton`                                                   | `Layout\WeekHeader.razor`                                                  |
| Stepper Saisir → Vérifier → Soumettre   | `SubmissionStatus` / `SubmissionStatusBuilder`                                      | `Layout\StepIndicator.razor`                                               |
| Thème clair/sombre (Alt+T)              | tokens CSS dans `wwwroot\app.css`                                                   | tokens `data-theme`, `wwwroot\shell.js` (interop)                          |
| Responsive ≤ 860 px                     | —                                                                                   | `Layout\MobileNav.razor` (barre basse) + CSS                               |
| Raccourcis Alt+1..7, `[` `]`, Ctrl+B    | —                                                                                   | `wwwroot\shell.js` + helper pur `ShortcutMap` (testable)                   |
| Ctrl+K (palette)                        | —                                                                                   | **optionnel / hors périmètre par défaut (D5)**                             |

### 1.2 Phase 2 — C+ « À construire »

| #   | Capacité (prototype `submit.js`)                                                                       | Existe ?                                                                                                                | Lot                      |
| --- | ------------------------------------------------------------------------------------------------------ | ----------------------------------------------------------------------------------------------------------------------- | ------------------------ |
| 1   | Statut par jour (non tenté, envoyé, modifié, conflit, partiel…) persisté                               | ❌ (marqueur `SubmittedUtc` **par semaine** seulement)                                                                  | WP8                      |
| 2   | Empreinte (fingerprint) par jour + horodatage + heures envoyées par cellule                            | ❌                                                                                                                      | WP8, WP10                |
| 3   | Migration EF additive + rétro-remplissage depuis `SubmittedUtc`                                        | ❌                                                                                                                      | WP8                      |
| 4   | Détection « modifié depuis l'envoi »                                                                   | ❌                                                                                                                      | WP10                     |
| 5   | _Mettre à jour_ la saisie existante (au lieu d'ajouter)                                                | ❌ (écrit en création uniquement ; `ForecastToolRole` = ListTasks/ListTimeEntries/CreateTimeEntry/CurrentUser)          | WP9                      |
| 6   | Suppression dans Forecast                                                                              | ❌ (dépend des outils MCP — D3)                                                                                         | WP9                      |
| 7   | Résolution de conflit : garder / accepter / compléter / retirer                                        | ❌ (A2 de l'historique : `AddComplement`, `AcceptForecastHours`, `ReplaceTask`, `IgnoreKnowingly` listés indisponibles) | WP10, WP11               |
| 8   | Journal d'envoi par jour (sans secrets)                                                                | ❌                                                                                                                      | WP8, WP10, WP11          |
| 9   | Annuler / rejouer (undo/replay/cancel) avec `CancellationToken`                                        | ◑ (le token existe dans `FillWeekAsync` ; pas de reprise tracée)                                                        | WP9, WP10                |
| 10  | « Soumettre les jours prêts »                                                                          | ❌                                                                                                                      | WP10, WP11               |
| 11  | Bandeau de jours + panneau du jour                                                                     | ❌ (`DaySelection` + `DraftGrid` existent)                                                                              | WP11                     |
| 12  | États de cellule : nouveau / inchangé / modifié / supprimé / conflit / ignoré / verrouillé / non tenté | ◑ (`ForecastCellStatus` : Planned/Ok/Mismatch/RowNotFound/Error/Locked/Unknown ; `ForecastCellEvaluator`)               | WP10 (modèle), WP11 (UI) |
| 13  | Idempotence (rejouer = aucun effet)                                                                    | ◑ (cellule non vide → signalée, pas écrite)                                                                             | WP9, WP10, WP12          |
| 14  | Verrou pendant l'envoi (double clic, double onglet)                                                    | ❌                                                                                                                      | WP10, WP11               |
| 15  | État « Rien de nouveau à envoyer »                                                                     | ❌                                                                                                                      | WP10, WP11               |
| 16  | Envoi pour approbation du manager                                                                      | ✅ **jamais fait automatiquement** (`SubmitForApprovalAsync` existe, reste une action explicite séparée)                | —                        |

### 1.3 Garanties anti-doublon (chaque ligne = au moins un test nommé, WP12)

- **G1** Envoyer deux fois le même jour sans changement ⇒ 0 appel d'écriture au 2ᵉ envoi (« Rien de nouveau à envoyer »).
- **G2** Double clic / deux onglets / deux exécutions concurrentes sur le même jour ⇒ une seule écriture (verrou par jour, test de concurrence).
- **G3** Envoi interrompu (timeout, annulation, crash simulé) puis reprise ⇒ relecture de Forecast **avant** d'écrire ; les cellules déjà présentes ne sont pas recréées.
- **G4** Lecture Forecast incomplète (`IsComplete == false`) ⇒ aucune écriture, statut « non tenté » (comportement actuel conservé et testé).
- **G5** Jour modifié après envoi ⇒ **mise à jour** de la saisie existante (jamais une 2ᵉ saisie sur la même tâche+jour) ; si l'outil de mise à jour est absent ⇒ action bloquée avec lien « modifier dans Forecast », jamais de création de remplacement.
- **G6** Conflit (Forecast contient des heures que l'app n'a pas écrites) ⇒ jamais écrasé sans choix explicite ; « compléter » n'ajoute que le **delta** ; chaque choix testé.
- **G7** Jour verrouillé (approuvé/facturé) ⇒ jamais écrit ni modifié.
- **G8** Tâche ignorée/exclue (`ExcludeFromSubmission`, `IgnoredTaskIds`) ⇒ jamais écrite.
- **G9** « Soumettre les jours prêts » ⇒ n'envoie que les jours _prêts_ ; un jour en conflit/verrouillé/ignoré est sauté et rapporté ; ré-exécution ⇒ idempotente.
- **G10** Empreinte stable : même contenu ⇒ même empreinte (ordre indifférent, aucune dépendance à la culture/fuseau) ; tout changement d'heures/tâche/jour ⇒ empreinte différente.
- **G11** Migration : base existante avec semaine « soumise » ⇒ jours en « Envoyé (empreinte inconnue) », **jamais** re-proposés comme « nouveaux » à l'envoi silencieux.
- **G12** Le journal ne contient ni secrets, ni jetons, ni corps de courriel/contenu d'activité (Mafia Boy, WP13).

---

## 2. Principes d'architecture (arrêtés dans WP1, résumés ici)

1. **Phase 1 sans backend d'écriture.** `NavSummaryService` est en lecture seule ; aucune migration en Phase 1.
2. **Un domaine par jour, pas par semaine.** Nouvelle entité `DaySubmission` (clé : `WeekStart`+`Date`) + `SubmissionJournalEntry`. Les `TimesheetEntry` existants ne changent pas.
3. **Un seul point d'écriture Forecast** : `ForecastTimesheetWriter` (étendu en WP9). L'UI n'appelle jamais `ForecastApi` directement.
4. **Machine à états dans un service pur** (`DaySubmissionService` + `DayPlanner` pur testable sans EF ni réseau) ; l'UI ne fait que projeter.
5. **Fake Forecast pour les tests** : réutiliser/étendre `FakeForecastLookup` et créer `FakeForecastWriteApi` (WP12) capable de simuler : lecture tronquée, timeout, saisie préexistante, jour verrouillé, outils update/delete absents.
6. **Capacités détectées, pas supposées** : `ForecastToolRole` étendu (`UpdateTimeEntry`, `DeleteTimeEntry`) avec `session.Has(role)` ; absence ⇒ capacité « indisponible » exposée à l'UI.
7. **UI : composants petits, CSS scopé**, tokens dans `wwwroot\app.css` uniquement (jamais de couleurs en dur dans les `.razor.css`).
8. **Texte UI en français**, accessibilité (rôles ARIA, focus visible, `prefers-reduced-motion`, contrastes AA clair/sombre).

---

## 3. Fichiers partagés (propriétaire + ordre de fusion)

| Fichier                                                                                                              | Propriétaire                                                      | Autres intervenants (interdits d'éditer, sauf mention)                                                     | Ordre de fusion                                                                                                       |
| -------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------- |
| `src\TimeSheetHelper.App\Components\Layout\MainLayout.razor` / `.razor.css`                                          | **Cendrillon** (WP3)                                              | WP6 : lecture seule ; Martha : spec seulement                                                              | WP3 → fin                                                                                                             |
| `src\TimeSheetHelper.App\wwwroot\app.css`                                                                            | **Cendrillon** (WP3 : tokens + utilitaires)                       | WP6/WP11 ajoutent **uniquement** dans un bloc délimité `/* === nav-c: <lot> === */` ; jamais de réécriture | WP3 → WP6 → WP11 (séquentiel, jamais en parallèle)                                                                    |
| `src\TimeSheetHelper.App\Components\Pages\Submit.razor` (+ `.razor.css`)                                             | **Cendrillon** — WP6 (migration d'en-tête, W2) puis WP11 (C+, W4) | Glados : interdit ; Gadget : adapte les tests de câblage seulement                                         | WP6 → WP11, **jamais en parallèle**                                                                                   |
| `src\TimeSheetHelper.Storage\TimeSheetDbContext.cs` + `Migrations\*`                                                 | **Glados** (WP8 seul)                                             | Tout autre changement de schéma ⇒ nouveau WP, pas de migration concurrente                                 | WP8 unique ; toute migration ultérieure est fusionnée _avant_ la suivante (jamais deux `migrations add` en parallèle) |
| `src\TimeSheetHelper.App\Program.cs` (enregistrements DI)                                                            | **Yoda** (WP1 réserve les lignes)                                 | WP4 et WP10 ajoutent _une ligne chacune_                                                                   | WP4 → WP10 (additif, conflits triviaux)                                                                               |
| `src\TimeSheetHelper.Forecast\ForecastTimesheetWriter.cs`, `ForecastToolResolver.cs`                                 | **Glados** (WP9)                                                  | WP10 consomme l'API, ne la modifie pas                                                                     | WP9 → WP10                                                                                                            |
| `tests\TimeSheetHelper.Tests\App\SubmitPageWiringTests.cs`, `SubmitPageGadgetTests.cs`, `SubmitPageRevisionTests.cs` | **Gadget**                                                        | Cendrillon corrige _seulement_ les assertions rendues obsolètes par sa propre migration, relues par Gadget | suit Submit.razor                                                                                                     |
| `.squad\decisions\inbox\*`                                                                                           | chaque agent écrit **son propre** fichier                         | Scribe fusionne                                                                                            | —                                                                                                                     |

Règle de bordure : un agent qui a besoin d'un changement dans un fichier d'un autre propriétaire **ne l'édite pas** : il l'écrit dans son rapport (« demande de changement ») et le coordinateur planifie/dispatche.

---

## 4. Lots de travail (WP)

Légende effort : S ≤ ½ j, M ≈ 1 j, L ≈ 2 j+ (agent). « Modèle » = modèle Anthropic explicite à passer au spawn (règle de routage 9).

### Étape 0 — Approbation

**WP0 — Éric approuve ce plan** (et, optionnellement, tranche D1–D6). Rien n'est lancé avant. _Effort : —._

### Vague W0 — Cadrage (parallèle : WP1 ∥ WP2)

**WP1 — ADR + contrats d'architecture** · Owner **Yoda** · modèle `claude-opus-5.5` · Effort M · Parallèle avec WP2

- **But** : figer les contrats consommés par les lots suivants.
- **Créer** : `docs\plans\nav-c-adr.md` (décisions D1–D6 appliquées, branche, ordre de fusion), `docs\plans\nav-c-contracts.md` : signatures C# (`NavSummary`/`DaySummary`, `INavSummaryService`, `ShortcutMap`, `ShellState`, `DaySubmission`, `DayStatus`, `CellState`, `DayPlan`, `IDaySubmissionService`, `ForecastCapabilities`), table de routes (Alt+1..7 ↔ routes réelles lues dans `Pages\*.razor`), matrice états→libellés FR, garde-fous de fichiers.
- **Modifier** : rien (aucun code).
- **Entrées prototype** : `c-rail\index.html`, `submit.js` (états, scénarios `neuf/inchange/modifie/conflit/partielle/double`), `submit.css`.
- **Acceptation** : chaque contrat compile mentalement avec l'existant (noms vérifiés par grep) ; chaque état du §1.2 #12 a un libellé FR, une couleur-token et une règle d'action ; scénarios du prototype mappés 1:1 sur des tests (§7).
- **Tests** : aucun (revue par code-review sur cohérence).
- **Dépendances** : WP0.

**WP2 — Spécification design (tokens + a11y)** · Owner **Martha Stewart** · modèle `claude-sonnet-5.5` · Effort S · Parallèle avec WP1

- **Créer** : `docs\plans\nav-c-design-spec.md` : tokens (`--accent-bg`, `--surface`, `--shadow`, `--radius`, jeux clair/sombre `data-theme`), grille rail/panneau/en-tête, points de rupture (860 px), états de cellule (couleur + icône + libellé — jamais la couleur seule), focus, contrastes AA, mouvements réduits, correspondance prototype → classes.
- **Entrées** : `shared\base.css`, `c-rail\index.html`, `submit.css`, `app.css` actuel.
- **Acceptation** : chaque token a valeur clair+sombre et ratio de contraste calculé ; chaque composant du §1.1 a sa spec ; captures de référence du prototype listées (pages × clair/sombre × large/étroit) pour WP7/WP15.
- **Dépendances** : WP0.

### Vague W1 — Fondations (parallèle : WP3 ∥ WP4 ∥ WP8 — fichiers disjoints)

**WP3 — Coquille de navigation** · Owner **Cendrillon** · `claude-opus-5.5` · Effort L

- **Créer** : `Components\Layout\Rail.razor(.css)`, `ContextPanel.razor(.css)`, `WeekHeader.razor(.css)`, `StepIndicator.razor(.css)`, `MobileNav.razor(.css)`, `Components\Layout\ShellState.cs` (état panneau/thème, persisté via interop), `Components\Layout\ShortcutMap.cs` (pur : touche → action), `wwwroot\shell.js` (thème Alt+T, raccourcis, persistance localStorage `tsh.theme` / `tsh.panel`, sans dépendance).
- **Modifier** : `Layout\MainLayout.razor`, `MainLayout.razor.css`, `wwwroot\app.css` (tokens + `[data-theme]` uniquement), `Components\App.razor` (référence `shell.js`, attribut `data-theme` initial anti-flash — fichier déjà modifié dans l'arbre de travail : **ne pas annuler les changements existants**).
- **Entrées prototype** : `c-rail\index.html` (structure/JS), `shared\base.css`, spec WP2, contrats WP1.
- **Acceptation** : rail 7 destinations avec lien actif ; panneau repliable (Ctrl+B) mémorisé ; thème sauvegardé sans flash ; ≤ 860 px : rail → barre basse, panneau en tiroir ; Alt+1..7 navigue ; `[`/`]` changent de semaine (inactif quand le focus est dans un champ de saisie) ; navigation clavier + ARIA (`nav`, `aria-current`, `aria-label` FR) ; le panneau consomme `INavSummaryService` via un **stub** tant que WP4 n'est pas fusionné (contrat WP1).
- **Tests** : couverts par WP5 ; Cendrillon fournit `ShortcutMap` pur testable.
- **Dépendances** : WP1, WP2. **Parallèle** avec WP4/WP8 (aucun fichier commun). Propriétaire des fichiers partagés `MainLayout*` et `app.css`.

**WP4 — NavSummaryService (lecture seule)** · Owner **Glados** · `claude-sonnet-5.5` · Effort M

- **Créer** : `src\TimeSheetHelper.App\Services\NavSummaryService.cs` (+ modèle `NavSummary`, `DaySummary` : date, heures proposées/objectif, nb d'écarts, état d'étape, badge), selon le contrat WP1.
- **Modifier** : `Program.cs` (1 ligne DI).
- **Entrées** : `WeekService`, `WeekProposalBuilder`, `SubmissionReviewService`, `EngineOptions` (objectif quotidien — D4).
- **Acceptation** : aucune écriture ni appel réseau Forecast ; réutilise les services existants (pas de recalcul parallèle) ; coût borné (un appel par rendu de semaine, cache court par semaine) ; objectif = `Engine:TargetHoursPerDay` (vérifier le nom exact dans `EngineOptions`).
- **Tests** : unitaires dans `tests\...\App\NavSummaryServiceTests.cs` (semaine vide, jour partiel, jour complet, jour avec écarts, week-end).
- **Dépendances** : WP1. **Parallèle** avec WP3/WP8.

**WP8 — Domaine + stockage par jour + migration** · Owner **Glados** · `claude-opus-5.5` · Effort L

- **Créer** : `src\TimeSheetHelper.Core\Timesheets\DaySubmission.cs` (WeekStart, Date, Status, Fingerprint?, SentUtc?, `Cells` JSON/enfants : TaskId, SentHours, ForecastEntryId?), `SubmissionJournalEntry.cs` (DayId, TimestampUtc, Kind, Message sans secrets, Outcome), enums `DayStatus` ; migration `…_DaySubmissions.cs` (+ Designer) ; `tests\...\Storage\DaySubmissionMigrationTests.cs` (modèle : `RemovedSlotsMigrationTests`).
- **Modifier** : `Storage\TimeSheetDbContext.cs` (2 DbSet + configuration : index unique `(WeekStart, Date)`, conversions UTC via `UtcDateTimeConverter` existant), `Migrations\TimeSheetDbContextModelSnapshot.cs` (généré).
- **Génération** : `dotnet ef migrations add DaySubmissions` (via `DesignTimeDbContextFactory`), puis relecture ; **aucune modification manuelle du snapshot**.
- **Acceptation** : migration **additive** (aucun DROP/ALTER destructif) ; rétro-remplissage : chaque jour d'un brouillon avec `SubmittedUtc` non nul ⇒ `DaySubmission` `Envoyé` avec `Fingerprint = null` (G11) ; base vide et base peuplée migrent sans erreur ; `Down` fonctionne.
- **Tests** : migration sur base existante (G11), unicité, aller-retour de sérialisation.
- **Dépendances** : WP1. **Parallèle** avec WP3/WP4. **Seul propriétaire de `TimeSheetDbContext`**.

**Fin de W1 — Intégration W1** (coordinateur) : `dotnet build`, `dotnet test tests\TimeSheetHelper.Tests`, relecture des diffs, **commit `W1`** sur `squad/nav-c`.

### Vague W2 (parallèle : WP5 ∥ WP6 ∥ WP9)

**WP6 — Migration des pages vers la coquille** · Owner **Cendrillon** · `claude-opus-5.5` · Effort L

- **Modifier** : `Pages\Week.razor(.css)`, `Day.razor(.css)`, `Submit.razor(.css)` (**en-tête et barre d'outils seulement**, aucune logique C+), `Rules.razor(.css)`, `ForecastTasks.razor(.css)`, `Sources.razor(.css)`, `ActivityLog.razor`, `NotFound.razor`/`Error.razor` si nécessaire ; `Components\Routes.razor` (si le layout l'exige) ; `app.css` (bloc `nav-c: pages`).
- **Principe** : retirer les `h1`/barres d'outils redondants, insérer les actions de page dans un emplacement d'en-tête (`RenderFragment` fourni par `WeekHeader`/`ShellState`, contrat WP1), corriger le CSS scopé en conflit.
- **Acceptation** : toutes les pages fonctionnent à l'identique (mêmes actions, mêmes handlers) ; aucune régression des tests existants ; titre de page (`PageTitle`) conservé ; pas de scroll horizontal ≤ 860 px.
- **Tests** : tests existants verts ; ajustements minimaux des tests de câblage de Submit (validés par Gadget).
- **Dépendances** : WP3 fusionné (et WP4 pour le panneau réel). Propriétaire de `Submit.razor` **avant WP11**.

**WP5 — Tests Phase 1** · Owner **Inspecteur Gadget** · `claude-sonnet-5.5` · Effort M

- **Créer** : `tests\...\App\ShortcutMapTests.cs` (Alt+1..7, `[` `]`, Ctrl+B, Alt+T ; ignorés dans les champs de saisie), `ShellMarkupTests.cs` (tests de marque/source, style de `SubmitPageWiringTests` : rail liste les 7 routes, `aria-current`, `nav` labellisé, `shell.js` référencé, aucune couleur en dur dans les `.razor.css` de la coquille), complète `NavSummaryServiceTests` si lacunes.
- **Acceptation** : chaque test échouerait si la fonctionnalité était retirée (anti-tautologie, skill `test-discipline`).
- **Dépendances** : WP3 (+WP4). **Parallèle** avec WP6/WP9 (fichiers de tests nouveaux uniquement).

**WP9 — Écriture Forecast : mise à jour, suppression, idempotence** · Owner **Glados** · `claude-opus-5.5` · Effort L

- **Modifier** : `Forecast\ForecastToolResolver.cs` (rôles `UpdateTimeEntry`, `DeleteTimeEntry`, détection par nom/schéma + surcharges de `ForecastOptions` ; `ForecastMcpSession.Has(role)`), `Forecast\ForecastTimesheetWriter.cs` (nouvelles opérations `UpdateCellAsync`, `DeleteCellAsync`, `FillWeekAsync` conservé et non régressif ; relecture avant écriture ; réutiliser `ForecastCellEvaluator`), `ForecastCommands.cs`/`ForecastArgumentBuilder.cs` (arguments des nouveaux outils), `ForecastFillReport.cs` (statuts additifs : `Updated`, `Deleted`, `Skipped`).
- **Spike obligatoire (1ʳᵉ étape)** : lister les outils MCP réellement exposés (via le code de découverte existant / fixtures) ; consigner dans `.squad\decisions\inbox\glados-forecast-mcp-capabilities.md`. **Défaut D3** : si un outil update/delete n'existe pas ⇒ capacité `Unavailable`, jamais de contournement par « créer + supprimer ».
- **Acceptation** : `CancellationToken` respecté partout ; écriture précédée d'une relecture complète (G3/G4) ; une cellule = une opération atomique rapportée (`ForecastCellResult`) ; `FillWeekAsync` existant inchangé en comportement (tests actuels verts).
- **Tests** : unitaires avec `FakeForecastWriteApi` minimal (celui complet est livré en WP12) ; tests d'outil absent.
- **Dépendances** : WP1 (contrats), WP8 pour les types partagés (`DayStatus` seulement si utilisé — sinon indépendant). **Parallèle** avec WP5/WP6.

**Fin de W2 — Intégration W2** + **commit `W2`**.

### Vague W3 (parallèle : WP7 ∥ WP10)

**WP7 — Vérification visuelle Phase 1 (Playwright)** · Owner **Martha Stewart** (revue visuelle) avec **Inspecteur Gadget** (exécution) · `claude-sonnet-5.5` · Effort M

- **Procédure** : lancer l'application (`dotnet run --project src\TimeSheetHelper.App`, vérifier l'URL/port dans les logs), Playwright MCP : pour chaque page (Semaine, Jour, Soumettre, Règles, Tâches, Sources, Activité) × {large 1440×900, étroit 390×844} × {clair, sombre} : capture dans `docs\plans\screenshots\p1\`, vérifications : rail/panneau/en-tête visibles, thème commuté par Alt+T, Alt+1..7, `[` `]`, Ctrl+B, tiroir mobile, aucun débordement, console sans erreur.
- **Acceptation** : rapport `.squad\decisions\inbox\martha-p1-visual-report.md` avec écarts vs prototype classés (bloquant/mineur) ; bloquants renvoyés à Cendrillon via coordinateur (nouveau cycle, verrou §8.5 si rejet).
- **Dépendances** : WP6, WP4. **Porte Phase 1** : si zéro bloquant ⇒ commit `P1-gate`.

**WP10 — Moteur de soumission par jour** · Owner **Glados** · `claude-opus-5.5` · Effort L

- **Créer** : `src\TimeSheetHelper.App\Services\DaySubmissionService.cs` (`PlanAsync(day)`, `SendAsync(day, resolutions, ct)`, `SendReadyAsync(week, ct)`, `CancelAsync`), `src\TimeSheetHelper.Forecast\Submission\DayPlanner.cs` (pur : brouillon + lecture Forecast + `DaySubmission` ⇒ `DayPlan` avec états de cellule §1.2 #12), `DayFingerprint.cs` (SHA-256 sur forme canonique : tri par tâche, heures à 2 décimales invariant, UTF-8), `DayLock.cs` (verrou par jour, `SemaphoreSlim` par clé + état persisté « en cours » avec expiration pour crash), `ConflictResolution.cs` (Keep/AcceptForecast/Complement/Remove).
- **Modifier** : `Program.cs` (1 ligne DI), `Services\SubmissionReviewService.cs` **seulement** pour exposer des données (pas de changement de comportement).
- **Règles** : machine à états `NonTenté → EnCours → Envoyé | Partiel | Erreur` ; `Envoyé → Modifié` si empreinte ≠ ; `Conflit` si Forecast contient des heures non écrites par l'app ; `Verrouillé` si approuvé/facturé ; « Rien de nouveau à envoyer » quand tout inchangé ; chaque transition journalisée (sans secrets) ; mise à jour via WP9 ; capacités indisponibles ⇒ actions désactivées avec raison.
- **Acceptation** : toutes les garanties G1–G11 sont implémentables et testées par WP12 ; aucune écriture Forecast hors `ForecastTimesheetWriter` ; aucune dépendance Razor.
- **Dépendances** : WP8, WP9. **Parallèle** avec WP7 (aucun fichier commun).

**Fin de W3 — Intégration W3** + commit `W3`.

### Vague W4 (parallèle : WP11 ∥ WP12)

**WP11 — UI C+ (bandeau de jours, panneau du jour, résolutions, journal)** · Owner **Cendrillon** · `claude-opus-5.5` · Effort L

- **Créer** : `Shared\DayStrip.razor(.css)`, `DayPanel.razor(.css)`, `CellStateBadge.razor(.css)`, `ConflictChooser.razor(.css)`, `DayJournal.razor(.css)`, `SendReadyBar.razor(.css)`, `Shared\DayStateLabels.cs` (états → libellé FR/icône/token, pur).
- **Modifier** : `Pages\Submit.razor(.css)` (intégration derrière la route/section de soumission jour par jour ; l'ancien flux reste utilisable tant que la parité n'est pas validée), `wwwroot\app.css` (bloc `nav-c: submit`).
- **Entrées** : `submit.js` (scénarios `neuf/inchange/modifie/conflit/partielle/double` = jeux de données de démonstration), `submit.css`, `DayStateLabels` de WP1/WP2.
- **Acceptation** : chaque état de cellule visible avec icône+libellé (pas la couleur seule) ; bouton d'envoi désactivé pendant l'envoi (verrou G2) avec annonce `aria-live` ; confirmation avant toute suppression/mise à jour ; « Rien de nouveau à envoyer » affiché ; « Soumettre les jours prêts » liste d'abord les jours concernés ; actions indisponibles expliquées ; **aucune** cartes de démonstration du prototype (D6).
- **Tests** : tests de câblage source (Gadget WP12) ; `DayStateLabels` couvert.
- **Dépendances** : WP6 (Submit.razor), WP10 (contrats ; développer contre l'interface). **Parallèle** avec WP12.

**WP12 — Tests anti-doublon / idempotence** · Owner **Inspecteur Gadget** · `claude-opus-5.5` · Effort L

- **Créer** : `tests\...\App\FakeForecastWriteApi.cs` (réseau simulé : lecture tronquée, timeout à la n-ième écriture, saisie préexistante, jour verrouillé, outils absents, latence contrôlable pour concurrence), `DaySubmissionIdempotenceTests.cs`, `DayPlannerTests.cs`, `DayFingerprintTests.cs`, `ConflictResolutionTests.cs`, `SendReadyDaysTests.cs`, `DayLockTests.cs`, `DayStateLabelsTests.cs`, tests de câblage `DayStripWiringTests.cs`.
- **Acceptation** : **chaque garantie G1–G11 a ≥ 1 test nommé `G<n>_…`** ; compteur d'appels d'écriture du fake vérifié (0 au 2ᵉ envoi) ; test de concurrence déterministe (barrière), pas de `Thread.Sleep` ; tests échouent si on retire la garde correspondante (preuve par mutation manuelle documentée dans le rapport).
- **Dépendances** : WP10 (API), WP9. Peut démarrer sur les contrats WP1 et finaliser à la fusion de WP10. **Parallèle** avec WP11.

**Fin de W4 — Intégration W4** + commit `W4`.

### Vague W5 (parallèle : WP13 ∥ WP14 ∥ WP15)

**WP13 — Revue de sécurité** · Owner **Mafia Boy** (agent `security-review`) · `claude-opus-5.5` · Effort M

- **Portée** : nouvelle persistance (`DaySubmission`, journal), écriture Forecast, `shell.js` (XSS/localStorage), aucun nouvel endpoint HTTP (à confirmer), journal sans secrets/jetons/corps de courriel (G12), rétention du journal, journalisation (`ILogger`) sans données sensibles, injection via noms de tâches affichés, entrées de `ForecastArgumentBuilder`.
- **Sortie** : `.squad\decisions\inbox\mafiaboy-nav-c-security-review.md` ; tout _High/Medium_ ⇒ correctif par un **autre** agent que l'auteur (§8.5).
- **Dépendances** : WP11, WP12.

**WP14 — Documentation** · Owner **Jimmy Neutron** · `claude-sonnet-5.5` · Effort S/M

- **Modifier/créer** : `README.md` (navigation, raccourcis, thèmes), `docs\submission-by-day.md` (états, garanties G1–G12, reprise, limites : suppression/mise à jour selon capacités MCP), `.squad\decisions\inbox\jimmy-nav-c-docs.md`.
- **Acceptation** : chaque raccourci/état documenté correspond au code (Fact Checker peut vérifier) ; FR.
- **Dépendances** : WP11. **Parallèle**.

**WP15 — Vérification visuelle Phase 2 (Playwright)** · Owner **Martha Stewart + Gadget** · `claude-sonnet-5.5` · Effort M

- Même procédure que WP7 pour la page Soumettre, avec des données de démonstration seedées par un **fake local** (jamais le vrai Forecast, aucune écriture réelle) : captures des états neuf/inchangé/modifié/conflit/partielle/double × large/étroit × clair/sombre dans `docs\plans\screenshots\p2\`.
- **Interdit** : envoyer quoi que ce soit au vrai Forecast pendant la vérification.
- **Dépendances** : WP11.

### Vague W6 — Stabilisation et intégration

**WP16 — Stabilisation finale** · Owner **Yoda** (+ `code-review`, + Scribe pour les décisions) · `claude-opus-5.5` · Effort M

- `dotnet build TimeSheetHelper.sln` sans avertissement nouveau ; `dotnet test tests\TimeSheetHelper.Tests` 100 % vert ; relecture globale du diff `squad/nav-c` ; vérification des fichiers partagés (aucun bloc `nav-c` orphelin) ; corrections des _findings_ par un agent **différent** de l'auteur ; vérifier que **rien** n'est resté dans `docs\plans\` hors livrables prévus ; commit `W6` ; rapport final.
- **Acceptation** : tous les DoD (§8.3) cochés dans un tableau récapitulatif `docs\plans\nav-c-final-report.md`.

_(WP17 réservé : Ctrl+K palette — seulement si D5 = oui ; Cendrillon, `claude-sonnet-5.5`, Effort M ; dépend de WP6.)_

---

## 5. Graphe de dépendances et vagues

```
WP0 ─► WP1 ─┬─► WP3 ─┬─► WP6 ─┬─► WP7 ──► [Porte P1]
            │        │        │
            ├─► WP4 ─┘        ├────────────────► WP11 ─┬─► WP13
 WP2 ───────┘ (WP3)           │                       ├─► WP14 ─► WP16
            ├─► WP8 ─┬─► WP10 ┘ (contrats) ─────────────┴─► WP15 ─┘
            └─► WP9 ─┘   │                              ▲
                  WP3─► WP5   WP10 ─► WP12 ─────────────┘ (WP13 dépend aussi de WP12)
```

| Vague | Lots lancés en parallèle | Condition de sortie                                  |
| ----- | ------------------------ | ---------------------------------------------------- |
| W0    | WP1 ∥ WP2                | Contrats + spec design revus ; aucune modif de code  |
| W1    | WP3 ∥ WP4 ∥ WP8          | build+tests verts ; commit W1                        |
| W2    | WP5 ∥ WP6 ∥ WP9          | build+tests verts ; commit W2                        |
| W3    | WP7 ∥ WP10               | Porte P1 (0 bloquant visuel) ; commit W3             |
| W4    | WP11 ∥ WP12              | G1–G11 couverts et verts ; commit W4                 |
| W5    | WP13 ∥ WP14 ∥ WP15       | 0 finding High/Medium ouvert ; docs/captures livrées |
| W6    | WP16                     | DoD global ; commit W6                               |

Parallélisme sûr garanti par §3 : deux lots d'une même vague ne partagent **aucun** fichier modifiable (sauf `Program.cs` en ajout d'une ligne, fusionné par le coordinateur dans l'ordre WP4 → WP10).

---

## 6. Stratégie de test

| Niveau                         | Contenu                                                                                                              | Où                                                                               |
| ------------------------------ | -------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------- |
| Unitaire pur                   | `ShortcutMap`, `DayFingerprint`, `DayPlanner`, `DayStateLabels`, `ConflictResolution`                                | `tests\TimeSheetHelper.Tests\App\*`, `…\Engine\*`                                |
| Services                       | `NavSummaryService`, `DaySubmissionService` avec SQLite/EF de test (`EngineTestHost` comme modèle)                   | idem                                                                             |
| Migration                      | base existante → migrée, rétro-remplissage (G11), `Down`                                                             | `…\Storage\DaySubmissionMigrationTests.cs` (modèle `RemovedSlotsMigrationTests`) |
| Marque / câblage source        | présence rail, ARIA, verrous d'UI, références `shell.js`, absence de couleurs en dur (style `SubmitPageWiringTests`) | `…\App\ShellMarkupTests.cs`, `DayStripWiringTests.cs`                            |
| Intégration avec faux Forecast | `FakeForecastWriteApi` : G1–G9 de bout en bout (service → writer → fake)                                             | `…\App\DaySubmission*Tests.cs`                                                   |
| Visuel                         | Playwright MCP sur l'app lancée (WP7, WP15) ; captures versionnées dans `docs\plans\screenshots\`                    | manuel-automatisé                                                                |
| Non-régression                 | **tous** les tests existants (`dotnet test tests\TimeSheetHelper.Tests`) à chaque fin de vague                       | —                                                                                |

Règles : un test ne vérifie que ce que le changement introduit ; pas de tests qui répètent l'implémentation, des constantes ou des valeurs par défaut ; pas de `Sleep` ; pas d'accès réseau ni au vrai Forecast.

---

## 7. Correspondance scénarios du prototype → tests (WP12)

| Scénario `submit.js` | Garantie                             | Test attendu                                                                        |
| -------------------- | ------------------------------------ | ----------------------------------------------------------------------------------- |
| `neuf`               | écriture nouvelle une seule fois     | `G1_SecondSendWritesNothing`                                                        |
| `inchange`           | « Rien de nouveau à envoyer »        | `G1_UnchangedDay_ShowsNothingToSend`                                                |
| `modifie`            | mise à jour, pas de 2ᵉ saisie        | `G5_ModifiedDay_UpdatesExistingEntry`, `G5_UpdateToolMissing_BlocksAndNeverCreates` |
| `conflit`            | choix explicite, jamais d'écrasement | `G6_Conflict_*` (garder/accepter/compléter/retirer)                                 |
| `partielle`          | reprise après interruption           | `G3_InterruptedSend_ResumeDoesNotDuplicate`                                         |
| `double`             | verrou/concurrence                   | `G2_ConcurrentSends_SingleWrite`                                                    |

---

## 8. Protocole d'autonomie

### 8.1 Règles communes (valent pour tous les agents)

1. Lire `.squad\team.md`, `.squad\routing.md`, la charte de l'agent (`.squad\agents\<agent>\charter.md`), `.squad\decisions.md` (sections pertinentes) et ce plan (le lot + §2, §3, §8) avant de commencer.
2. **Aucun commit** par un sous-agent. Les commits sont faits **par le coordinateur uniquement**, un par vague, sur la branche **`squad/nav-c`** créée depuis la branche courante (D1). Messages : `feat(nav-c): W<n> — <résumé>` + trailer `Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>`. **Ne jamais contourner** un hook `pre-commit`/`pre-push` (`--no-verify` interdit) : si un hook échoue, corriger la cause. (Aucun hook actif détecté dans `.git\hooks` à la rédaction, seulement des `.sample`.)
3. **Interdit** : `git stash`, `git clean`, `git checkout`/`git restore` de fichiers, `git reset --hard`, suppression de fichiers non créés par le lot. L'arbre de travail contient des changements **non liés** déjà présents (`App.razor`, `TimeSheetHelper.App.csproj`, `TrayIcon.cs`, `docs\icon-proposals\`, `docs\nav-prototypes\`, `Assets\`, `favicon.svg`, `TrayIconTests.cs`) : **ne pas les annuler ni les reformater** ; `App.razor` n'est modifié que par WP3, en ajout.
4. Pas de `/tmp` ni de dossier temporaire : fichiers de travail dans le dépôt, nettoyés en fin de lot.
5. Ne modifier que les fichiers listés dans le lot ; besoin hors périmètre ⇒ « demande de changement » dans le rapport.
6. UI en français ; commentaires de code rares (seulement si nécessaire) ; pas de secrets ; chemins Windows (`\`).
7. Après modification : `dotnet build` puis tests ciblés (`dotnet test tests\TimeSheetHelper.Tests --filter "FullyQualifiedName~<NomTests>"`), puis suite complète du projet de tests avant rapport.
8. Rapport obligatoire : `.squad\decisions\inbox\<agent>-<slug>.md` (format §A.0).

### 8.2 Modèles (routage règle 9)

Toujours passer `model` explicitement, **Anthropic uniquement** : `claude-opus-5.5` (conception, domaine, UI complexe, tests d'idempotence, sécurité), `claude-sonnet-5.5` (lots plus simples : design spec, NavSummary, docs, Playwright, tests d'UI), `claude-haiku-4.5` (étapes mécaniques pas-à-pas : régénérer un snapshot, lancer build/tests, copier des captures). Ne pas passer `reasoning_effort`/`context_tier` sauf demande.

### 8.3 Définition de « terminé » (DoD) par lot

- ✔ Fichiers conformes à la liste (ni plus ni moins) ; ✔ `dotnet build` sans nouvel avertissement ; ✔ tests ciblés + suite complète verts ; ✔ critères d'acceptation du lot cochés un par un dans le rapport ; ✔ aucun fichier d'un autre propriétaire modifié ; ✔ nettoyage fait ; ✔ rapport écrit ; ✔ revue (§8.4) passée.

### 8.4 Porte de revue

Chaque lot : (1) auto-vérification par l'auteur ; (2) revue **Inspecteur Gadget** (tests, anti-tautologie, couverture des garanties) ; (3) agent `code-review` sur le diff du lot ; (4) lots sensibles (WP8, WP9, WP10) : + relecture Yoda. Le coordinateur n'avance à la vague suivante que sur revue verte.

### 8.5 Verrouillage après rejet (lockout)

Si un relecteur **rejette** un lot, l'**auteur est verrouillé** sur ces artefacts : un **autre** agent (même rôle si possible, ou Yoda) révise, en repartant des commentaires du rejet. Un auteur rejeté peut relire/commenter mais pas ré-éditer. Deux rejets successifs sur un lot ⇒ escalade Yoda (re-découpage) ; troisième ⇒ arrêt (§8.7).

### 8.6 Blocages — défauts documentés (ne pas demander)

| Situation                                              | Défaut appliqué                                                                  |
| ------------------------------------------------------ | -------------------------------------------------------------------------------- |
| Outil MCP update/delete absent                         | Capacité `Unavailable`, action désactivée + lien Forecast (D3)                   |
| Objectif quotidien incertain                           | `Engine:TargetHoursPerDay` de l'app (D4)                                         |
| Divergence prototype vs. app existante                 | L'app existante prime pour la **logique**, le prototype prime pour le **visuel** |
| Nom/chemin différent de celui du plan                  | Utiliser le nom réel du code, le noter dans le rapport                           |
| Test existant devenu faux à cause du lot               | L'auteur adapte l'assertion minimalement, justifié ; Gadget valide               |
| Ctrl+K                                                 | Non implémenté (D5)                                                              |
| Cartes de démonstration/légende/scénarios du prototype | Exclues (D6)                                                                     |
| Heures flottantes/arrondis                             | 2 décimales, comparaison avec tolérance définie dans `ForecastHours` (existant)  |

### 8.7 Conditions d'**arrêt et question** (seules autorisées)

1. Toute opération qui écrirait dans le **vrai** Forecast ou sur la base de données de production pour vérification.
2. Perte de données potentielle : une migration qui ne peut pas être additive.
3. Commande destructive git/fichier nécessaire (stash/clean/reset/checkout).
4. Découverte d'un secret dans le dépôt ou dans un journal.
5. Troisième rejet de revue sur le même lot, ou build/tests rouges persistants après 3 tentatives de correction distinctes.
6. Contradiction entre ce plan approuvé et une décision déjà actée dans `.squad\decisions.md` qui ne se résout pas par les défauts §8.6.

### 8.8 Politique d'essais (retry)

Échec de build/test : jusqu'à **3** tentatives, chacune avec une cause identifiée **différente** (pas de répétition aveugle) ; tests instables : relancer une fois pour confirmer l'instabilité, puis corriger la cause (pas de `Skip`). Échec d'outil (Playwright, MCP) : une relance, puis autre moyen (ex. tests de marque) et le noter. Ensuite : blocage §8.7-5.

### 8.9 Retour arrière (rollback)

- Un commit par vague ⇒ retour au commit de la vague précédente (`git revert <sha>` seulement sur instruction explicite de l'équipe — jamais `reset --hard`).
- Migration : `Down` testée (WP8) ; la migration `DaySubmissions` est la **seule** de ce chantier ; sauvegarde de la base locale (copie du fichier `.db` dans `LocalFolders`, hors dépôt) avant d'exécuter l'app avec la migration pour vérification.
- Phase 2 livrée derrière l'ancien flux conservé (WP11) tant que la parité n'est pas validée ⇒ désactivation simple.
- Phase 1 isolée : le retour à l'ancienne coquille = revenir sur `MainLayout*` + fichiers `Layout\*` créés.

### 8.10 Rapports

Chaque agent écrit `.squad\decisions\inbox\<agent>-<slug>.md` : lot, fichiers créés/modifiés, décisions prises (avec le défaut utilisé), critères d'acceptation (✔/✘ + preuve), commandes lancées + résultats, demandes de changement, risques restants. Scribe fusionne dans `.squad\decisions.md`.

---

## 9. Décisions ouvertes pour Éric (défauts recommandés)

| #   | Question                                                                                                         | Défaut recommandé                                                                                                                                                                                                  |
| --- | ---------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| D1  | Branche et commits                                                                                               | Branche `squad/nav-c`, un commit par vague (7), sans push automatique ; vérifier les hooks pre-push avant tout push                                                                                                |
| D2  | Modèle de persistance                                                                                            | Nouvelle table `DaySubmission` par jour + journal, migration additive, rétro-remplissage depuis `SubmittedUtc` (statut « Envoyé (empreinte inconnue) »)                                                            |
| D3  | Mise à jour/suppression Forecast                                                                                 | Seulement si l'outil MCP existe ; sinon « indisponible » + lien « modifier dans Forecast » ; jamais de contournement créer+supprimer                                                                               |
| D4  | Objectif quotidien                                                                                               | Valeur de configuration de l'app (`TargetHoursPerDay`, 8 h), pas les 7,5 h du prototype.  L'objectif quotidien est une référence pas un blocant. L'important est d'atteindre l'objectif hebdomadaire de 40 heures. |
| D5  | Palette Ctrl+K                                                                                                   | Hors périmètre (WP17 réservé si vous la voulez)                                                                                                                                                                    |
| D6  | Éléments de démonstration du prototype (cartes de scénario, légende, garde-fous « pédagogiques ») + barre mobile | Porter la barre mobile telle que dessinée ; exclure les cartes de démonstration ; les garde-fous deviennent un texte d'aide sur la page Soumettre                                                                  |

---

## 10. Risques

| Risque                                                         | Impact                        | Mitigation                                                                                                                                                  |
| -------------------------------------------------------------- | ----------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Outils MCP update/delete inexistants ou différents             | Fonctionnalités C+ partielles | Spike WP9 en premier ; détection de capacités ; défaut D3                                                                                                   |
| Doublons d'heures dans Forecast (incident réel)                | Élevé                         | Relecture avant écriture, verrou par jour, idempotence, G1–G11, vérifs Playwright uniquement sur fake                                                       |
| Conflits de fusion sur `app.css` / `Submit.razor`              | Moyen                         | Propriété unique, blocs délimités, ordre séquentiel §3                                                                                                      |
| Migration EF sur base de l'utilisateur                         | Élevé                         | Additive, `Down` testée, copie de sauvegarde, test sur base peuplée                                                                                         |
| Régression des pages existantes lors de la migration d'en-tête | Moyen                         | WP6 « à l'identique », tests existants, WP7 visuel                                                                                                          |
| Faux positifs « modifié » (empreinte instable)                 | Moyen                         | Forme canonique + G10                                                                                                                                       |
| Tests de câblage source fragiles                               | Faible                        | Adaptés minimalement, revus par Gadget                                                                                                                      |
| Fichiers non liés déjà modifiés dans l'arbre de travail        | Moyen                         | Règle §8.1-3 : ne pas annuler ni inclure dans les commits sans décision (le coordinateur ajoute les fichiers **par chemin explicite**, jamais `git add -A`) |
| Dépassement de contexte des agents                             | Moyen                         | Lots bornés, prompts auto-suffisants (annexe)                                                                                                               |

**Compatibilité des données** : migration additive ; `TimesheetDraft.SubmittedUtc` conservé et non modifié ; les jours d'une semaine déjà soumise passent « Envoyé (empreinte inconnue) » ⇒ jamais ré-envoyés silencieusement ; le premier « modifié » détecté déclenche une comparaison avec Forecast avant toute écriture.

**Effort total estimé** : S : WP2, WP14 · M : WP1, WP4, WP5, WP7, WP13, WP15, WP16 · L : WP3, WP6, WP8, WP9, WP10, WP11, WP12.

---

# Annexe A — Prompts prêts à coller (un par lot)

> Le coordinateur colle **§A.0 (bloc commun) + le prompt du lot**. Chaque prompt est auto-suffisant : l'agent n'a aucune mémoire de cette conversation. Toujours passer `model` explicitement (indiqué par lot).

## A.0 Bloc commun (à placer en tête de chaque prompt)

```
Contexte : dépôt C:\git-decarufe\time-sheet-helper (.NET, Blazor Server, UI en FRANÇAIS, Windows, chemins avec \).
Projet : time-sheet-helper aide à remplir une feuille de temps Forecast. On remplace la navigation de MainLayout.razor par le
prototype « C » (rail d'icônes, panneau contextuel, en-tête de semaine, stepper Saisir→Vérifier→Soumettre, thème clair/sombre,
responsive ≤ 860 px, raccourcis) puis on ajoute la soumission jour par jour « C+ ». Prototypes : docs\nav-prototypes\c-rail\
(index.html, submit.js, submit.css) et docs\nav-prototypes\shared\ (base.css, pages.js). Plan complet : docs\plans\nav-c-implementation-plan.md
(lis le lot concerné + §2 principes, §3 fichiers partagés, §8 protocole).
Squad : lis .squad\team.md, .squad\routing.md, ta charte .squad\agents\<ton-agent>\charter.md, et les sections utiles de .squad\decisions.md.
RÈGLES ABSOLUES :
- Ne fais AUCUN commit. Interdits : git stash, git clean, git checkout/restore de fichiers, git reset --hard, --no-verify.
- Des changements non liés existent déjà dans l'arbre de travail (App.razor, TimeSheetHelper.App.csproj, TrayIcon.cs, docs\icon-proposals\,
  docs\nav-prototypes\, Assets\, favicon.svg, TrayIconTests.cs) : ne les annule pas, ne les reformate pas.
- Modifie UNIQUEMENT les fichiers listés pour ton lot. Besoin ailleurs => note une « demande de changement » dans ton rapport.
- N'utilise jamais /tmp ni un dossier temporaire ; nettoie tes fichiers de travail.
- N'écris jamais dans le vrai Forecast, n'utilise aucun secret ; tests avec faux (fakes) uniquement.
- Commentaires de code seulement quand indispensables. Textes d'interface en français.
- Valide : `dotnet build` puis `dotnet test tests\TimeSheetHelper.Tests` (ciblé avec --filter pendant le travail, suite complète avant de rapporter).
  En cas d'échec : max 3 tentatives, chacune avec une cause différente, puis rapporte le blocage.
- Défauts si incertain (ne pose pas de question) : voir plan §8.6. Conditions d'arrêt autorisées : plan §8.7.
FORMAT DU RAPPORT (obligatoire) : écris .squad\decisions\inbox\<agent>-<slug>.md contenant : lot, fichiers créés/modifiés,
décisions prises (+ défaut utilisé), critères d'acceptation ✔/✘ avec preuve, commandes lancées + résultats, demandes de changement, risques restants.
Réponse finale : ≤ 150 mots, chemin du rapport + statut (Terminé / Bloqué + raison).
```

## A.1 WP1 — Yoda — `model: claude-opus-5.5`

```
[A.0] Lot WP1 — ADR + contrats d'architecture (aucun code de production).
But : figer les contrats pour les lots suivants. Lis : MainLayout.razor, Routes.razor, Pages\*.razor (routes @page réelles),
Services\SubmissionReviewService.cs, Forecast\Submission\SubmissionGap.cs, Forecast\ForecastTimesheetWriter.cs, ForecastToolResolver.cs,
Core\Timesheets\TimesheetDraft.cs, Storage\TimeSheetDbContext.cs, Engine\EngineOptions.cs, Components\Shared\SubmissionStatus.cs, DaySelection.cs,
et les prototypes c-rail (index.html, submit.js, submit.css).
Crée : docs\plans\nav-c-adr.md et docs\plans\nav-c-contracts.md contenant :
1) signatures C# EXACTES (noms vérifiés par grep) de NavSummary/DaySummary/INavSummaryService, ShortcutMap, ShellState (+ emplacement d'en-tête RenderFragment),
   DaySubmission/DayStatus/CellState/DayPlan/IDaySubmissionService/ConflictResolution, ForecastCapabilities (+ rôles UpdateTimeEntry/DeleteTimeEntry) ;
2) table de routes ↔ Alt+1..7 d'après les @page réelles ; 3) matrice états de cellule → libellé FR / icône / token couleur / actions permises
(nouveau, inchangé, modifié, supprimé, conflit, ignoré, verrouillé, non tenté) ; 4) mapping scénarios neuf/inchange/modifie/conflit/partielle/double → tests ;
5) lignes DI réservées dans Program.cs ; 6) décisions D1–D6 du plan appliquées avec leurs défauts.
Acceptation : aucun nom inventé sans le marquer « NOUVEAU » ; chaque état a libellé+icône+règle ; contrats cohérents avec l'existant.
Fichiers autorisés : uniquement les 2 documents ci-dessus.
```

## A.2 WP2 — Martha Stewart — `model: claude-sonnet-5.5`

```
[A.0] Lot WP2 — Spécification de design (aucun code de production).
Lis : docs\nav-prototypes\shared\base.css, c-rail\index.html, c-rail\submit.css, src\TimeSheetHelper.App\wwwroot\app.css, MainLayout.razor(.css).
Crée docs\plans\nav-c-design-spec.md : tokens (--accent-bg, --surface, --shadow, --radius, etc.) pour data-theme clair ET sombre avec ratios de contraste AA calculés ;
grille rail/panneau/en-tête ; point de rupture 860 px (barre basse + tiroir) ; spec de chaque composant (Rail, ContextPanel, WeekHeader, StepIndicator, MobileNav,
DayStrip, DayPanel, CellStateBadge, ConflictChooser, DayJournal) ; états de cellule = couleur + icône + libellé (jamais la couleur seule) ; focus visible ;
prefers-reduced-motion ; correspondance classes prototype → classes app ; liste des captures de référence (pages × clair/sombre × 1440×900 / 390×844).
Fichier autorisé : docs\plans\nav-c-design-spec.md uniquement.
```

## A.3 WP3 — Cendrillon — `model: claude-opus-5.5`

```
[A.0] Lot WP3 — Coquille de navigation C (propriétaire de MainLayout*, app.css, Layout\*). Prérequis : docs\plans\nav-c-contracts.md et nav-c-design-spec.md.
Crée dans src\TimeSheetHelper.App : Components\Layout\Rail.razor(.css), ContextPanel.razor(.css), WeekHeader.razor(.css), StepIndicator.razor(.css),
MobileNav.razor(.css), ShellState.cs, ShortcutMap.cs (pur, testable), wwwroot\shell.js (thème Alt+T, Alt+1..7, [ ], Ctrl+B ; localStorage tsh.theme / tsh.panel ; ignore
les raccourcis quand le focus est dans input/textarea/select/contenteditable).
Modifie : Components\Layout\MainLayout.razor(.css), wwwroot\app.css (tokens + [data-theme] seulement, bloc « /* === nav-c: shell === */ »), Components\App.razor
(ajout de la référence shell.js et attribut data-theme initial anti-flash — conserve les modifications existantes du fichier).
Reprends la structure et le comportement de docs\nav-prototypes\c-rail\index.html. Le panneau lit INavSummaryService : tant que WP4 n'est pas fusionné, utilise un stub conforme
au contrat. Accessibilité : <nav aria-label>, aria-current, focus visible, annonces FR. Aucune couleur en dur dans les .razor.css (tokens uniquement).
Acceptation : voir plan §4 WP3. Ne touche à aucune page de Pages\ (c'est WP6).
```

## A.4 WP4 — Glados — `model: claude-sonnet-5.5`

```
[A.0] Lot WP4 — NavSummaryService en lecture seule.
Crée src\TimeSheetHelper.App\Services\NavSummaryService.cs (+ modèles NavSummary/DaySummary et interface selon docs\plans\nav-c-contracts.md) : pour une semaine, par jour :
heures proposées, objectif (Engine:TargetHoursPerDay de EngineOptions — vérifie le nom réel), nombre d'écarts, état d'étape (Saisir/Vérifier/Soumettre), badge. Réutilise WeekService,
WeekProposalBuilder, SubmissionReviewService ; AUCUN appel d'écriture ni réseau Forecast ; cache court par semaine. Ajoute UNE ligne DI dans Program.cs.
Tests : tests\TimeSheetHelper.Tests\App\NavSummaryServiceTests.cs (semaine vide, jour partiel, complet, avec écarts, week-end ; échoueraient si la logique était retirée).
Fichiers autorisés : ceux-ci uniquement.
```

## A.5 WP8 — Glados — `model: claude-opus-5.5`

```
[A.0] Lot WP8 — Domaine + stockage par jour + migration EF (SEUL propriétaire de TimeSheetDbContext et Migrations).
Crée src\TimeSheetHelper.Core\Timesheets\DaySubmission.cs (WeekStart, Date, Status, Fingerprint?, SentUtc?, cellules envoyées : TaskId, SentHours, ForecastEntryId?),
SubmissionJournalEntry.cs (DayId, TimestampUtc, Kind, Message SANS secrets/jetons/corps de courriel, Outcome), enum DayStatus (selon docs\plans\nav-c-contracts.md).
Modifie Storage\TimeSheetDbContext.cs (2 DbSet, index unique (WeekStart, Date), UtcDateTimeConverter existant). Génère la migration : `dotnet ef migrations add DaySubmissions`
(DesignTimeDbContextFactory existe) ; NE modifie PAS le snapshot à la main. Rétro-remplissage : pour chaque TimesheetDraft avec SubmittedUtc non nul => DaySubmission « Envoyé » par jour avec
Fingerprint = null. Migration strictement additive, Down fonctionnelle.
Tests : tests\TimeSheetHelper.Tests\Storage\DaySubmissionMigrationTests.cs (modèle : RemovedSlotsMigrationTests) : base existante migrée + rétro-remplissage (G11), unicité, aller-retour.
Avant de tester l'app sur une vraie base, ne le fais pas : tests sur bases de test uniquement.
```

## A.6 WP6 — Cendrillon — `model: claude-opus-5.5`

```
[A.0] Lot WP6 — Migration des pages vers la coquille (WP3 doit être fusionné).
Modifie : Pages\Week, Day, Submit (en-tête et barre d'outils SEULEMENT — aucune logique C+), Rules, ForecastTasks, Sources, ActivityLog (+ leurs .razor.css), NotFound/Error si besoin, wwwroot\app.css
(uniquement dans un bloc « /* === nav-c: pages === */ »). Retire les h1/toolbars redondants ; place les actions de page dans l'emplacement d'en-tête fourni par ShellState/WeekHeader (contrat WP1) ;
corrige le CSS scopé en conflit. Comportement des pages IDENTIQUE (mêmes handlers, mêmes PageTitle). Pas de scroll horizontal ≤ 860 px.
Si des tests de câblage de Submit deviennent faux à cause de ton changement : ajuste l'assertion au minimum et justifie dans le rapport (Gadget validera). Tous les tests existants doivent rester verts.
```

## A.7 WP5 — Inspecteur Gadget — `model: claude-sonnet-5.5`

```
[A.0] Lot WP5 — Tests Phase 1 (nouveaux fichiers de tests seulement ; WP3 et WP4 fusionnés).
Crée tests\TimeSheetHelper.Tests\App\ShortcutMapTests.cs (Alt+1..7, [ ], Ctrl+B, Alt+T ; ignorés dans les champs de saisie) et ShellMarkupTests.cs (tests de marque/source dans le style de SubmitPageWiringTests :
rail = les 7 routes réelles, aria-current, nav labellisé, shell.js référencé, pas de couleur en dur dans les .razor.css de Layout). Complète NavSummaryServiceTests si lacunes.
Applique le skill test-discipline : chaque test doit échouer si la fonctionnalité est retirée ; pas de tests tautologiques. Ne modifie aucun code de production : signale tout défaut trouvé.
```

## A.8 WP9 — Glados — `model: claude-opus-5.5`

```
[A.0] Lot WP9 — Écriture Forecast : mise à jour, suppression, idempotence.
ÉTAPE 1 (spike) : inspecte ForecastToolResolver, ForecastMcpSession, ForecastMcpConnector, ForecastCommands, fixtures de tests, pour établir quels outils MCP Forecast existent
(update/delete d'une saisie de temps). Écris .squad\decisions\inbox\glados-forecast-mcp-capabilities.md. Défaut : outil absent => capacité Unavailable, JAMAIS de contournement créer+supprimer.
ÉTAPE 2 : étends ForecastToolRole (UpdateTimeEntry, DeleteTimeEntry) + ForecastMcpSession.Has(role) ; ForecastTimesheetWriter : UpdateCellAsync / DeleteCellAsync avec relecture complète AVANT
écriture (si lecture incomplète => aucune écriture), CancellationToken respecté, un résultat ForecastCellResult par cellule (statuts additifs Updated/Deleted/Skipped dans ForecastFillReport),
réutilise ForecastCellEvaluator ; ForecastArgumentBuilder/ForecastCommands pour les arguments. FillWeekAsync existant : comportement inchangé (tests actuels verts).
Tests unitaires avec un fake minimal (le fake complet est le lot WP12) : outil absent, lecture tronquée, annulation.
Fichiers autorisés : Forecast\ForecastToolResolver.cs, ForecastMcpSession.cs, ForecastTimesheetWriter.cs, ForecastCommands.cs, ForecastArgumentBuilder.cs, ForecastFillReport.cs + nouveaux tests.
```

## A.9 WP7 — Martha Stewart (+ Gadget) — `model: claude-sonnet-5.5`

```
[A.0] Lot WP7 — Vérification visuelle Phase 1 avec Playwright MCP (aucune modification de code de production).
Lance l'app : `dotnet run --project src\TimeSheetHelper.App` (lis l'URL dans la sortie ; tue le processus par son PID à la fin). Pour chaque page (Semaine, Jour, Soumettre, Règles, Tâches, Sources, Activité)
× {1440×900, 390×844} × {clair, sombre} : capture dans docs\plans\screenshots\p1\<page>-<largeur>-<theme>.png ; vérifie rail/panneau/en-tête, Alt+T, Alt+1..7, [ ], Ctrl+B, tiroir mobile,
absence de débordement horizontal, console sans erreur. Compare au prototype docs\nav-prototypes\c-rail\index.html et à docs\plans\nav-c-design-spec.md.
N'écris RIEN dans Forecast. Rapport : .squad\decisions\inbox\martha-p1-visual-report.md avec écarts classés bloquant/mineur ; réponse finale : « 0 bloquant » ou liste des bloquants.
```

## A.10 WP10 — Glados — `model: claude-opus-5.5`

```
[A.0] Lot WP10 — Moteur de soumission par jour (WP8 et WP9 fusionnés ; contrats : docs\plans\nav-c-contracts.md).
Crée : src\TimeSheetHelper.App\Services\DaySubmissionService.cs (PlanAsync, SendAsync(day, resolutions, ct), SendReadyAsync(week, ct), CancelAsync),
src\TimeSheetHelper.Forecast\Submission\DayPlanner.cs (pur), DayFingerprint.cs (SHA-256 sur forme canonique : tri par tâche, heures 2 décimales en culture invariante, UTF-8),
DayLock.cs (verrou par jour + état persisté « en cours » avec expiration), ConflictResolution.cs (Keep/AcceptForecast/Complement/Remove). Ajoute UNE ligne DI dans Program.cs.
SubmissionReviewService : exposer des données seulement, sans changer le comportement.
Règles : états NonTenté→EnCours→Envoyé|Partiel|Erreur ; Envoyé→Modifié si empreinte ≠ ; Conflit si Forecast contient des heures non écrites par l'app ; Verrouillé si approuvé/facturé ;
« Rien de nouveau à envoyer » si tout inchangé ; relecture Forecast AVANT toute écriture ; jamais d'écriture si lecture incomplète ; ignorées/exclues jamais écrites ; Complement = delta seulement ;
mise à jour via WP9 sinon action désactivée avec raison ; chaque transition journalisée SANS secrets ; aucune écriture Forecast hors ForecastTimesheetWriter ; aucune dépendance Razor.
Garanties à rendre testables : G1–G11 (plan §1.3). Écris des tests unitaires de base pour DayPlanner/DayFingerprint ; la batterie anti-doublon complète est le lot WP12.
```

## A.11 WP11 — Cendrillon — `model: claude-opus-5.5`

```
[A.0] Lot WP11 — UI C+ sur la page Soumettre (WP6 et WP10 fusionnés ; propriétaire de Submit.razor à ce stade).
Crée dans Components\Shared : DayStrip, DayPanel, CellStateBadge, ConflictChooser, DayJournal, SendReadyBar (.razor + .razor.css), DayStateLabels.cs (états→libellé FR/icône/token, pur).
Modifie Pages\Submit.razor(.css) et wwwroot\app.css (bloc « /* === nav-c: submit === */ »). Base : docs\nav-prototypes\c-rail\submit.js/submit.css (scénarios neuf/inchange/modifie/conflit/partielle/double
= données de démonstration, NE PAS les porter dans l'app) et docs\plans\nav-c-design-spec.md.
Exigences : chaque état de cellule avec icône+libellé ; bouton d'envoi désactivé pendant l'envoi avec annonce aria-live ; confirmation avant mise à jour/suppression ; « Rien de nouveau à envoyer » ;
« Soumettre les jours prêts » liste d'abord les jours concernés ; actions indisponibles expliquées ; l'ancien flux reste disponible tant que la parité n'est pas validée ; l'UI n'appelle JAMAIS ForecastApi
(uniquement IDaySubmissionService). Aucun changement de logique métier dans les services.
```

## A.12 WP12 — Inspecteur Gadget — `model: claude-opus-5.5`

```
[A.0] Lot WP12 — Tests anti-doublon / idempotence (nouveaux fichiers de tests ; WP9 et WP10 fusionnés).
Crée tests\TimeSheetHelper.Tests\App\FakeForecastWriteApi.cs (lecture tronquée, timeout à la n-ième écriture, saisie préexistante, jour verrouillé, outils update/delete absents, latence contrôlable
avec barrière pour la concurrence, compteur d'appels d'écriture), puis DaySubmissionIdempotenceTests, DayPlannerTests, DayFingerprintTests, ConflictResolutionTests, SendReadyDaysTests, DayLockTests,
DayStateLabelsTests, DayStripWiringTests. CHAQUE garantie G1–G11 (plan §1.3) a ≥ 1 test nommé « G<n>_… ». Vérifie les compteurs d'écriture (0 au 2ᵉ envoi). Concurrence déterministe, pas de Thread.Sleep.
Prouve par mutation manuelle (documentée dans le rapport, code de production remis en état exactement) que les tests échouent quand la garde est retirée. Skill test-discipline obligatoire.
Ne modifie pas le code de production : signale les défauts.
```

## A.13 WP13 — Mafia Boy — `model: claude-opus-5.5` (agent `security-review`)

```
[A.0] Lot WP13 — Revue de sécurité du chantier nav-c (lecture seule, aucun correctif).
Portée : DaySubmission/SubmissionJournalEntry (contenu persisté, rétention), écriture Forecast (ForecastTimesheetWriter, ForecastArgumentBuilder : injection/validation), DaySubmissionService (verrous, état « en cours »),
wwwroot\shell.js (XSS, localStorage), composants Razor (rendu des noms de tâches/messages = encodage), absence de nouveaux endpoints HTTP (confirme), journal sans secrets/jetons/corps de courriel/contenu d'activité (G12),
journalisation ILogger. Écris .squad\decisions\inbox\mafiaboy-nav-c-security-review.md : constats classés High/Medium/Low avec fichier:ligne, preuve et correctif proposé. Ne modifie aucun fichier de code.
```

## A.14 WP14 — Jimmy Neutron — `model: claude-sonnet-5.5`

```
[A.0] Lot WP14 — Documentation.
Mets à jour README.md (navigation, raccourcis Alt+1..7 / [ ] / Ctrl+B / Alt+T, thèmes, responsive) et crée docs\submission-by-day.md (états de jour et de cellule, garanties anti-doublon G1–G12,
reprise après interruption, limites : mise à jour/suppression selon les outils MCP disponibles, aucun envoi automatique à l'approbation). Vérifie chaque affirmation contre le code (noms de raccourcis,
libellés, états). FR. Fichiers autorisés : README.md, docs\submission-by-day.md.
```

## A.15 WP15 — Martha Stewart (+ Gadget) — `model: claude-sonnet-5.5`

```
[A.0] Lot WP15 — Vérification visuelle Phase 2 (Playwright MCP, aucune modification de code de production).
Lance l'app avec un fake local de Forecast/données de démonstration (jamais le vrai Forecast ; aucune écriture réelle). Capture la page Soumettre dans les états neuf/inchangé/modifié/conflit/partielle/double
× {1440×900, 390×844} × {clair, sombre} → docs\plans\screenshots\p2\. Vérifie : états lisibles sans couleur seule, verrou pendant l'envoi, « Rien de nouveau à envoyer », liste « jours prêts », clavier, console propre.
Rapport : .squad\decisions\inbox\martha-p2-visual-report.md. Si un fake local n'existe pas, utilise les tests de marque et note la limite plutôt que de toucher au vrai service.
```

## A.16 WP16 — Yoda — `model: claude-opus-5.5`

```
[A.0] Lot WP16 — Stabilisation finale.
`dotnet build TimeSheetHelper.sln` (aucun nouvel avertissement) et `dotnet test tests\TimeSheetHelper.Tests` (100 % vert). Relis le diff complet de la branche squad/nav-c (git diff --stat, git diff par fichier) ;
vérifie : propriétés de fichiers partagés respectées (plan §3), aucun bloc nav-c orphelin dans app.css, aucune couleur en dur, aucun secret, aucun fichier temporaire, ancien flux Soumettre toujours fonctionnel,
tous les DoD. Corrige ou délègue les corrections à un agent différent de l'auteur d'origine (plan §8.5). Écris docs\plans\nav-c-final-report.md (tableau lot × DoD × preuve, écarts connus).
Ne commit pas (le coordinateur le fait).
```

---

## Annexe B — Checklist coordinateur par vague

1. Lancer les lots de la vague **en parallèle** (modèle explicite) avec §A.0 + prompt. 2. Lire les rapports `.squad\decisions\inbox\`. 3. Porte de revue (§8.4). 4. `dotnet build` + `dotnet test tests\TimeSheetHelper.Tests`. 5. Contrôle des fichiers modifiés vs liste du lot (`git status`, jamais `git add -A` : ajouter par chemins). 6. Commit de vague sur `squad/nav-c` avec trailer. 7. Scribe fusionne la boîte de décisions. 8. Vague suivante.
