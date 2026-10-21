# Installation de Time Sheet Helper

Deux formes d'installation, toutes deux **par utilisateur, sans droits administrateur**, avec le runtime
.NET inclus :

- **MSI** (`artifacts/TimeSheetHelper-<version>.msi`) : recommandé. Un double-clic, ou `msiexec`
  en silence.
- **Archive + scripts PowerShell** (`artifacts/TimeSheetHelper-<version>.zip`) : décrite plus bas, conservée
  pour la transition (elle propose en plus les secrets et la configuration à l'installation, que le MSI
  laisse à la page **Sources** de l'application).

## Ce que le MSI installe

Dans `%LOCALAPPDATA%\Programs\TimeSheetHelper` :

| Élément               | Rôle                                                                                                                                                                                                                                                                                                                                                                |
| --------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `TimeSheetHelper.exe` | l'application d'arrière-plan : icône de notification, serveur web local (`http://localhost:5178`), sources. Elle démarre avec la session Windows (clé `HKCU\…\Run`). Le raccourci du menu Démarrer la lance avec `--open` : elle démarre si besoin et ouvre l'interface dans le **navigateur par défaut** (Edge, en général). L'icône de notification fait de même. |
| `plugins\<paquet>\`   | les sources fournies (`windows-activity`, `devops`, `microsoft-365`), chargées au démarrage. Elles se désactivent depuis la page **Sources**.                                                                                                                                                                                                                       |
| `vscode\*.vsix`       | l'extension VS Code (si construite), installée avec `code --install-extension` quand VS Code est présent.                                                                                                                                                                                                                                                           |

L'interface est servie en HTTP sur l'adresse `Urls` de `appsettings.json` (`appsettings.Local.json` l'emporte) et
ouverte dans le navigateur. L'application reste dans la session de l'utilisateur : la
surveillance de la fenêtre active, Microsoft 365 (connexion unique Windows) et le Gestionnaire
d'identification Windows en dépendent, ce qui exclut un service Windows en session 0.

Le MSI n'installe jamais `appsettings.Local.json`, les données (`%LOCALAPPDATA%\TimeSheetHelper`) ni les
secrets : une mise à jour ou une désinstallation n'y touche pas. L'application en cours est
fermée pendant une mise à jour. Les plugins installés par l'utilisateur (page **Sources** ou
`TimeSheetHelper plugins install`) vivent dans `%LOCALAPPDATA%\TimeSheetHelper\plugins` et survivent aussi.

Prérequis sur le poste de l'utilisateur : Windows 10/11 x64 et un navigateur (Edge est inclus dans Windows).

### Construire le MSI

Prérequis : SDK .NET 10, Git ; Node.js et pnpm seulement pour inclure l'extension VS Code. WiX est restauré par
NuGet (`WixToolset.Sdk`, aucun outil à installer).

```powershell
pwsh ./installer/build-msi.ps1                 # version déduite de git describe
pwsh ./installer/build-msi.ps1 -Version 1.2.0  # version explicite
pwsh ./installer/build-msi.ps1 -SkipExtension  # sans l'extension VS Code
```

Le script publie `src/TimeSheetHelper.App` en mode autonome `win-x64` (la publication place aussi la fenêtre
et les plugins dans le dossier), construit l'extension, retire les réglages locaux de la machine de build,
puis construit `installer/TimeSheetHelper.Installer` (WiX). La version de MSI est la partie `X.Y.Z`
(majeure et mineure ≤ 255).

### Signature (avertissement « peut être dangereux »)

Un MSI non signé déclenche l'avertissement SmartScreen du navigateur et de Windows au téléchargement. Avec un
certificat de signature de code Authenticode (`.pfx`), le script signe les binaires `TimeSheetHelper*` puis le
MSI (SHA-256, horodatés) et vérifie les signatures avec `signtool` (Windows SDK) :

```powershell
$env:SIGNING_CERT_PASSWORD = '<mot de passe>'
pwsh ./installer/build-msi.ps1 -CertificatePath C:\certs\code-signing.pfx
```

Dans le workflow `release.yml`, ajoutez les secrets du dépôt `SIGNING_CERT_PFX_BASE64`
(`[Convert]::ToBase64String([IO.File]::ReadAllBytes('code-signing.pfx'))`) et `SIGNING_CERT_PASSWORD`.
Sans ces deux secrets, la release reste non signée (avec un avertissement explicite). Si un seul est
configuré, le workflow échoue. Un certificat public ou un service de signature ne garantit pas à lui seul
l'absence d'avertissement SmartScreen.

#### Signature dans GitHub Actions

Le certificat **public uniquement** est versionné dans `installer/code-signing.cer`. Le workflow vérifie que
le PFX fourni par les secrets correspond à ce certificat, puis approuve le `.cer` dans
`Cert:\CurrentUser\Root` sur le runner Windows GitHub **éphémère**. Cette approbation permet à
`signtool verify /pa` de vérifier les signatures self-signed; elle ne modifie pas les postes des utilisateurs
et ne donne aucune réputation SmartScreen publique.

Configurez les deux secrets dans **Settings > Secrets and variables > Actions** du dépôt :

- `SIGNING_CERT_PFX_BASE64` : contenu Base64 du PFX privé, jamais le `.cer`.
- `SIGNING_CERT_PASSWORD` : mot de passe du PFX.

Pour transmettre le PFX sans afficher son contenu ni le copier dans le presse-papiers :

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes(
    "$env:LOCALAPPDATA\TimeSheetHelper\signing\code-signing.pfx")) |
    gh secret set SIGNING_CERT_PFX_BASE64 --repo decarufe/time-sheet-helper
if ($LASTEXITCODE -ne 0) { throw 'Configuration du secret PFX échouée.' }
gh secret set SIGNING_CERT_PASSWORD --repo decarufe/time-sheet-helper
# Saisir le mot de passe dans l'invite masquée de gh, jamais dans une commande ou dans le chat.
```

Le `.cer` public accompagne les artefacts et les releases, avec son SHA-256 dans `SHA256SUMS.txt`.
Publiez aussi son empreinte par un canal indépendant : télécharger le certificat, le MSI et les sommes depuis
la même release ne protège pas d'une substitution de l'ensemble. Le PFX temporaire est supprimé après la
construction du MSI, même en cas d'échec. Ne versionnez et ne publiez jamais le PFX.
Lors d'une rotation, remplacez le `.cer` versionné et les deux secrets ensemble.

#### Créer un certificat self-signed local

Pour développement ou déploiement interne, générez un certificat Authenticode self-signed sans infrastructure CA :

```powershell
pwsh ./installer/new-signing-certificate.ps1
# Crée code-signing.cer et code-signing.pfx dans %LOCALAPPDATA%\TimeSheetHelper\signing
# Demande le mot de passe du PFX en SecureString interactif (jamais visible)
pwsh ./installer/new-signing-certificate.ps1 -OutputDirectory C:\certs -CertificateName my-signer
# Personnalisé
```

Le script génère :
- **code-signing.cer** : clé publique, partageable via un **canal sécurisé hors-bande** (e-mail chiffré, message direct IRC privé, appel) **distinct du téléchargement** du .pfx pour installation sur d'autres machines. L'empreinte SHA-256 doit être vérifiée par un canal indépendant (ex. appel, IRC privé).
- **code-signing.pfx** : clé privée chiffrée avec le mot de passe SecureString. À **conserver secrètement ; jamais versionnée, commitée, ni partagée avec le dépôt**.

Utilisé avec `build-msi.ps1` :

```powershell
$env:SIGNING_CERT_PASSWORD = '<mot de passe>'
pwsh ./installer/build-msi.ps1 -CertificatePath $env:LOCALAPPDATA\TimeSheetHelper\signing\code-signing.pfx
```

**Mise en garde** :
- **Scope** : Ce certificat convient aux tests, déploiements internes et environnements contrôlés uniquement. Pour les utilisateurs du grand public, un certificat CA (OV/EV) ou Azure Trusted Signing est fortement recommandé.
- **Vérification locale** : `build-msi.ps1` utilise `signtool verify /pa` pour vérifier les signatures. Un certificat self-signed n'étant pas approuvé par défaut, `signtool` échoue avec « a certificate chain processed, but terminated in a root certificate which is not trusted ». Après avoir vérifié l'empreinte SHA-256 du `.cer` par un canal indépendant, vous pouvez faire confiance à ce certificat uniquement pour votre compte Windows :

  ```powershell
  Import-Certificate -FilePath "$env:LOCALAPPDATA\TimeSheetHelper\signing\code-signing.cer" -CertStoreLocation Cert:\CurrentUser\Root
  ```

  Relancez ensuite `build-msi.ps1`. Cet import modifie la confiance de votre compte; ne le faites que pour un certificat que vous contrôlez et ne demandez pas au grand public d'installer une racine self-signed.
- **Confiance** : Installer le .cer accordera confiance à **TOUS les binaires signés avec cette clé**. N'installer que si vous contrôlez la chaîne qui a généré la clé et que vous acceptez le risque de substitution.
- **Empreinte** : Vérifiez l'empreinte SHA-256 du fichier .cer via un canal indépendant (IRC privé, appel, message direct) distinct du canal de téléchargement des fichiers. Calculez-la avec : `Get-FileHash -Path code-signing.cer -Algorithm SHA256`
### Installer, mettre à jour, désinstaller

```powershell
msiexec /i TimeSheetHelper-1.2.0.msi            # interactif
msiexec /i TimeSheetHelper-1.2.0.msi /qn         # silencieux
msiexec /i TimeSheetHelper-1.2.0.msi /qn AUTOLAUNCH=0   # sans démarrer l'application à la fin
msiexec /x TimeSheetHelper-1.2.0.msi             # désinstaller (ou Paramètres > Applications)
```

Installer une version plus récente remplace l'ancienne (mise à jour majeure) ; une version plus ancienne est
refusée. Si l'application a été installée avec les scripts, la désinstaller d'abord
(`uninstall.ps1 -KeepData`) : les deux méthodes écrivent dans le même dossier.

Après l'installation, ouvrez la page **Sources** : c'est là que se règlent Microsoft 365, Azure DevOps, les
jetons, et que l'on active, désactive, installe ou retire des plugins.

---

## Publier une version

Le workflow **Release** (`.github/workflows/release.yml`) se lance à la main : _Actions > Release > Run workflow_,
sur la branche `main`. Il exécute les tests, construit le MSI et le zip, génère les notes de version, crée le tag
`vX.Y.Z` sur le dernier commit de `main` et publie la release GitHub (MSI, zip, `SHA256SUMS.txt`).

| Entrée     | Rôle                                                                                                                                                                                                                          |
| ---------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `bump`     | `auto` (défaut) déduit l'incrément des commits conventionnels : `feat` = minor, `fix` et autres = patch, `!` ou `BREAKING CHANGE` = major (minor tant que la version est 0.x). `patch`, `minor`, `major` forcent l'incrément. |
| `version`  | Force un numéro (`1.4.0`, `2.0.0-rc.1`) au lieu de la suggestion ; doit être supérieur à la dernière version et le tag ne doit pas exister. Un suffixe `-…` publie une pré-version.                                           |
| `ai_notes` | Notes rédigées par l'IA (GitHub Models, permission `models: read`) ; en cas d'échec, notes automatiques classées par type de commit.                                                                                          |
| `dry_run`  | Construit tout et publie les fichiers en artefact du run, sans tag ni release.                                                                                                                                                |

La première version est `0.1.0`. Les scripts (`scripts/release/Get-ReleaseVersion.ps1`,
`New-ReleaseNotes.ps1`) s'exécutent aussi en local pour prévisualiser la version et les notes.

---

## Archive et scripts PowerShell

Ce dossier produit aussi une archive autonome (`artifacts/TimeSheetHelper-<version>.zip`) qui installe
l'application **par utilisateur, sans droits administrateur**, avec ses dépendances.

## Construire le paquet

Prérequis sur le poste de build : SDK .NET 10, Git ; Node.js et pnpm seulement pour inclure
l'extension VS Code.

```powershell
pwsh ./installer/build-installer.ps1                 # version déduite de git describe
pwsh ./installer/build-installer.ps1 -Version 1.2.0  # version explicite
pwsh ./installer/build-installer.ps1 -SkipExtension  # sans l'extension VS Code
```

Le script :

1. publie `src/TimeSheetHelper.App` en mode autonome `win-x64` (dossier, pas fichier unique) ;
2. construit l'extension VS Code (`pnpm install --frozen-lockfile` puis `pnpm run package`) si `extensions/vscode`
   existe et que pnpm est disponible, sinon affiche un avertissement et continue ;
3. assemble l'archive.

Version : `git describe --tags` (préfixe `v` retiré, ex. tag `v1.2.0` → `1.2.0`) ; sans tag,
`0.1.0-<commit>`. La partie `X.Y.Z` devient la version de l'assembly.

Contenu de l'archive :

| Élément                         | Rôle                                                    |
| ------------------------------- | ------------------------------------------------------- |
| `app/`                          | application publiée (runtime .NET inclus)               |
| `vscode/*.vsix`                 | extension VS Code (si construite) et `extension-id.txt` |
| `Install.cmd`                   | lanceur à double-cliquer (`-ExecutionPolicy Bypass`)    |
| `install.ps1` / `uninstall.ps1` | installation / désinstallation                          |
| `README.txt`                    | instructions pour l'utilisateur                         |
| `version.txt`                   | version du paquet                                       |

## Installer

Décompresser l'archive puis double-cliquer sur `Install.cmd` (ou lancer `install.ps1`).
L'installation, idempotente (la relancer = mise à jour) :

1. vérifie Windows 10/11 x64 ;
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
10. démarre l'application et ouvre `http://localhost:5178`.

### Options

| Option                 | Effet                                                                                                                                                                    |
| ---------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `-DryRun`              | affiche les actions sans rien modifier                                                                                                                                   |
| `-Yes`                 | mode sans surveillance : réponses par défaut (Git et gh installés s'ils manquent, VS Code non ; jeton GitHub repris de `gh auth token` ; pas de saisie de configuration) |
| `-SkipSecrets`         | ne configure pas les secrets                                                                                                                                             |
| `-SkipConfig`          | ne crée/modifie pas `appsettings.Local.json`                                                                                                                             |
| `-SkipPrerequisites`   | ne propose pas l'installation des outils manquants                                                                                                                       |
| `-SkipExtension`       | n'installe pas l'extension VS Code                                                                                                                                       |
| `-NoStartup`           | pas de lancement à l'ouverture de session (supprime le raccourci existant)                                                                                               |
| `-NoLaunch`            | ne démarre pas l'application à la fin                                                                                                                                    |
| `-InstallDir <chemin>` | autre dossier d'installation                                                                                                                                             |

Exemple de vérification : `.\install.ps1 -DryRun -Yes -SkipSecrets`.

### Secrets

Identifiants **génériques** (`CRED_TYPE_GENERIC`, persistance « ordinateur local », utilisateur =
utilisateur Windows courant), écrits par `CredWrite` exactement comme le fait
`CredentialManagerSecretStore` dans l'application. `cmdkey` n'est pas utilisé : il exposerait le
secret sur la ligne de commande.

| Cible                         | Contenu           | Source proposée                       |
| ----------------------------- | ----------------- | ------------------------------------- |
| `TimeSheetHelper:GitHub`      | jeton GitHub      | `gh auth token`, sinon saisie masquée |
| `TimeSheetHelper:AzureDevOps` | PAT Azure DevOps  | saisie masquée                        |
| `TimeSheetHelper:Anthropic`   | clé API Anthropic | saisie masquée                        |

Un secret déjà présent n'est remplacé qu'après confirmation.

### Configuration locale

`appsettings.Local.json` (dans le dossier d'installation, conservé lors des mises à jour) :

```json
{
  "Microsoft365": { "TenantId": "…", "ClientId": "…" },
  "AzureDevOps": { "Organizations": ["mon-organisation"] }
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
