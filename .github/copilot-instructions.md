# Copilot Coding Agent — Squad Instructions

You are working on a project that uses **Squad**, an AI team framework. When picking up issues autonomously, follow these guidelines.

## Coordinator Canary Check

**IMPORTANT — run this check on EVERY session start, before doing any work:**

Inspect your `agent_instructions` for the coordinator canaries. This is a **two-state payload-integrity check within a known Squad coordinator session**; it verifies whether the coordinator payload loaded fully or was visibly truncated. It is **not** proof that an agent with no canaries is safely non-Squad — wholly absent payloads and proven non-Squad agents require a host-level identity beacon before custom-agent instruction ingestion.

1. If the HEAD canary `SQUAD_COORDINATOR_CANARY_HEAD_b7d2` is **absent**, the Squad coordinator payload is not observable from inside this prompt. Treat non-Squad discrimination as an **OPEN host-level concern**; do not claim this check proved the current agent is non-Squad.
2. If the HEAD canary `SQUAD_COORDINATOR_CANARY_HEAD_b7d2` is **present** and the EOF canary `SQUAD_COORDINATOR_CANARY_a8f3` is **present**, the coordinator prompt loaded fully. Proceed with normal Squad behavior.
3. If the HEAD canary `SQUAD_COORDINATOR_CANARY_HEAD_b7d2` is **present** but the EOF canary `SQUAD_COORDINATOR_CANARY_a8f3` is **absent**, the coordinator prompt loaded but was **truncated** before the end.

**When the coordinator prompt is truncated, you MUST:**
1. **STOP** — do not proceed with standard Squad behavior.
2. **WARN the user** with this exact message:
   ```
   ⚠️ Squad coordinator (squad.agent.md) loaded but appears to be truncated before the EOF canary. Do NOT proceed with standard Squad behavior — Squad's safety rails are incomplete. Please restart your session.
   ```
3. Do not continue with normal Squad routing, spawning, PR, or branch-protection behavior after emitting the warning.

## Team Context

Before starting work on any issue:

1. Read `.squad/team.md` for the team roster, member roles, and your capability profile.
2. Read `.squad/routing.md` for work routing rules.
3. If the issue has a `squad:{member}` label, read that member's charter at `.squad/agents/{member}/charter.md` to understand their domain expertise and coding style — work in their voice.

## Capability Self-Check

Before starting work, check your capability profile in `.squad/team.md` under the **Coding Agent → Capabilities** section.

- **🟢 Good fit** — proceed autonomously.
- **🟡 Needs review** — proceed, but note in the PR description that a squad member should review.
- **🔴 Not suitable** — do NOT start work. Instead, comment on the issue:
  ```
  🤖 This issue doesn't match my capability profile (reason: {why}). Suggesting reassignment to a squad member.
  ```

## Branch Naming

Use the squad branch convention:
```
squad/{issue-number}-{kebab-case-slug}
```
Example: `squad/42-fix-login-validation`

## PR Guidelines

When opening a PR:
- Reference the issue: `Closes #{issue-number}`
- If the issue had a `squad:{member}` label, mention the member: `Working as {member} ({role})`
- If this is a 🟡 needs-review task, add to the PR description: `⚠️ This task was flagged as "needs review" — please have a squad member review before merging.`
- Follow any project conventions in `.squad/decisions.md`

## Decisions

If you make a decision that affects other team members, write it to:
```
.squad/decisions/inbox/copilot-{brief-slug}.md
```
The Scribe will merge it into the shared decisions file.

---

# Time Sheet Helper — project guide

