📌 Imported from squad-export on 2026-10-14T12:28:15.554Z. Portable knowledge carried over; project learnings from previous project preserved below.

# Project Context

- **Owner:** Eric De Carufel
- **Project:** gl-librairie-martin-slim — système de gestion Librairie Martin (SLIM): POS, réception, RFID, synchros comptables (Quickbooks / Sage / Acomba).
- **Stack:** Backend .NET/C# (clean architecture: Api / Application / Domain / Infrastructure + xUnit). Frontend React + TypeScript + Vite, i18n fr/en. Docker Compose. Azure.
- **Created:** 2026-09-01

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

- 2026-09-01 : Équipe montée. Je suis l'expert DevOps. `docker-compose.yml` présent dans `src/backend`; workflows GitHub Actions déjà présents (squad-\*).
- 2026-10-01 : Config frontend par environnement (ADR `2026-10-01-Configuration-Frontend-Par-Environnement`). `deploy-stage*.yml` copie `configs/config.<env>.js`, supprime `configs/` et échoue si absent ; `TENANT_ID`/`CLIENT_ID` = placeholders remplacés par `frontendTenantId`/`frontendClientId`. Les groupes `librairie-martin-slim-v2-<env>-secrets` (Library, manuels) sont liés dans le YAML (`variables: - group:`) et doivent exister, avec `sqlAdministratorLoginPassword` inclus. Piège : une variable ADO non définie est transmise comme texte littéral `$(nom)` (truthy) — toujours valider les valeurs dans le script. Voir `pipelines/variable-groups.md`.
- 2026-09-30 : Audit CI/CD SLIM v2 : Azure DevOps utilise des environnements `Development`, `UAT`, `Production`; UAT/Production ont des checks d'approbation live, mais le YAML ne chaîne pas strictement Dev → UAT → Prod et les slots App Service sont désactivés.
- 2026-09-30: Politique modèle — toujours spawné avec `model` Anthropic explicite (config.json modelPolicy).
- 2026-10-05 : Aspire 13.6 fournit `Aspire.Hosting.Docker` pour générer Compose depuis l’AppHost (`aspire publish`), pas une API documentée d’import Compose au runtime. Le Toolkit offre `CommunityToolkit.Aspire.Hosting.SqlDatabaseProjects` pour publier le DACPAC dans le cycle AppHost.
- 2026-10-05 : Le debugger VS Code Aspire (`type: aspire`) démarre l’AppHost et attache les debuggers supportés des ressources .NET et JavaScript. C’est l’unique configuration de lancement VS Code : l’API ne démarre plus SQL Server via `docker compose` et le `docker-compose.yml` est supprimé.
- 2026-10-05 : Le guide local documente Aspire comme source unique d'orchestration; l’AppHost publie le DACPAC dans la base locale via `CommunityToolkit.Aspire.Hosting.SqlDatabaseProjects` (ressource `database-schema`), le pipeline Azure le publiant pour les environnements.
- 2026-10-05 : Le mot de passe `sa` local est généré par Aspire (plus de paramètre `sa-password`); `LibrairieMartinSlim.Database` est le nom du projet/DACPAC, distinct de la base locale `LibrairieMartinSlim`.

## time-sheet-helper (projet actuel)

> Les apprentissages ci-dessus (SLIM, Docker Compose, Aspire, Azure DevOps) viennent du projet précédent et ne s'appliquent PAS à time-sheet-helper. Ceux ci-dessous sont la réalité du projet.

