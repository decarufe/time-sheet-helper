<#
.SYNOPSIS
    Installs (or upgrades) Time Sheet Helper for the current user. No administrator rights required.

.DESCRIPTION
    - Checks prerequisites (Windows 10/11 x64, Microsoft Edge) and offers optional tools through
      winget (Git, GitHub CLI, VS Code) - always after confirmation, unless -Yes is given.
    - Stops a running instance, copies app\ to %LOCALAPPDATA%\Programs\TimeSheetHelper while
      preserving appsettings.Local.json. User data in %LOCALAPPDATA%\TimeSheetHelper is never touched.
    - Installs the VS Code extension (.vsix) in VS Code and VS Code Insiders when present.
    - Creates Start Menu and Startup (logon) shortcuts.
    - Optionally stores secrets in Windows Credential Manager (generic credentials, same format
      as the application's CredentialManagerSecretStore) and writes appsettings.Local.json.
    - Registers an entry in "Apps & features" (HKCU) pointing to uninstall.ps1.
    - Starts the application and opens its web UI.
    Re-running the script upgrades an existing installation.

.PARAMETER Yes
    Unattended mode: every question gets its default answer (missing Git / GitHub CLI are installed,
    VS Code is not, the GitHub token is taken from `gh auth token`, other prompts are skipped).

.PARAMETER DryRun
    Only displays the actions that would be performed. Nothing is written, installed or stopped.

.EXAMPLE
    .\install.ps1
    .\install.ps1 -Yes -SkipSecrets
    .\install.ps1 -DryRun
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Interactive console script with colored output.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'The script implements its own -DryRun switch.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Parameters are used inside script blocks.')]
[CmdletBinding()]
param(
    [switch]$Yes,
    [switch]$DryRun,
    [switch]$SkipSecrets,
    [switch]$SkipConfig,
    [switch]$SkipPrerequisites,
    [switch]$SkipExtension,
    [switch]$NoStartup,
    [switch]$NoLaunch,
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\TimeSheetHelper')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$AppName = 'Time Sheet Helper'
$ExeName = 'TimeSheetHelper.exe'
$ProcessName = 'TimeSheetHelper'
$UninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\TimeSheetHelper'
$DataDir = Join-Path $env:LOCALAPPDATA 'TimeSheetHelper'
$LocalSettingsName = 'appsettings.Local.json'
$ShortcutName = "$AppName.lnk"

$PackageDir = $PSScriptRoot
$PackageAppDir = Join-Path $PackageDir 'app'
$PackageVsCodeDir = Join-Path $PackageDir 'vscode'

# ------------------------------------------------------------------------------------------------
# Console helpers
# ------------------------------------------------------------------------------------------------
function Write-Section([string]$Title) { Write-Host ''; Write-Host "== $Title ==" -ForegroundColor Cyan }
function Write-Ok([string]$Message) { Write-Host "  [OK] $Message" -ForegroundColor Green }
function Write-Info([string]$Message) { Write-Host "  $Message" }
function Write-Warn([string]$Message) { Write-Host "  [!] $Message" -ForegroundColor Yellow }

function Invoke-Step {
    # Runs an action, or only describes it in -DryRun mode.
    param([string]$Description, [scriptblock]$Action)
    if ($DryRun) {
        Write-Host "  [simulation] $Description" -ForegroundColor DarkYellow
        return
    }
    Write-Info $Description
    & $Action
}

function Confirm-Choice {
    # Yes/no question in French. -Yes (unattended) returns the default answer.
    param([string]$Question, [bool]$Default = $true)
    if ($Yes) {
        $label = if ($Default) { 'oui' } else { 'non' }
        Write-Info "$Question -> $label (mode sans surveillance)"
        return $Default
    }
    $suffix = if ($Default) { '[O/n]' } else { '[o/N]' }
    $answer = Read-Host "  $Question $suffix"
    if ([string]::IsNullOrWhiteSpace($answer)) { return $Default }
    return ($answer.Trim() -match '^(o|oui|y|yes)$')
}

function Update-SessionPath {
    # Reload PATH after a winget install so newly installed tools are found in this session.
    $machine = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $user = [Environment]::GetEnvironmentVariable('Path', 'User')
    $env:Path = (@($machine, $user) | Where-Object { $_ }) -join ';'
}

function Get-JsonProperty {
    param($Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($property) { return $property.Value }
    return $null
}

function Set-JsonProperty {
    param($Object, [string]$Name, $Value)
    if ($Object.PSObject.Properties[$Name]) { $Object.$Name = $Value }
    else { $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value }
}

function Get-JsonSection {
    # Returns (creating it if needed) a nested object of a configuration document.
    param($Object, [string]$Name)
    $section = Get-JsonProperty $Object $Name
    if ($null -eq $section) {
        $section = New-Object PSObject
        Set-JsonProperty $Object $Name $section
    }
    return $section
}

# ------------------------------------------------------------------------------------------------
# Windows Credential Manager (generic credentials, identical to CredentialManagerSecretStore).
# cmdkey is not used: it would expose the secret on the command line.
# ------------------------------------------------------------------------------------------------
function Initialize-CredentialApi {
    if ('TimeSheetHelperInstaller.Credentials' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace TimeSheetHelperInstaller
{
    public static class Credentials
    {
        private const int CRED_TYPE_GENERIC = 1;
        private const int CRED_PERSIST_LOCAL_MACHINE = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public int Flags;
            public int Type;
            public string TargetName;
            public string Comment;
            public long LastWritten;
            public int CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public int AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(ref CREDENTIAL credential, int flags);

        [DllImport("advapi32.dll")]
        private static extern void CredFree(IntPtr buffer);

        public static bool Exists(string target)
        {
            IntPtr ptr;
            if (!CredRead(target, CRED_TYPE_GENERIC, 0, out ptr)) return false;
            CredFree(ptr);
            return true;
        }

        public static void Write(string target, string userName, string secret)
        {
            byte[] bytes = Encoding.Unicode.GetBytes(secret);
            IntPtr blob = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, blob, bytes.Length);
                CREDENTIAL credential = new CREDENTIAL();
                credential.Type = CRED_TYPE_GENERIC;
                credential.TargetName = target;
                credential.CredentialBlob = blob;
                credential.CredentialBlobSize = bytes.Length;
                credential.Persist = CRED_PERSIST_LOCAL_MACHINE;
                credential.UserName = userName;
                if (!CredWrite(ref credential, 0))
                    throw new InvalidOperationException("CredWrite failed: " + Marshal.GetLastWin32Error());
            }
            finally
            {
                Marshal.FreeHGlobal(blob);
            }
        }
    }
}
'@
}

function ConvertTo-PlainText([Security.SecureString]$Secure) {
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

# ------------------------------------------------------------------------------------------------
# Detection helpers
# ------------------------------------------------------------------------------------------------
function Find-Edge {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe'),
        (Join-Path $env:ProgramFiles 'Microsoft\Edge\Application\msedge.exe'),
        (Join-Path $env:LOCALAPPDATA 'Microsoft\Edge\Application\msedge.exe')
    )
    foreach ($root in 'HKLM:', 'HKCU:') {
        $appPath = Get-ItemProperty -Path "$root\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe" -ErrorAction SilentlyContinue
        if ($appPath -and $appPath.PSObject.Properties['(default)']) { $candidates += $appPath.'(default)' }
    }
    return $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}

function Find-Tool {
    # Returns the path of a command-line tool, looking in PATH then in well-known folders.
    param([string]$Command, [string[]]$Fallbacks = @())
    $found = Get-Command $Command -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) { return $found.Source }
    return $Fallbacks | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}

