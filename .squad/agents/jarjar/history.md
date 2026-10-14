📌 Imported from squad-export on 2026-10-14T12:28:15.554Z. Portable knowledge carried over; project learnings from previous project preserved below.

# Project Context

- **Owner:** Eric De Carufel
- **Project:** gl-librairie-martin-slim — système de gestion Librairie Martin (SLIM): POS, réception, RFID, synchros comptables (Quickbooks / Sage / Acomba).
- **Stack:** Backend .NET/C# (clean architecture: Api / Application / Domain / Infrastructure + xUnit). Frontend React + TypeScript + Vite, i18n fr/en. Docker Compose. Azure.
- **Created:** 2026-09-01

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->
- 2026-09-01 : Équipe montée. Je suis la voix utilisateur (persona). Des personas SLIM enrichis existent dans `Documents/Personas-SLIM-enrichis-2026-08-24.md`.
- 2026-10-14 : Contexte réel = time-sheet-helper (et non SLIM/Librairie Martin). Je suis un consultant qui remplit sa feuille de temps Forecast chaque semaine. Parcours : tray/rappel -> Semaine -> Journée/Règles -> Soumettre (simulation, saisie, vérification) ; l'approbation dans Forecast reste manuelle. Les irritants clés : exactitude de la proposition, transparence des règles, erreurs/jetons expirés, UI en français.
