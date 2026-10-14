📌 Imported from squad-export on 2026-10-14T12:28:15.554Z. Portable knowledge carried over; project learnings from previous project preserved below.

# Project Context

- **Owner:** Eric De Carufel
- **Project:** gl-librairie-martin-slim — système de gestion Librairie Martin (SLIM): POS, réception, RFID, synchros comptables (Quickbooks / Sage / Acomba).
- **Stack:** Backend .NET/C# (clean architecture: Api / Application / Domain / Infrastructure + xUnit). Frontend React + TypeScript + Vite, i18n fr/en. Docker Compose. Azure.
- **Created:** 2026-09-01

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->
- 2026-09-29 : Rejoins l'équipe comme spécialiste documentation technique. J'utilise le skill `documentation-nexus` (plugin `core` du nexus-plugin-marketplace) comme unique source d'autorité pour la structure, les templates et les conventions de nommage. La documentation du projet vit sous `/docs` (profil GitHub repo docs, pas Azure DevOps Wiki).
- 📌 Team update (2026-09-29T11:55:44-04:00): Premier passage `migrate` complété — `docs/architecture/*.md` (11 fichiers) restructuré vers les 7 pages requises + `Decisions-Architecturales/`, `Domaine/`, `Guides-Et-References/`. `docs/PRD.md` déplacé vers `cadrage/PRD/PRD.md`. Un passage `fill` reste en attente du go-ahead de l'utilisateur (TODOs Setup-Dev.md/Branches-Et-PR.md, table des parties prenantes).

- 2026-10-14 : Équipe importée vers `time-sheet-helper` (la section Project Context ci-dessus date de Librairie Martin, obsolète). Audit doc : pas de README racine, `/docs` ne contient que `microsoft365-setup.md`, aucune structure Nexus ; `installer/README.md` dit encore que la publication « place aussi la fenêtre et les plugins » (projet Desktop supprimé, UI = Blazor dans le navigateur) ; `installer/README.txt` et `extensions/vscode/README.md` sont à jour. Charter réécrit pour ce projet. Aucune doc projet créée (en attente du go d'Éric).
