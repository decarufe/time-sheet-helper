# Time Sheet Helper

Aide à la saisie de la feuille de temps : l'application rassemble votre activité (fenêtres Windows, Azure DevOps, Microsoft 365, extension VS Code), la classe par tâche selon des règles, produit un brouillon de la semaine, puis l'envoie dans Forecast jour par jour. Application Blazor Server locale : http://localhost:5178.

## Installation

- Installateur MSI par utilisateur (sans droits administrateur) : [installer\README.md](installer/README.md).
- Source Microsoft 365 (courriels, calendrier, Teams) : [docs\microsoft365-setup.md](docs/microsoft365-setup.md).

## Développement

```
dotnet build
dotnet test tests\TimeSheetHelper.Tests
pwsh installer\build-msi.ps1
```

Projets : `src\TimeSheetHelper.{App,Core,Engine,Storage,Forecast,Plugins.Abstractions}`, collecteurs `Collectors.{Windows,DevOps,Microsoft365}`, extension VS Code dans `extensions\vscode`.

## Navigation

Un rail à gauche regroupe les pages en quatre sections. Quand on revient à une section, on retrouve la dernière page visitée.

| Section | Page | Raccourci |
|---|---|---|
| Temps | Semaine | Alt+1 |
| Temps | Jour | Alt+2 |
| Envoi | Soumettre | Alt+3 |
| Configuration | Règles | Alt+4 |
| Configuration | Tâches | Alt+5 |
| Configuration | Sources | Alt+6 |
| Avancé | Activité brute | Alt+7 |

- **En-tête de semaine** (sections Temps et Envoi) : semaine précédente (`[`), sélecteur de semaine, semaine suivante (`]`), « Aujourd'hui », et le parcours de la semaine : Saisir → Vérifier → Soumettre, avec ce qui reste à faire (heures à classer, écarts) et la progression vers l'objectif.
- **Panneau de section** (Ctrl+B pour l'afficher ou le masquer) : liens de la section, jours lundi à vendredi avec leur état et leurs heures sur l'objectif quotidien (8 h par défaut, 40 h par semaine), et la liste « Avant d'envoyer » dans la section Envoi.
- **Thème** clair ou sombre : bouton en bas du rail ou Alt+T. Sans préférence enregistrée, le thème du système est suivi.
- **Œil** (en bas du rail) : visible quand le suivi d'activité est disponible ; suspend ou reprend la surveillance.
- **Petits écrans** (860 px et moins) : le rail laisse place à une barre des quatre sections en bas, et le panneau devient un tiroir ouvert par « Menu » (Échap le ferme). Un lien « Aller au contenu » est disponible au clavier.
- `[` `]` et Ctrl+B sont ignorés dans les champs de saisie ; les raccourcis Alt fonctionnent partout. Sans JavaScript chargé, l'application reste utilisable mais sans raccourcis.

## Soumission vers Forecast

Voir [docs\submission-by-day.md](docs/submission-by-day.md) : états d'un jour, résolution des conflits, garanties contre les doublons et limites (modification, suppression, approbation).