function Find-VsCodeCli {
    $clis = @()
    $stable = Find-Tool 'code.cmd' @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Microsoft VS Code\bin\code.cmd'),
        (Join-Path $env:ProgramFiles 'Microsoft VS Code\bin\code.cmd'))
    if ($stable) { $clis += [pscustomobject]@{ Name = 'VS Code'; Path = $stable } }
    $insiders = Find-Tool 'code-insiders.cmd' @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Microsoft VS Code Insiders\bin\code-insiders.cmd'),
        (Join-Path $env:ProgramFiles 'Microsoft VS Code Insiders\bin\code-insiders.cmd'))
    if ($insiders) { $clis += [pscustomobject]@{ Name = 'VS Code Insiders'; Path = $insiders } }
    return $clis
}

function Install-WithWinget {
    param([string]$Id, [string]$DisplayName, [switch]$UserScope)
    $arguments = @('install', '--id', $Id, '--exact', '--source', 'winget',
        '--accept-package-agreements', '--accept-source-agreements', '--silent')
    if ($UserScope) { $arguments += @('--scope', 'user') }
    Invoke-Step "winget $($arguments -join ' ')" {
        & winget @arguments
        if ($LASTEXITCODE -ne 0) { Write-Warn "L'installation de $DisplayName via winget a échoué (code $LASTEXITCODE)." }
        else { Write-Ok "$DisplayName installé." }
        Update-SessionPath
    }
}

