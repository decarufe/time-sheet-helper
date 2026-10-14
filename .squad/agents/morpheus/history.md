📌 Imported from squad-export on 2026-10-14T12:28:15.554Z. Portable knowledge carried over; project learnings from previous project preserved below.

# Project Context

- **Owner:** Eric De Carufel
- **Project:** gl-librairie-martin-slim — système de gestion Librairie Martin (SLIM): POS, réception, RFID, synchros comptables (Quickbooks / Sage / Acomba).
- **Stack:** Backend .NET/C# (clean architecture: Api / Application / Domain / Infrastructure + xUnit). Frontend React + TypeScript + Vite, i18n fr/en. Docker Compose. Azure.
- **Created:** 2026-09-01

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

- 2026-09-01 : Équipe montée. Je suis le Product Owner. Le dossier `Documents/` contient déjà du cadrage SLIM (Epics, personas, backlog) à exploiter.
- 📌 Team update (2026-09-29T11:55:44-04:00): `/docs` a été restructuré vers le standard documentation-nexus par Jimmy Neutron ; ma perspective produit a été incorporée pour le scope de `Contexte-Projet.md` (portée métier, exclusion de `/cadrage`).
- 2026-10-14 : Le `.squad/` importé venait de gl-librairie-martin-slim (POS/RFID/Quickbooks) — références obsolètes. Le projet réel est time-sheet-helper : app Windows locale .NET 10 qui collecte l'activité du consultant (Windows, VS Code/Claude Code, Git/GitHub/Azure DevOps, Microsoft 365) et propose une feuille de temps hebdomadaire écrite dans Forecast après validation. Utilisateur = le consultant (Nexus). Valeur = justesse de la proposition + moins de corrections ; contraintes produit non négociables : local, métadonnées seulement, secrets hors config, validation humaine avant écriture. Charter réécrit en conséquence. (Les sections « Project Context » en tête de ce fichier restent à l'ancien projet.)
