# Squad Team

> time-sheet-helper

## Coordinator

| Name  | Role        | Notes                                              |
| ----- | ----------- | -------------------------------------------------- |
| Squad | Coordinator | Routes work, enforces handoffs and reviewer gates. |

## Human Members

Human members retain final authority over project decisions. The active human is
identified via `git config user.name` at session start and validated against the roster
below — see `routing.md` → Local Human Identity & Decision Gate.

| Name   | Role            | Authority                         |
| ------ | --------------- | --------------------------------- |
| Felix  | Human developer | Technical lead and decision-maker |
| Hélène | Human developer | Technical lead and decision-maker |
| Éric   | Human developer | Technical lead and decision-maker |

## Members

| Name              | Role                                | Charter                               | Status           |
| ----------------- | ----------------------------------- | ------------------------------------- | ---------------- |
| Yoda              | Architecte                          | .squad/agents/yoda/charter.md         | 🏗️ Architecte    |
| Morpheus          | Product Owner                       | .squad/agents/morpheus/charter.md     | 🧭 Product Owner |
| Cendrillon        | Développeur Frontend                | .squad/agents/cendrillon/charter.md   | ⚛️ Frontend      |
| Glados            | Développeur Backend                 | .squad/agents/glados/charter.md       | 🔧 Backend       |
| Inspecteur Gadget | Testeur                             | .squad/agents/gadget/charter.md       | 🧪 Testeur       |
| Mafia Boy         | Hacker / Sécurité                   | .squad/agents/mafiaboy/charter.md     | 🔒 Sécurité      |
| Martha Stewart    | Designer UI/UX                      | .squad/agents/martha/charter.md       | 🎨 Design        |
| Zeus              | Expert Cloud                        | .squad/agents/zeus/charter.md         | ☁️ Cloud         |
| R2-D2             | Expert DevOps                       | .squad/agents/r2d2/charter.md         | ⚙️ DevOps        |
| Jimmy Neutron     | Spécialiste Documentation Technique | .squad/agents/jimmy/charter.md        | 📚 Documentation |
| Jar Jar Binks     | User (persona)                      | .squad/agents/jarjar/charter.md       | 👤 User          |
| Scribe            | Session Logger                      | .squad/agents/scribe/charter.md       | 📋 Scribe        |
| Ralph             | Work Monitor                        | .squad/agents/ralph/charter.md        | 🔄 Monitor       |
| Rai               | RAI Reviewer                        | .squad/agents/Rai/charter.md          | 🛡️ RAI           |
| Fact Checker      | Fact Checker                        | .squad/agents/fact-checker/charter.md | 🔍 Verifier      |

## Coding Agent

<!-- copilot-auto-assign: false -->

| Name     | Role         | Charter | Status          |
| -------- | ------------ | ------- | --------------- |
| @copilot | Coding Agent | —       | 🤖 Coding Agent |

### Capabilities

**🟢 Good fit — auto-route when enabled:**

- Bug fixes with clear reproduction steps
- Test coverage (adding missing tests, fixing flaky tests)
- Lint/format fixes and code style cleanup
- Dependency updates and version bumps
- Small isolated features with clear specs
- Boilerplate/scaffolding generation
- Documentation fixes and README updates

**🟡 Needs review — route to @copilot but flag for squad member PR review:**

- Medium features with clear specs and acceptance criteria
- Refactoring with existing test coverage
- API endpoint additions following established patterns
- Migration scripts with well-defined schemas

**🔴 Not suitable — route to squad member instead:**

- Architecture decisions and system design
- Multi-system integration requiring coordination
- Ambiguous requirements needing clarification
- Security-critical changes (auth, encryption, access control)
- Performance-critical paths requiring benchmarking
- Changes requiring cross-team discussion

## Project Context

