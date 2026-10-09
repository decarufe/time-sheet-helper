Time Sheet Helper - installation
================================

Installation (aucun droit administrateur requis)
------------------------------------------------
1. Décompressez toute l'archive dans un dossier (par exemple Téléchargements).
2. Double-cliquez sur Install.cmd.
3. Répondez aux questions :
   - installation des outils manquants via winget (Git, GitHub CLI, VS Code) ;
   - enregistrement des secrets (jeton GitHub, PAT Azure DevOps, clé API Anthropic)
     dans le Gestionnaire d'identification Windows ;
   - configuration Microsoft 365 (TenantId / ClientId) et organisations Azure DevOps.
4. L'application démarre et s'ouvre dans le navigateur : http://localhost:5178

Relancer Install.cmd avec une version plus récente met l'application à jour.
Vos données et votre fichier appsettings.Local.json sont conservés.

Emplacements
------------
Application : %LOCALAPPDATA%\Programs\TimeSheetHelper
Données     : %LOCALAPPDATA%\TimeSheetHelper (jamais modifié par l'installation)
Secrets     : Gestionnaire d'identification Windows > Informations d'identification Windows
              (TimeSheetHelper:GitHub, TimeSheetHelper:AzureDevOps, TimeSheetHelper:Anthropic)

Options (PowerShell)
--------------------
  powershell -ExecutionPolicy Bypass -File install.ps1 -DryRun        simulation, aucune modification
  powershell -ExecutionPolicy Bypass -File install.ps1 -Yes           sans questions (réponses par défaut)
  ... -SkipSecrets    ne pas configurer les secrets
  ... -SkipConfig     ne pas créer appsettings.Local.json
  ... -NoStartup      pas de démarrage automatique à l'ouverture de session
  ... -NoLaunch       ne pas démarrer l'application à la fin

Prérequis
---------
- Windows 10 ou 11, 64 bits.
- Microsoft Edge (utilisé pour la saisie automatique dans Forecast).
- Facultatif : Git, GitHub CLI (gh), VS Code (pour l'extension).
Le runtime .NET est inclus : rien d'autre à installer.

Désinstallation
---------------
Paramètres > Applications > Applications installées > Time Sheet Helper > Désinstaller,
ou : powershell -ExecutionPolicy Bypass -File "%LOCALAPPDATA%\Programs\TimeSheetHelper\uninstall.ps1"
Les données et les secrets ne sont supprimés qu'après confirmation (ou avec -RemoveData).