A local Windows Blazor Server app (http://localhost:5178, tray icon, single instance) that collects activity (Windows foreground windows, Azure DevOps, Microsoft 365, a VS Code extension), classifies it into Forecast tasks, builds a weekly draft and submits it to Forecast **day by day**. .NET 10, EF Core + SQLite, xUnit.

## Build, test, run

```powershell
dotnet build
dotnet test tests\TimeSheetHelper.Tests
# single test / class (xUnit filter)
dotnet test tests\TimeSheetHelper.Tests --filter "FullyQualifiedName~WeekServiceTests.Non_contiguous_slots"
dotnet test tests\TimeSheetHelper.Tests --filter "FullyQualifiedName~PredictionFeedbackTests"
dotnet run --project src\TimeSheetHelper.App
pwsh installer\build-msi.ps1           # MSI (CI: installer\build-msi.ps1 -Version x.y.z)
```

- When the app is running it locks `bin\`; build/test into a throwaway folder instead: `dotnet test tests\TimeSheetHelper.Tests --artifacts-path "$PWD\.tsh-art"`, then delete `.tsh-art` (never commit it).
- `TIMESHEETHELPER_DATA_DIR` relocates all local data (default `%LOCALAPPDATA%\TimeSheetHelper\data.db`); use it for throwaway runs.
- EF migrations (local tool in `dotnet-tools.json`): `dotnet tool restore`, then `dotnet ef migrations add <Name> --project src\TimeSheetHelper.Storage --startup-project src\TimeSheetHelper.Storage`. Migrations are applied at startup (`AppServices.MigrateDatabaseAsync`).
- VS Code extension (`extensions\vscode`, pnpm): `pnpm run compile`, `pnpm test` (vitest), `pnpm run package`.
- CLI mode of the same exe (`Cli.cs`): `forecast login|logout|tools|call <tool> [json]|sync-tasks|import-tasks|export-tasks`, `sync [--week yyyy-MM-dd] [--source x]`, `plugins ...`.
- Release: manual `release.yml` workflow on `main`; the version is derived from **Conventional Commits** (`feat(scope): …`, `fix(…)`, `docs`, `test`, `chore`), so keep that commit style.

## Architecture (data flow)

1. **Collection** — sources are plugins (`Plugins.Abstractions`: `ISourcePlugin` → `ISourceInstance`, writing to `IActivitySink`). `App\Plugins\PluginHost` loads them in isolated `PluginLoadContext`s from `<exe>\plugins` and `<data>\plugins` using each project's `plugin.json`. The `Collectors.*` projects are *not* referenced by the App; they are published as `BundledPlugin` items into `bin\...\plugins\<dir>` by an MSBuild target. Push sources (VS Code extension) post to `/ingest/{route}` with a token (`App\Ingest\IngestEndpoints.cs`). `SyncScheduler` polls pull sources.
2. **Storage** — `Storage\TimeSheetDbContext` (SQLite) holds activities, rules, Forecast tasks, `SlotOverrides` (manual assignments, `ForecastTaskId == null` = "Pas du travail"), `RemovedSlots` (tombstones: deleted slots stay ignored even if a sync re-ingests them), `AssignmentFeedback`, drafts and the submission journal. Always use `IDbContextFactory<TimeSheetDbContext>` with short-lived contexts (services are singletons).
3. **Engine** (`Engine`) — `ActivityResolver` turns activities into windows with a candidate (rule task via `RuleEvaluator`, otherwise an unclassified group from `GroupKeyBuilder`) and a weight. `PredictionFeedback` splits that weight using learned confirmations (rule prior 2, group prior 1.5). `SlotAllocator` cuts each **local** day into 15-minute slots (`EngineOptions.SlotLength`) and picks the heaviest candidate; user overrides always win. `WeekProposalBuilder` builds week/day views and writes the draft (a submitted draft is never modified). `WeekService` is the UI façade. `ClaudeClassifier` (Anthropic SDK) suggests tasks for unclassified groups; `CorrectionLearner` derives rules from corrections.
4. **Forecast** — accessed through the Forecast **MCP server** (`ForecastMcpConnector`/`ForecastToolResolver`, `ModelContextProtocol.Core`), not a REST client. `Forecast\Submission` (`DayPlanner`, `SubmissionGapService`) plus `App\Services\DaySubmissionService` submit one day at a time; see `docs\submission-by-day.md` for day states, conflicts and duplicate protection.
5. **UI** — `App\Components` (Blazor interactive server). Pages talk to `WeekService`/services, never to the DbContext directly. Pure UI logic lives in testable static classes in `Components\Shared` (`TimesheetUi`, `TaskPickerView`, `GapLabels`, `DayStateLabels`, `UiLabels`).

## Conventions

- **Language**: user-facing text, docs and release notes are **French** (e.g. "Effacer", "Pas du travail", "Ignoré"); code, identifiers and XML comments are English. Keep labels in the label/helper classes, not scattered literals.
- **Time**: everything is stored in UTC (`*Utc` properties, `DateTime.SpecifyKind(..., Utc)`); local days are computed through `EngineOptions.TimeZoneId` / `SlotAllocator.DayBounds`/`LocalDate` (DST-aware). Inject `TimeProvider`, never use `DateTime.Now`.
- Slot keys are slot-start UTC instants; ranges load `WeekProposalBuilder.LookBehind/LookAhead` around them because activity windows reach across slots.
- The built-in "Ignoré" task (`ForecastTask.IgnoredExternalId`, ensured by `IgnoredTask.EnsureAsync`) is special: excluded from learning and treated as not submitted.
- Files use **CRLF** line endings; re-normalize files written by scripts.
- Services are registered via `Add*` extension methods per project (`AddTimesheetEngine`, `AddForecast`, `AddTimeSheetStorage`, `AppServices.AddTimeSheetHelper`).

## Tests

- One xUnit project (`tests\TimeSheetHelper.Tests`, `net10.0-windows`) referencing all projects; folders mirror areas (`Engine`, `App`, `Storage`, `DevOps`, `Microsoft365`).
- Engine/DB tests use `EngineTestHost` (in-memory SQLite, `FakeClassifier`, fixed clock) and the `Fx` helpers: Toronto time, week of Monday 2026-10-05 (`Fx.Mon(9, 15)`, `Fx.Act(...)`, `host.AddTaskAsync/AddRuleAsync/AddActivitiesAsync`). Pure logic (allocator, UI helpers) is tested without a host.
- Test names are sentence-style with underscores describing behaviour (`Not_work_keeps_the_collected_activity_and_...`).