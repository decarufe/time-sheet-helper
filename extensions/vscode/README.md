# Time Sheet Helper pour VS Code

Envoie l'activité de VS Code à l'application Time Sheet Helper locale (`http://localhost:5178/ingest/vscode`)
pour proposer la feuille de temps.

## Ce qui est envoyé

Tant qu'une fenêtre VS Code a le focus, un battement est envoyé toutes les 30 s, et immédiatement lors d'un
changement (fichier, onglet, terminal, débogage, focus) :

| Champ | Contenu |
| --- | --- |
| `sessionId` | identifiant de la fenêtre VS Code |
| `timestampUtc` | heure ISO 8601 UTC |
| `windowFocused` | `false` une seule fois quand la fenêtre perd le focus |
| `workspaceName`, `workspaceFolders` | espace de travail ouvert |
| `activeFile`, `languageId` | fichier actif (chemin local uniquement) |
| `gitRemote`, `gitBranch` | via l'extension Git intégrée (`vscode.git`) |
| `editsSinceLast` | nombre de modifications de documents depuis le dernier battement |
| `debugging` | une session de débogage est active |
| `aiActivity` | voir l'heuristique ci-dessous |

Aucun contenu de fichier n'est envoyé.

## Heuristique « activité IA »

VS Code n'expose aucune API indiquant qu'un assistant IA travaille. `aiActivity` vaut `true` quand :

- le terminal actif a un nom contenant « claude » (Claude Code lancé dans le terminal intégré) ;
- ou l'onglet actif est une vue de clavardage : un onglet webview / éditeur personnalisé dont le type de vue
  contient « claude », « copilot » ou « chat » (panneau Claude Code, Copilot Chat ouvert dans l'éditeur), ou un
  onglet d'un type inconnu dont le libellé contient ces mots. Les onglets de fichiers ne sont jamais considérés
  (un fichier `chat.ts` n'est pas une conversation).

Limites : une conversation affichée dans une barre latérale n'est pas un onglet et n'est pas détectée ; un
terminal « claude » reste « actif » tant qu'aucun autre terminal n'est sélectionné, même si l'éditeur a le focus.

## Paramètres

- `timesheetHelper.enabled` : active l'envoi (par défaut `true`).
- `timesheetHelper.endpoint` : adresse du point d'ingestion.
- `timesheetHelper.token` : jeton ; vide = lecture de `%LOCALAPPDATA%\TimeSheetHelper\ingest-token`, créé par
  l'application au premier démarrage.

Si l'application n'est pas démarrée, l'extension reste silencieuse et conserve au plus 5 minutes
(20 battements) pour les envoyer plus tard.

## Développement

```
npm ci
npm run compile   # vérification de types + bundle esbuild dans dist/
npm test          # tests vitest de la logique pure
npm run package   # produit timesheet-helper.vsix
```

F5 depuis ce dossier ouvert dans VS Code lance un hôte d'extension (voir `.vscode/launch.json`).
Installation du paquet : `code --install-extension timesheet-helper.vsix`.