# ------------------------------------------------------------------------------------------------
# Start
# ------------------------------------------------------------------------------------------------
$version = 'inconnue'
$versionFile = Join-Path $PackageDir 'version.txt'
if (Test-Path $versionFile) { $version = (Get-Content $versionFile -TotalCount 1).Trim() }

Write-Host ''
Write-Host "Installation de $AppName $version" -ForegroundColor Cyan
if ($DryRun) { Write-Host 'MODE SIMULATION (-DryRun) : aucune modification ne sera effectuée.' -ForegroundColor DarkYellow }
Write-Info "Dossier d'installation : $InstallDir"
Write-Info "Données utilisateur (jamais modifiées) : $DataDir"

if (-not (Test-Path (Join-Path $PackageAppDir $ExeName))) {
    throw "Paquet incomplet : $ExeName est introuvable dans $PackageAppDir. Décompressez toute l'archive avant de lancer l'installation."
}

# ------------------------------------------------------------------------------------------------
# 1. Prerequisites
# ------------------------------------------------------------------------------------------------
Write-Section 'Vérification des prérequis'

$os = [Environment]::OSVersion.Version
$arch = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
if ($os.Major -lt 10 -or -not [Environment]::Is64BitOperatingSystem) {
    throw "Windows 10 ou 11 64 bits est requis (détecté : Windows $os, $arch)."
}
$osName = if ($os.Build -ge 22000) { 'Windows 11' } else { 'Windows 10' }
Write-Ok "$osName (build $($os.Build)), architecture $arch"
if ($arch -eq 'ARM64') { Write-Warn "Processeur ARM64 : l'application x64 fonctionnera via l'émulation de Windows." }

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Warn "Session administrateur détectée. L'installation est par utilisateur : lancez-la plutôt depuis une session normale."
}

$edge = Find-Edge
if ($edge) {
    Write-Ok "Microsoft Edge : $edge"
}
else {
    Write-Warn "Microsoft Edge est introuvable. Il est requis pour la saisie automatique dans Forecast (Playwright, canal msedge)."
}

