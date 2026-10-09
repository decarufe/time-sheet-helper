# Paquet d'installation de Time Sheet Helper

Ce dossier produit une archive autonome (`artifacts/TimeSheetHelper-<version>.zip`) qui installe
l'application **par utilisateur, sans droits administrateur**, avec ses dépendances.

## Construire le paquet

Prérequis sur le poste de build : SDK .NET 10, Git ; Node.js (npm) seulement pour inclure
l'extension VS Code.

```powershell
pwsh ./installer/build-installer.ps1                 # version déduite de git describe
pwsh ./installer/build-installer.ps1 -Version 1.2.0  # version explicite
pwsh ./installer/build-installer.ps1 -SkipExtension  # sans l'extension VS Code
```

Le script :

1. publie `src/TimeSheetHelper.App` en mode autonome `win-x64` (dossier, pas fichier unique :
   Playwright a besoin de son dossier `.playwright`, pas de trimming) ;
2. construit l'extension VS Code (`npm ci` puis `npm run package`) si `extensions/vscode`
   existe et que npm est disponible, sinon affiche un avertissement et continue ;
3. assemble l'archive.

Version : `git describe --tags` (préfixe `v` retiré, ex. tag `v1.2.0` → `1.2.0`) ; sans tag,
`0.1.0-<commit>`. La partie `X.Y.Z` devient la version de l'assembly.

Contenu de l'archive :

| Élément | Rôle |
| --- | --- |
| `app/` | application publiée (runtime .NET inclus) |
| `vscode/*.vsix` | extension VS Code (si construite) et `extension-id.txt` |
| `Install.cmd` | lanceur à double-cliquer (`-ExecutionPolicy Bypass`) |
| `install.ps1` / `uninstall.ps1` | installation / désinstallation |
| `README.txt` | instructions pour l'utilisateur |
| `version.txt` | version du paquet |

## Installer

Décompresser l'archive puis double-cliquer sur `Install.cmd` (ou lancer `install.ps1`).
L'installation, idempotente (la relancer = mise à jour) :

1. vérifie Windows 10/11 x64 et la présence de **Microsoft Edge** (canal `msedge` de Playwright) ;
2. propose, via `winget` et **toujours après confirmation**, d'installer Git, GitHub CLI (`gh`) et,
   si l'extension est fournie, VS Code (installation utilisateur) ;
3. arrête l'instance en cours de `TimeSheetHelper.exe` ;
4. copie `app/` dans `%LOCALAPPDATA%\Programs\TimeSheetHelper` en conservant
   `appsettings.Local.json` ; les données de `%LOCALAPPDATA%\TimeSheetHelper` ne sont jamais touchées ;
5. installe le `.vsix` dans VS Code et VS Code Insiders s'ils sont présents ;
6. crée les raccourcis menu Démarrer et Démarrage (lancement à l'ouverture de session) ;
7. enregistre facultativement les secrets dans le Gestionnaire d'identification Windows ;
8. demande facultativement TenantId / ClientId Microsoft 365 et les organisations Azure DevOps et
   les écrit dans `appsettings.Local.json` ;
9. inscrit l'application dans **Applications installées** (clé
   `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\TimeSheetHelper`) ;
10. démarre l'application et ouvre http://localhost:5178.

### Options

| Option | Effet |
| --- | --- |
| `-DryRun` | affiche les actions sans rien modifier |
| `-Yes` | mode sans surveillance : réponses par défaut (Git et gh installés s'ils manquent, VS Code non ; jeton GitHub repris de `gh auth token` ; pas de saisie de configuration) |
| `-SkipSecrets` | ne configure pas les secrets |
| `-SkipConfig` | ne crée/modifie pas `appsettings.Local.json` |
| `-SkipPrerequisites` | ne propose pas l'installation des outils manquants |
| `-SkipExtension` | n'installe pas l'extension VS Code |
| `-NoStartup` | pas de lancement à l'ouverture de session (supprime le raccourci existant) |
| `-NoLaunch` | ne démarre pas l'application à la fin |
| `-InstallDir <chemin>` | autre dossier d'installation |

Exemple de vérification : `.\install.ps1 -DryRun -Yes -SkipSecrets`.

### Secrets

Identifiants **génériques** (`CRED_TYPE_GENERIC`, persistance « ordinateur local », utilisateur =
utilisateur Windows courant), écrits par `CredWrite` exactement comme le fait
`CredentialManagerSecretStore` dans l'application. `cmdkey` n'est pas utilisé : il exposerait le
secret sur la ligne de commande.

| Cible | Contenu | Source proposée |
| --- | --- | --- |
| `TimeSheetHelper:GitHub` | jeton GitHub | `gh auth token`, sinon saisie masquée |
| `TimeSheetHelper:AzureDevOps` | PAT Azure DevOps | saisie masquée |
| `TimeSheetHelper:Anthropic` | clé API Anthropic | saisie masquée |

Un secret déjà présent n'est remplacé qu'après confirmation.

### Configuration locale

`appsettings.Local.json` (dans le dossier d'installation, conservé lors des mises à jour) :

```json
{
  "Microsoft365": { "TenantId": "…", "ClientId": "…" },
  "AzureDevOps": { "Organizations": [ "mon-organisation" ] }
}
```

## Désinstaller

Paramètres › Applications › Applications installées › Time Sheet Helper, ou :

```powershell
powershell -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\Programs\TimeSheetHelper\uninstall.ps1"
```

Arrête l'application, retire l'extension VS Code, les raccourcis, l'entrée de désinstallation et le
dossier d'installation. Les données (`%LOCALAPPDATA%\TimeSheetHelper`) et les secrets ne sont
supprimés qu'après confirmation : `-KeepData` les conserve, `-RemoveData` les supprime sans
question. `-DryRun` et `-Yes` sont aussi disponibles.