- 2026-10-14 : Projet = app Windows .NET 10 (`TimeSheetHelper.App`, WinExe + Blazor sur `localhost:5178`), install par utilisateur. Pas de Docker ni de cloud. Packaging : `installer/build-msi.ps1` (MSI WiX 6.0.2, recommandé) et `installer/build-installer.ps1` (zip + `install.ps1`/`uninstall.ps1`/`Install.cmd`, en transition). Le MSI va dans `%LOCALAPPDATA%\Programs\TimeSheetHelper`, autorun via `HKCU\…\Run`, raccourci menu Démarrer `--open`; MSI et scripts partagent ce dossier (ne pas mélanger).
- 2026-10-14 : Version = `git describe --tags --always --dirty` ; aucun tag git aujourd'hui → `0.1.0-<sha>[-dirty]` (ex. `0.1.0-09d3792-dirty`). Le MSI n'utilise que `X.Y.Z` ; tous les builds sans tag ont donc la même version MSI `0.1.0` (d'où `AllowSameVersionUpgrades="yes"`). Le suffixe `-dirty` finit dans `InformationalVersion` et le nom du fichier : un build « dirty » ne doit pas être distribué.
- 2026-10-14 : Hygiène git : `artifacts/` et `appsettings.Local.json` sont ignorés (bien, rien de suivi). Non ignorés/untracked : `__azurite_db_queue__.json`, `__azurite_db_queue_extent__.json`, `__queuestorage__/` (aucune référence Azurite dans src/tests/installer/docs : résidus de l'extension VS Code Azurite, à supprimer/ignorer), `squad-export.json` (~118 Ko, import d'un autre projet, à ne pas committer), `.squad-archive-*/`.
- 2026-10-14 : CI : aucun workflow de build/test/release du produit ; uniquement `squad-*` et `sync-squad-labels` (Node/`actions/github-script`, runner ubuntu). `TimeSheetHelper.Installer.wixproj` n'est pas dans le `.sln`. Le MSI n'est pas signé.
- 2026-10-14 : Règle pnpm : `build-*.ps1` utilisent bien `pnpm install --frozen-lockfile` + `pnpm run package`. Mais `.mcp.json` (entrée `squad_state`, ajoutée par Squad) et `.copilot/mcp-config.json` (exemple) utilisent `npx -y` : contraire à la préférence (`pnpm dlx`) ; à signaler.
- 2026-10-14 : Désinstallation : le MSI retire l'extension VS Code (`code --uninstall-extension`, sauf pendant une mise à jour) et laisse données/secrets/`appsettings.Local.json`. `uninstall.ps1` (zip) retire aussi raccourcis + clé `Uninstall` HKCU, et ne supprime données/secrets qu'après confirmation (`-RemoveData`). `install.ps1` crée un raccourci Démarrage alors que le MSI utilise la clé `Run` : installer les deux = double autorun.
- 2026-10-14 : Risque à vérifier : `util:CloseApplication` (`CloseMessage=no`, `TerminateProcess=0`) pour une appli de plateau sans fenêtre visible peut ne pas la fermer pendant une mise à jour (fichiers verrouillés / redémarrage). `install.ps1` fait un `Stop-Process -Force`, pas le MSI.

- 2026-10-14 : Vérification du JS isolé SuggestInput.razor.js dans le build publié : présent dans wwwroot\Components\Shared (+ .br/.gz), référencé dans TimeSheetHelper.staticwebassets.endpoints.json (content-type text/javascript), servi en HTTP 200 par l'exe publié en Production (Program.cs appelle MapStaticAssets hors condition Development). CSS isolé (bundle TimeSheetHelper.styles.css, avec ConfirmDialog/NewTaskForm/SuggestInput) aussi servi. Package.wxs inclut tout le publish par glob dynamique (Files Include=...\**, seuls exe/Local/Development/pdb exclus) : aucun fichier de wwwroot à déclarer à la main. Piège de test : un publish manuel copie appsettings.local.json (Urls=5178 + secrets), que build-msi.ps1 supprime ; pour lancer l'exe publié sur un autre port, supprimer ce fichier du dossier temporaire, sinon Urls du fichier local écrase ASPNETCORE_URLS et --urls (crash AddressInUse si l'app de l'utilisateur tourne déjà). Utiliser TIMESHEETHELPER_DATA_DIR pour isoler les données. artifacts\publish existant est périmé (sans le JS) : build-msi.ps1 le recrée entièrement. Aucun défaut.
