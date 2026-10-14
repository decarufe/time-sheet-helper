📌 Imported from squad-export on 2026-10-14T12:28:15.554Z. Portable knowledge carried over; project learnings from previous project preserved below.

# Project Context

- **Owner:** Eric De Carufel
- **Project:** gl-librairie-martin-slim — système de gestion Librairie Martin (SLIM): POS, réception, RFID, synchros comptables (Quickbooks / Sage / Acomba).
- **Stack:** Backend .NET/C# (clean architecture: Api / Application / Domain / Infrastructure + xUnit). Frontend React + TypeScript + Vite, i18n fr/en. Docker Compose. Azure.
- **Created:** 2026-09-01

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->
- 2026-09-01 : Équipe montée. Je suis l'expert cloud. Un dossier `slim-azure-architecture/` existe à la racine du repo.
- 2026-09-30 : Audit cloud/Azure du déploiement SLIM : la souscription cible ne contient aucun groupe de ressources ou ressource nommée SLIM/Librairie; les contrôles live par environnement ne sont donc pas vérifiables tant que DEV/UAT/PROD ne sont pas déployés.
- 2026-09-30: Politique modèle — toujours spawné avec `model` Anthropic explicite (config.json modelPolicy).

- 2026-10-14 : Contexte corrigé : le projet réel est time-sheet-helper (app Windows locale .NET 10, aucun cloud déployé) ; ce qui précède sur SLIM/Azure vient d'un autre projet. Mes surfaces : Entra/Graph (MSAL public client, WAM, cache DPAPI, 5 permissions déléguées), Forecast MCP (OAuth, port 17653), Azure DevOps/GitHub, API Anthropic (Engine:Claude, modèle claude-sonnet-5-5, MaxTokens 2048). Point d'attention : .mcp.json découvert dans les dossiers parents peut rediriger l'endpoint Forecast (voir Yoda).