$winget = Find-Tool 'winget.exe' @((Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\winget.exe'))
if ($winget) { Write-Ok 'winget disponible' } else { Write-Warn "winget est introuvable (App Installer) : les outils manquants devront être installés manuellement." }

$hasVsix = [bool](Get-ChildItem -Path $PackageVsCodeDir -Filter '*.vsix' -File -ErrorAction SilentlyContinue)
$wantedTools = @(
    @{ Id = 'Git.Git'; Name = 'Git'; Command = 'git.exe'; Default = $true; UserScope = $false
        Why = 'collecte des commits locaux'
        Fallbacks = @((Join-Path $env:ProgramFiles 'Git\cmd\git.exe'), (Join-Path $env:LOCALAPPDATA 'Programs\Git\cmd\git.exe')) },
    @{ Id = 'GitHub.cli'; Name = 'GitHub CLI (gh)'; Command = 'gh.exe'; Default = $true; UserScope = $false
        Why = 'obtention du jeton GitHub'
        Fallbacks = @((Join-Path $env:ProgramFiles 'GitHub CLI\gh.exe'), (Join-Path $env:LOCALAPPDATA 'Programs\GitHub CLI\gh.exe')) }
)
if (-not $edge) {
    $wantedTools += @{ Id = 'Microsoft.Edge'; Name = 'Microsoft Edge'; Command = 'msedge.exe'; Default = $true; UserScope = $false
        Why = 'automatisation de Forecast'; Fallbacks = @() }
}

foreach ($tool in $wantedTools) {
    $path = Find-Tool $tool.Command $tool.Fallbacks
    if ($path) { Write-Ok "$($tool.Name) : $path"; continue }
    Write-Warn "$($tool.Name) est absent ($($tool.Why))."
    if ($SkipPrerequisites -or -not $winget) { continue }
    if (Confirm-Choice "Installer $($tool.Name) avec winget (une élévation UAC peut être demandée) ?" $tool.Default) {
        Install-WithWinget -Id $tool.Id -DisplayName $tool.Name -UserScope:$tool.UserScope
    }
}

$codeClis = @(Find-VsCodeCli)
if ($codeClis.Count -gt 0) {
    foreach ($cli in $codeClis) { Write-Ok "$($cli.Name) : $($cli.Path)" }
}
elseif ($hasVsix -and -not $SkipExtension) {
    Write-Warn "VS Code est absent : l'extension Time Sheet Helper ne pourra pas être installée."
    if (-not $SkipPrerequisites -and $winget -and (Confirm-Choice 'Installer VS Code (installation utilisateur) pour utiliser l''extension ?' $false)) {
        Install-WithWinget -Id 'Microsoft.VisualStudioCode' -DisplayName 'VS Code' -UserScope
        $codeClis = @(Find-VsCodeCli)
    }
}

# ------------------------------------------------------------------------------------------------
# 2. Stop the running instance
# ------------------------------------------------------------------------------------------------
Write-Section 'Arrêt de l''instance en cours'
$running = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
if ($running.Count -eq 0) {
    Write-Ok 'Aucune instance en cours.'
}
else {
    Invoke-Step "Arrêt de $ProcessName (PID $(($running | ForEach-Object { $_.Id }) -join ', '))" {
        $running | Stop-Process -Force
        $running | ForEach-Object { $_.WaitForExit(15000) | Out-Null }
        Write-Ok 'Instance arrêtée.'
    }
}

# ------------------------------------------------------------------------------------------------
# 3. Copy the application
# ------------------------------------------------------------------------------------------------
Write-Section 'Copie des fichiers de l''application'
$localSettingsPath = Join-Path $InstallDir $LocalSettingsName
$isUpgrade = Test-Path (Join-Path $InstallDir $ExeName)
if ($isUpgrade) {
    $previous = Get-ItemProperty -Path $UninstallKey -ErrorAction SilentlyContinue
    $previousVersion = if ($previous -and $previous.PSObject.Properties['DisplayVersion']) { $previous.DisplayVersion } else { 'inconnue' }
    Write-Info "Mise à jour d'une installation existante (version $previousVersion -> $version)."
}
if (Test-Path $localSettingsPath) { Write-Ok "$LocalSettingsName existant conservé." }

Invoke-Step "Nettoyage de $InstallDir (sauf $LocalSettingsName)" {
    if (Test-Path $InstallDir) {
        Get-ChildItem -Path $InstallDir -Force |
            Where-Object { $_.Name -ne $LocalSettingsName } |
            Remove-Item -Recurse -Force
    }
    else {
        New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    }
}
Invoke-Step "Copie de app\ vers $InstallDir" {
    Copy-Item -Path (Join-Path $PackageAppDir '*') -Destination $InstallDir -Recurse -Force
    foreach ($file in 'uninstall.ps1', 'version.txt') {
        $source = Join-Path $PackageDir $file
        if (Test-Path $source) { Copy-Item $source -Destination $InstallDir -Force }
    }
    $extensionId = Join-Path $PackageVsCodeDir 'extension-id.txt'
    if (Test-Path $extensionId) { Copy-Item $extensionId -Destination (Join-Path $InstallDir 'vscode-extension-id.txt') -Force }
    # Files extracted from a downloaded zip carry the Mark of the Web; remove it.
    Get-ChildItem -Path $InstallDir -Recurse -File | Unblock-File
    Write-Ok 'Fichiers copiés.'
}
$exePath = Join-Path $InstallDir $ExeName

# ------------------------------------------------------------------------------------------------
# 4. VS Code extension
# ------------------------------------------------------------------------------------------------
Write-Section 'Extension VS Code'
$vsix = Get-ChildItem -Path $PackageVsCodeDir -Filter '*.vsix' -File -ErrorAction SilentlyContinue | Select-Object -First 1
if ($SkipExtension) {
    Write-Info 'Étape ignorée (-SkipExtension).'
}
elseif (-not $vsix) {
    Write-Info 'Aucune extension (.vsix) dans ce paquet.'
}
elseif ($codeClis.Count -eq 0) {
    Write-Warn "VS Code introuvable : installez-le puis exécutez « code --install-extension `"$($vsix.FullName)`" »."
}
else {
    foreach ($cli in $codeClis) {
        Invoke-Step "Installation de $($vsix.Name) dans $($cli.Name)" {
            & $cli.Path --install-extension $vsix.FullName --force
            if ($LASTEXITCODE -ne 0) { Write-Warn "Échec de l'installation de l'extension dans $($cli.Name) (code $LASTEXITCODE)." }
            else { Write-Ok "Extension installée dans $($cli.Name)." }
        }
    }
}

# ------------------------------------------------------------------------------------------------
# 5. Shortcuts
# ------------------------------------------------------------------------------------------------
Write-Section 'Raccourcis'
function New-AppShortcut([string]$Path) {
    Invoke-Step "Création du raccourci $Path" {
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($Path)
        $shortcut.TargetPath = $exePath
        $shortcut.WorkingDirectory = $InstallDir
        $shortcut.IconLocation = "$exePath,0"
        $shortcut.Description = 'Aide à la saisie des feuilles de temps'
        $shortcut.WindowStyle = 7  # minimized: the app lives in the notification area
        $shortcut.Save()
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
    }
}
New-AppShortcut (Join-Path ([Environment]::GetFolderPath('Programs')) $ShortcutName)
$startupShortcut = Join-Path ([Environment]::GetFolderPath('Startup')) $ShortcutName
if ($NoStartup) {
    Write-Info 'Démarrage automatique non configuré (-NoStartup).'
    if (Test-Path $startupShortcut) {
        Invoke-Step "Suppression du raccourci de démarrage existant $startupShortcut" { Remove-Item $startupShortcut -Force }
    }
}
else {
    New-AppShortcut $startupShortcut
}

# ------------------------------------------------------------------------------------------------
# 6. Secrets (Windows Credential Manager)
# ------------------------------------------------------------------------------------------------
Write-Section 'Secrets (Gestionnaire d''identification Windows)'
$secretDefinitions = @(
    @{ Target = 'TimeSheetHelper:GitHub'; Label = 'Jeton GitHub' },
    @{ Target = 'TimeSheetHelper:AzureDevOps'; Label = 'PAT Azure DevOps (portée : Work Items Read, Code Read)' },
    @{ Target = 'TimeSheetHelper:Anthropic'; Label = 'Clé API Anthropic' }
)
if ($SkipSecrets) {
    Write-Info 'Étape ignorée (-SkipSecrets).'
}
else {
    Initialize-CredentialApi
    $gh = Find-Tool 'gh.exe' @((Join-Path $env:ProgramFiles 'GitHub CLI\gh.exe'))
    $configureSecrets = $Yes -or (Confirm-Choice 'Configurer maintenant les secrets (jetons et clés API) ?' $true)
    foreach ($definition in $secretDefinitions) {
        $target = $definition.Target
        $exists = [TimeSheetHelperInstaller.Credentials]::Exists($target)
        if (-not $configureSecrets) {
            $state = if ($exists) { 'présent' } else { 'absent' }
            Write-Info "$($definition.Label) ($target) : $state"
            continue
        }
        if ($exists -and -not (Confirm-Choice "$($definition.Label) déjà enregistré ($target). Le remplacer ?" $false)) {
            Write-Ok "$target conservé."
            continue
        }

        if ($DryRun) {
            $source = if ($target -eq 'TimeSheetHelper:GitHub' -and $gh) { 'gh auth token ou saisie masquée' } else { 'saisie masquée' }
            Invoke-Step "Enregistrement de $target ($source, identifiant générique, utilisateur $env:USERNAME)" {}
            continue
        }

        $secret = $null
        if ($target -eq 'TimeSheetHelper:GitHub' -and $gh) {
            $ghToken = (& $gh auth token 2>$null)
            if ($LASTEXITCODE -eq 0 -and $ghToken) {
                if (Confirm-Choice 'Utiliser le jeton de la session GitHub CLI (gh auth token) ?' $true) { $secret = "$ghToken".Trim() }
            }
            elseif ($Yes) {
                Write-Warn 'GitHub CLI n''est pas authentifié (gh auth login) : jeton GitHub non configuré.'
            }
        }
        if (-not $secret) {
            if ($Yes) {
                Write-Info "$($definition.Label) : non configuré (mode sans surveillance)."
                continue
            }
            $secure = Read-Host "  $($definition.Label) (Entrée pour ignorer)" -AsSecureString
            $secret = ConvertTo-PlainText $secure
        }
        if ([string]::IsNullOrWhiteSpace($secret)) {
            Write-Info "$target ignoré."
            continue
        }
        $userName = $env:USERNAME
        $secretValue = $secret
        Invoke-Step "Enregistrement de $target (identifiant générique, utilisateur $userName)" {
            [TimeSheetHelperInstaller.Credentials]::Write($target, $userName, $secretValue)
            Write-Ok "$target enregistré."
        }
        $secret = $null
        $secretValue = $null
    }
}

# ------------------------------------------------------------------------------------------------
# 7. Local configuration (appsettings.Local.json)
# ------------------------------------------------------------------------------------------------
Write-Section "Configuration locale ($LocalSettingsName)"
if ($SkipConfig -or $Yes) {
    $reason = if ($SkipConfig) { '-SkipConfig' } else { 'mode sans surveillance' }
    Write-Info "Étape ignorée ($reason)."
}
elseif (Confirm-Choice 'Configurer Microsoft 365 et Azure DevOps maintenant ?' $true) {
    $settings = New-Object PSObject
    if (Test-Path $localSettingsPath) {
        $raw = Get-Content $localSettingsPath -Raw -Encoding UTF8
        if (-not [string]::IsNullOrWhiteSpace($raw)) { $settings = $raw | ConvertFrom-Json }
    }

    function Read-Value([string]$Prompt, $Current) {
        $hint = if ($Current) { " [actuel : $Current]" } else { '' }
        $value = Read-Host "  $Prompt$hint (Entrée pour conserver)"
        if ([string]::IsNullOrWhiteSpace($value)) { return $Current }
        return $value.Trim()
    }

    $m365 = Get-JsonSection $settings 'Microsoft365'
    Write-Info 'Microsoft 365 (inscription d''application Entra ID, Outlook / Teams via Microsoft Graph) :'
    $tenantId = Read-Value 'TenantId' (Get-JsonProperty $m365 'TenantId')
    $clientId = Read-Value 'ClientId' (Get-JsonProperty $m365 'ClientId')
    if ($tenantId) { Set-JsonProperty $m365 'TenantId' $tenantId }
    if ($clientId) { Set-JsonProperty $m365 'ClientId' $clientId }

    $ado = Get-JsonSection $settings 'AzureDevOps'
    $currentOrgs = @(Get-JsonProperty $ado 'Organizations') | Where-Object { $_ }
    $orgsText = Read-Value 'Organisations Azure DevOps (séparées par des virgules)' ($currentOrgs -join ', ')
    if ($orgsText) {
        [string[]]$orgs = @($orgsText -split '[,;\s]+' | Where-Object { $_ })
        Set-JsonProperty $ado 'Organizations' $orgs
    }

    $json = $settings | ConvertTo-Json -Depth 10
    Invoke-Step "Écriture de $localSettingsPath" {
        [IO.File]::WriteAllText($localSettingsPath, $json, (New-Object Text.UTF8Encoding($false)))
        Write-Ok "$LocalSettingsName enregistré."
    }
}

# ------------------------------------------------------------------------------------------------
# 8. Apps & features entry
# ------------------------------------------------------------------------------------------------
Write-Section 'Inscription dans « Applications installées »'
$powershellExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$uninstallScript = Join-Path $InstallDir 'uninstall.ps1'
$uninstallCommand = "`"$powershellExe`" -NoProfile -ExecutionPolicy Bypass -File `"$uninstallScript`""
Invoke-Step "Écriture de $UninstallKey" {
    $sizeKb = [int]((Get-ChildItem -Path $InstallDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1KB)
    New-Item -Path $UninstallKey -Force | Out-Null
    $values = @{
        DisplayName          = $AppName
        DisplayVersion       = $version
        Publisher            = $AppName
        DisplayIcon          = "$exePath,0"
        InstallLocation      = $InstallDir
        InstallDate          = (Get-Date -Format 'yyyyMMdd')
        UninstallString      = $uninstallCommand
        QuietUninstallString = "$uninstallCommand -Yes -KeepData"
    }
    foreach ($name in $values.Keys) {
        New-ItemProperty -Path $UninstallKey -Name $name -Value $values[$name] -PropertyType String -Force | Out-Null
    }
    New-ItemProperty -Path $UninstallKey -Name 'EstimatedSize' -Value $sizeKb -PropertyType DWord -Force | Out-Null
    New-ItemProperty -Path $UninstallKey -Name 'NoModify' -Value 1 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -Path $UninstallKey -Name 'NoRepair' -Value 1 -PropertyType DWord -Force | Out-Null
    Write-Ok 'Entrée de désinstallation enregistrée.'
}

# ------------------------------------------------------------------------------------------------
# 9. Launch
# ------------------------------------------------------------------------------------------------
Write-Section 'Démarrage'
$url = 'http://localhost:5178'
foreach ($settingsFile in (Join-Path $PackageAppDir 'appsettings.json'), $localSettingsPath) {
    if (-not (Test-Path $settingsFile)) { continue }
    try {
        $urls = Get-JsonProperty (Get-Content $settingsFile -Raw -Encoding UTF8 | ConvertFrom-Json) 'Urls'
        if ($urls) { $url = ($urls -split ';')[0] }
    }
    catch { Write-Warn "Impossible de lire $settingsFile : $($_.Exception.Message)" }
}

if ($NoLaunch) {
    Write-Info "Démarrage ignoré (-NoLaunch). Interface : $url"
}
else {
    Invoke-Step "Démarrage de $exePath puis ouverture de $url" {
        Start-Process -FilePath $exePath -WorkingDirectory $InstallDir -WindowStyle Hidden
        $uri = [Uri]$url
        $deadline = (Get-Date).AddSeconds(45)
        $ready = $false
        while (-not $ready -and (Get-Date) -lt $deadline) {
            $client = New-Object Net.Sockets.TcpClient
            try { $client.Connect($uri.Host, $uri.Port); $ready = $true }
            catch { Start-Sleep -Milliseconds 500 }
            finally { $client.Close() }
        }
        if ($ready) {
            Start-Process $url
            Write-Ok "Application démarrée : $url"
        }
        else {
            Write-Warn "L'application ne répond pas encore sur $url. Ouvrez-la depuis l'icône de la zone de notification."
        }
    }
}

Write-Host ''
if ($DryRun) {
    Write-Host 'Simulation terminée : aucune modification n''a été effectuée.' -ForegroundColor Green
}
else {
    Write-Host "$AppName $version est installé." -ForegroundColor Green
    Write-Host "Pour désinstaller : Paramètres > Applications > Applications installées > $AppName."
}
