📌 Imported from squad-export on 2026-10-14T12:28:15.554Z. Portable knowledge carried over; project learnings from previous project preserved below.

# Project Context

- **Owner:** Eric De Carufel
- **Project:** gl-librairie-martin-slim — système de gestion Librairie Martin (SLIM): POS, réception, RFID, synchros comptables (Quickbooks / Sage / Acomba).
- **Stack:** Backend .NET/C# (clean architecture: Api / Application / Domain / Infrastructure + xUnit). Frontend React + TypeScript + Vite, i18n fr/en. Docker Compose. Azure.
- **Created:** 2026-09-01

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

- 2026-09-01 : Équipe montée. Je suis l'architecte, garant de la cohérence clean architecture backend et de l'intégration front/back.
- 📌 Team update (2026-09-29T11:55:44-04:00): `/docs` a été restructuré vers le standard documentation-nexus par Jimmy Neutron ; ma perspective d'architecte a été incorporée pour les décisions de placement du contenu (extraction des ADR dans `Decisions-Architecturales/`, structure des modules `Domaine/SLIM/`).
- 2026-10-14 : Le contexte projet ci-dessus (SLIM, React, Quickbooks) est périmé : le projet réel est **time-sheet-helper** (aide à la saisie de feuilles de temps, .NET 10). Contexte à jour dans `.squad/team.md`.
- 2026-10-14 : Architecture : `Core` (sans dépendance) ← `Plugins.Abstractions` / `Storage` (EF Core SQLite, UTC) ← `Engine` (règles, créneaux 15 min, classification Claude) / `Forecast` (client MCP + OAuth) ← `App` (hôte Blazor WinExe + tray + CLI + `PluginHost` + `SyncScheduler` + `/ingest`). Collecteurs (Windows, DevOps, Microsoft365) = plugins chargés à l'exécution (`plugin.json`, `AssemblyLoadContext`), jamais référencés par l'`App` (`ReferenceOutputAssembly=false`, cible `StageBundledPlugins`).
- 2026-10-14 : Flux : sources pull/continuous/push → `IActivitySink` → SQLite → Engine (règles, créneaux, classification) → proposition de semaine → corrections (apprentissage de règles) → brouillon → Forecast. Dépendances externes : Graph/Entra, Azure DevOps, GitHub, Forecast MCP, API Anthropic.
- 2026-10-14 : Points d'attention : isolation des plugins = chargement seulement (accès complet à `ISecretStore`), `GitRemote.cs` dupliqué (Windows/DevOps), recherche de `.mcp.json` dans le répertoire courant et ses parents, fuseaux/DST pour les créneaux, `installer/README.md` parle encore de « la fenêtre » (projet Desktop supprimé). Détail : `.squad/decisions/inbox/yoda-architecture-baseline-time-sheet-helper.md`.
