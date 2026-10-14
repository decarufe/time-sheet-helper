# Work Routing

How to decide who handles what.

## Routing Table

| Work Type                                    | Route To          | Examples                                                                                                                                                                                                                                                                          |
| -------------------------------------------- | ----------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Architecture & design système                | Yoda              | Frontières entre projets (Core / Engine / Storage / Forecast / Plugins.Abstractions), contrat de plugins et `PluginApi.Version`, flux collecte → moteur → Forecast, revue d'architecture                                                                                          |
| Scope & priorités / backlog                  | Morpheus          | Quoi construire ensuite, arbitrages, user stories, vision produit (aide à la saisie des feuilles de temps)                                                                                                                                                                        |
| Frontend (Blazor / tray / extension VS Code) | Cendrillon        | Composants et pages Razor de `TimeSheetHelper.App/Components` (Week, Day, Rules, Sources, Submit, ForecastTasks), icône de notification (`TrayIcon`), rappel hebdomadaire, extension VS Code (TypeScript, pnpm, esbuild, vitest)                                                  |
| Backend (.NET/C#)                            | Glados            | `Core`, `Engine` (règles, créneaux, classification), `Storage` (EF Core SQLite, migrations), `Forecast` (client MCP), collecteurs (`Collectors.DevOps`, `Collectors.Microsoft365`, `Collectors.Windows`), hôte de plugins (`PluginHost`, `SyncScheduler`, `IngestEndpoints`), CLI |
| Design UI/UX                                 | Martha Stewart    | Parcours de revue/validation de la semaine, lisibilité des propositions d'heures, page Sources, accessibilité                                                                                                                                                                     |
| Cloud & identité                             | Zeus              | Entra ID (inscription d'application, consentement, WAM), Microsoft Graph, intégrations Azure DevOps / GitHub, API Anthropic (coûts, quotas)                                                                                                                                       |
| DevOps & CI/CD                               | R2-D2             | Installeur WiX/MSI et scripts `installer/`, `dotnet build/publish` (publication autonome win-x64, étapes MSBuild des plugins), versionnement, pipelines, releases                                                                                                                 |
| Documentation technique                      | Jimmy Neutron     | Structure et contenu de `/docs` (guides d'installation et de configuration, ADR), README de l'installeur et de l'extension, audit et migration de doc via le skill `documentation-nexus`                                                                                          |
| Sécurité / pentest                           | Mafia Boy         | Secrets (Gestionnaire d'identification, DPAPI), jeton d'ingestion local, serveur HTTP localhost, OAuth Forecast, vie privée (métadonnées seulement), chargement de plugins tiers                                                                                                  |
| Voix utilisateur                             | Jar Jar Binks     | Perspective du consultant qui remplit sa feuille de temps, feedback d'usage, validation besoins (persona)                                                                                                                                                                         |
| Code review                                  | Yoda              | Review PRs, qualité, suggestions (peut réassigner en cas de rejet)                                                                                                                                                                                                                |
| Testing                                      | Inspecteur Gadget | Tests xUnit de `tests/TimeSheetHelper.Tests` (Engine, collecteurs avec fixtures JSON, Forecast, PluginHost), `TestPlugin`, cas limites (fuseaux, créneaux de 15 min), vérifier les correctifs                                                                                     |
| Session logging                              | Scribe            | Automatique — jamais besoin de routage                                                                                                                                                                                                                                            |
| RAI review                                   | Rai               | Sécurité du contenu, biais, détection de secrets, revue éthique                                                                                                                                                                                                                   |
| Vérification / avocat du diable              | Fact Checker      | Vérifier les affirmations, pré-mortem, contre-arguments                                                                                                                                                                                                                           |

## Issue Routing

| Label          | Action                                               | Who          |
| -------------- | ---------------------------------------------------- | ------------ |
| `squad`        | Triage: analyze issue, assign `squad:{member}` label | Lead         |
| `squad:{name}` | Pick up issue and complete the work                  | Named member |

### How Issue Assignment Works

1. When a GitHub issue gets the `squad` label, the **Lead** triages it — analyzing content, assigning the right `squad:{member}` label, and commenting with triage notes.
2. When a `squad:{member}` label is applied, that member picks up the issue in their next session.
3. Members can reassign by removing their label and adding another member's label.
4. The `squad` label is the "inbox" — untriaged issues waiting for Lead review.

## Local Human Identity & Decision Gate

Squad already resolves the current user via `git config user.name` at session start.
For decisions, validate that name against the `Human Members` roster in
`.squad/team.md`. If it matches, address that human by name. If it doesn't match (git
not configured, different machine account, or the name isn't on the roster), ask which
roster member is active using the platform's question tool with an `Other` option, and
use that answer for the rest of the session.

## Rules

1. **Eager by default** — spawn all agents who could usefully start work, including anticipatory downstream work.
2. **Scribe always runs** after substantial work, always as `mode: "background"`. Never blocks.
3. **Quick facts → coordinator answers directly.** Don't spawn an agent for "what port does the server run on?"
4. **When two agents could handle it**, pick the one whose domain is the primary concern.
5. **"Team, ..." → fan-out.** Spawn all relevant agents in parallel as `mode: "background"`.
6. **Anticipate downstream work.** If a feature is being built, spawn the tester to write test cases from requirements simultaneously.
7. **Issue-labeled work** — when a `squad:{member}` label is applied to an issue, route to that member. The Lead handles all `squad` (base label) triage.
8. **Human decision gate** — human members retain final decision authority. When work is blocked on a product, architecture, scope, priority, or process decision, resolve the active human from the git-identified current user (validated against the `Human Members` roster in `.squad/team.md`, or asked directly with an `Other` option if unmatched) and address them by name with the blocker, the decision required, the recommended option, and viable alternatives. Do not delegate the final decision to an agent. After the human decides, persist the decision through Squad's configured decision-memory mechanism and resume delegation to the appropriate named agent or agents.
9. **Modèles Anthropic uniquement** — chaque spawn (`task`, y compris Scribe, Ralph, `explore`, `rubber-duck`, `code-review`, background) DOIT passer un paramètre `model` explicite choisi parmi `claude-haiku-*`, `claude-sonnet-*`, `claude-opus-*`, résolu selon la section `## Model` du charter de l'agent. Ne jamais omettre `model` (le défaut plateforme peut être GPT) et ne jamais utiliser de modèle GPT, Gemini, Grok ou autre. Si aucun modèle Anthropic n'est disponible, arrêter et avertir l'humain. Référence : `.squad/config.json` → `modelPolicy`.