- **Owner:** Eric De Carufel
- **Project:** time-sheet-helper (solution `TimeSheetHelper.sln`) — application Windows locale qui aide à remplir la feuille de temps hebdomadaire : collecte l'activité du consultant (fenêtre active, VS Code, Claude Code, Git, GitHub, Azure DevOps, calendrier/courriels/Teams), propose les heures par tâche Forecast, puis les écrit dans Forecast après validation de l'utilisateur. Seules des métadonnées sont conservées (jamais le corps des courriels/messages).
- **Stack:** .NET 10 / C# (`net10.0`, `net10.0-windows` pour l'hôte et les plugins Windows), nullable + implicit usings. Hôte `TimeSheetHelper.App` : ASP.NET Core + Blazor (Razor Components, rendu serveur interactif) en `WinExe` avec icône de notification (WinForms), CLI et serveur local `http://localhost:5178`. Stockage SQLite via EF Core (migrations dans `Storage`). Tests xUnit. Installeur WiX (MSI par utilisateur) + scripts PowerShell. Extension VS Code en TypeScript (esbuild, vitest, **pnpm** — jamais npm).
- **Architecture (projets de `src/`):**
  - `Core` — modèles et contrats sans dépendance (ActivityEvent, ActivitySource/SourceCatalog, ICollector, IActivitySink/IActivityHistory, Rule, TimesheetDraft, ForecastTask, ISecretStore, DataPaths).
  - `Plugins.Abstractions` — contrat des plugins de source (`ISourcePlugin`, `SourceDescriptor`, `IPluginContext`, `ISourceInstance`, `IPushReceiver`, `PluginApi.Version`).
  - `Storage` — `TimeSheetDbContext` (SQLite, dates en UTC), `ActivityStore`, migrations EF.
  - `Engine` — cœur métier : regroupement des activités (GroupKey), évaluation des règles, allocation en créneaux de 15 min (SlotAllocator), classification (règles puis Claude via l'API Anthropic), apprentissage des corrections, construction de la proposition de semaine (`WeekProposalBuilder`/`WeekService`).
  - `Forecast` — client du serveur MCP de Forecast (OAuth, DPAPI) : lecture/synchronisation des tâches, écriture des feuilles de temps.
  - `Collectors.Windows` (activité Windows, VS Code push, Claude Code), `Collectors.DevOps` (GitHub, Azure DevOps, Git local), `Collectors.Microsoft365` (Graph : calendrier, courriels envoyés, Teams via MSAL/WAM) — chacun est un **paquet plugin** (`plugin.json` + assembly) chargé à l'exécution dans un `AssemblyLoadContext` isolé depuis `plugins/<paquet>/` (fournis) ou `%LOCALAPPDATA%\TimeSheetHelper\plugins` (installés par l'utilisateur) ; référencés par l'App sans sortie (`ReferenceOutputAssembly=false`) et publiés par la cible MSBuild `StageBundledPlugins`.
  - `App` — composition (`AppServices`), `PluginHost` (cycle de vie des plugins, état en base), `SyncScheduler` (collecte pull périodique, 30 min), `IngestEndpoints` (`POST /ingest/{route}` protégé par jeton), `ForecastRunner`, `TrayIcon`, `WeeklyReminder`, `Cli`, composants Blazor (pages Week, Day, Rules, Sources, ForecastTasks, Submit, ActivityLog).
- **Flux:** sources (pull / continuous / push) → `IActivitySink` → SQLite (`ActivityEvent`, dédoublonnage par `ExternalId`) → Engine (règles, créneaux, classification) → proposition de semaine → correction par l'utilisateur (apprentissage de règles) → brouillon → Forecast (MCP).
- **Conventions clés:** modèle de plugins versionné (`PluginApi.Version`, refus d'une autre version majeure) ; un plugin défaillant n'arrête jamais les autres ; heures stockées en UTC, créneaux de 15 min ; secrets dans le Gestionnaire d'identification Windows (`ISecretStore`), jamais dans la config ni la base ; configuration `appsettings.json` + `appsettings.Local.json` (réglages par machine, jamais installés par le MSI) ; données dans `%LOCALAPPDATA%\TimeSheetHelper` (relocalisables via `TIMESHEETHELPER_DATA_DIR`) ; instance unique par dossier de données ; interface et messages en français ; `InternalsVisibleTo` vers `TimeSheetHelper.Tests` ; tests avec fixtures JSON des API externes (`tests/TimeSheetHelper.Tests/Fixtures`) et un plugin de test (`TestPlugin`) ; pnpm obligatoire côté extension VS Code.
- **Dépendances externes:** Microsoft Graph (app registration Entra, permissions déléguées, voir `docs/microsoft365-setup.md`), Azure DevOps (organisation `nexusinno`, PAT), GitHub (jeton), Forecast (serveur MCP `https://mcp.forecast.it/mcp`, OAuth), API Anthropic (classification, clé en secret).
- **Created:** 2026-10-14
