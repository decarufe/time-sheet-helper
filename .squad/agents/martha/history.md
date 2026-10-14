📌 Imported from squad-export on 2026-10-14T12:28:15.554Z. Portable knowledge carried over; project learnings from previous project preserved below.

# Project Context

- **Owner:** Eric De Carufel
- **Project:** gl-librairie-martin-slim — système de gestion Librairie Martin (SLIM): POS, réception, RFID, synchros comptables (Quickbooks / Sage / Acomba).
- **Stack:** Backend .NET/C# (clean architecture: Api / Application / Domain / Infrastructure + xUnit). Frontend React + TypeScript + Vite, i18n fr/en. Docker Compose. Azure.
- **Created:** 2026-09-01

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

- 2026-09-01 : Équipe montée. Je suis la designer UI/UX. Un dossier `Prototype/` et des personas SLIM enrichis existent dans `Documents/`. (Projet précédent, Librairie Martin — obsolète ici.)
- 2026-10-14 : Projet réel = `time-sheet-helper` (Windows .NET 10, Blazor Interactive Server, UI en français). Langage visuel : style sobre type GitHub, jetons CSS dans `wwwroot/app.css` (`--bg/--fg/--muted/--border/--accent/--row-alt/--idle/--locked`), thème clair/sombre automatique, CSS isolé `.razor.css` par composant, sans lib tierce. Charter adapté en conséquence.
- 2026-10-14 : Page Soumettre — conception « résoudre les écarts sur place » (proposition dans `decisions/inbox/martha-ecarts-soumission-ux.md`). Leçons : (1) le brouillon est **dérivé** des créneaux (`SaveDraftAsync` recrée toutes les entrées à chaque recalcul/ouverture) : toute correction d'heures doit passer par des overrides de créneaux ou un nouvel « ajustement de cellule » persisté ; (2) côté Forecast on ne peut que **lire et ajouter** (`get_time_analysis`, `plan_time_registration_create`), et les entrées sont rapprochées **par projet** (`HoursOf`) : un écart peut être un faux positif ; (3) Soumettre n'affiche ni objectif, ni non classé, ni « À confirmer » — ces écarts n'existent qu'en Semaine ; (4) à unifier : `--ok/--warn/--danger` dupliqués dans 3 feuilles, `UiLabels.Hours` vs `TimesheetUi.Hours`, modales maison vs `ConfirmDialog`. Un seul gabarit `GapCard` + panneaux par type, gravité Bloquant / À vérifier / Info, aperçu avant → après, undo pour le réversible, `ConfirmDialog` pour Supprimer/fusion.
