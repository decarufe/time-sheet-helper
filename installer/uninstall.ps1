<#
.SYNOPSIS
    Uninstalls Time Sheet Helper for the current user.

.DESCRIPTION
    Stops the application, removes the VS Code extension, the shortcuts, the "Apps & features"
    entry and the installation folder. User data (%LOCALAPPDATA%\TimeSheetHelper) and the secrets
    stored in Windows Credential Manager are removed only after confirmation, or with -RemoveData.

.PARAMETER KeepData
    Keep user data and stored secrets without asking.

.PARAMETER RemoveData
    Remove user data and stored secrets without asking.

.PARAMETER Yes
    Unattended mode: answers questions with their default (data and secrets are kept).

.PARAMETER DryRun
    Only displays the actions that would be performed.
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Interactive console script with colored output.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Parameters are used inside functions.')]
[CmdletBinding()]
param(
    [switch]$KeepData,
    [switch]$RemoveData,
    [switch]$Yes,
    [switch]$DryRun,
    [string]$InstallDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($KeepData -and $RemoveData) { throw '-KeepData et -RemoveData sont incompatibles.' }

$AppName = 'Time Sheet Helper'
$ProcessName = 'TimeSheetHelper'
$UninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\TimeSheetHelper'
$DataDir = Join-Path $env:LOCALAPPDATA 'TimeSheetHelper'
$ShortcutName = "$AppName.lnk"
$CredentialTargets = @('TimeSheetHelper:GitHub', 'TimeSheetHelper:AzureDevOps', 'TimeSheetHelper:Anthropic', 'TimeSheetHelper:Ingest')

if (-not $InstallDir) {
    $entry = Get-ItemProperty -Path $UninstallKey -ErrorAction SilentlyContinue
    if ($entry -and $entry.PSObject.Properties['InstallLocation'] -and $entry.InstallLocation) { $InstallDir = $entry.InstallLocation }
    else { $InstallDir = Join-Path $env:LOCALAPPDATA 'Programs\TimeSheetHelper' }
}

function Write-Section([string]$Title) { Write-Host ''; Write-Host "== $Title ==" -ForegroundColor Cyan }
function Write-Ok([string]$Message) { Write-Host "  [OK] $Message" -ForegroundColor Green }
function Write-Info([string]$Message) { Write-Host "  $Message" }
function Write-Warn([string]$Message) { Write-Host "  [!] $Message" -ForegroundColor Yellow }

function Invoke-Step {
    param([string]$Description, [scriptblock]$Action)
    if ($DryRun) {
        Write-Host "  [simulation] $Description" -ForegroundColor DarkYellow
        return
    }
    Write-Info $Description
    & $Action
}

function Confirm-Choice {
    param([string]$Question, [bool]$Default = $false)
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

function Find-Tool {
    param([string]$Command, [string[]]$Fallbacks = @())
    $found = Get-Command $Command -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) { return $found.Source }
    return $Fallbacks | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}

Write-Host ''
Write-Host "Désinstallation de $AppName" -ForegroundColor Cyan
if ($DryRun) { Write-Host 'MODE SIMULATION (-DryRun) : aucune modification ne sera effectuée.' -ForegroundColor DarkYellow }
Write-Info "Dossier d'installation : $InstallDir"

# The script itself may live in the folder being removed: move out of it.
Set-Location -Path $env:TEMP

# 1. Stop the application ------------------------------------------------------------------------
Write-Section 'Arrêt de l''application'
$running = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
if ($running.Count -eq 0) { Write-Ok 'Aucune instance en cours.' }
else {
    Invoke-Step "Arrêt de $ProcessName (PID $(($running | ForEach-Object { $_.Id }) -join ', '))" {
        $running | Stop-Process -Force
        $running | ForEach-Object { $_.WaitForExit(15000) | Out-Null }
        Write-Ok 'Instance arrêtée.'
    }
}

# 2. VS Code extension ---------------------------------------------------------------------------
Write-Section 'Extension VS Code'
$extensionIdFile = Join-Path $InstallDir 'vscode-extension-id.txt'
if (-not (Test-Path $extensionIdFile)) {
    Write-Info 'Aucune extension enregistrée par l''installation.'
}
else {
    $extensionId = (Get-Content $extensionIdFile -TotalCount 1).Trim()
    $clis = @(
        (Find-Tool 'code.cmd' @((Join-Path $env:LOCALAPPDATA 'Programs\Microsoft VS Code\bin\code.cmd'), (Join-Path $env:ProgramFiles 'Microsoft VS Code\bin\code.cmd'))),
        (Find-Tool 'code-insiders.cmd' @((Join-Path $env:LOCALAPPDATA 'Programs\Microsoft VS Code Insiders\bin\code-insiders.cmd'), (Join-Path $env:ProgramFiles 'Microsoft VS Code Insiders\bin\code-insiders.cmd')))
    ) | Where-Object { $_ }
    if (-not $clis) { Write-Info 'VS Code introuvable : rien à faire.' }
    foreach ($cli in $clis) {
        Invoke-Step "Désinstallation de l'extension $extensionId ($cli)" {
            & $cli --uninstall-extension $extensionId 2>&1 | Out-Null
            if ($LASTEXITCODE -eq 0) { Write-Ok 'Extension désinstallée.' } else { Write-Info 'Extension absente ou déjà désinstallée.' }
        }
    }
}

# 3. Shortcuts -----------------------------------------------------------------------------------
Write-Section 'Raccourcis'
foreach ($folder in 'Programs', 'Startup') {
    $shortcut = Join-Path ([Environment]::GetFolderPath($folder)) $ShortcutName
    if (Test-Path $shortcut) { Invoke-Step "Suppression de $shortcut" { Remove-Item $shortcut -Force } }
    else { Write-Info "Absent : $shortcut" }
}

# 4. Apps & features entry -----------------------------------------------------------------------
Write-Section 'Entrée « Applications installées »'
if (Test-Path $UninstallKey) { Invoke-Step "Suppression de $UninstallKey" { Remove-Item $UninstallKey -Recurse -Force } }
else { Write-Info 'Aucune entrée.' }

# 5. Installation folder -------------------------------------------------------------------------
Write-Section 'Fichiers de l''application'
if (Test-Path $InstallDir) {
    Invoke-Step "Suppression de $InstallDir" {
        Remove-Item $InstallDir -Recurse -Force
        Write-Ok 'Dossier supprimé.'
    }
}
else { Write-Info "Absent : $InstallDir" }

# 6. User data and secrets -----------------------------------------------------------------------
Write-Section 'Données et secrets'
$removeUserData = $false
if ($RemoveData) { $removeUserData = $true }
elseif (-not $KeepData) {
    $removeUserData = Confirm-Choice "Supprimer aussi les données ($DataDir) et les secrets enregistrés (Gestionnaire d'identification) ?" $false
}

if (-not $removeUserData) {
    Write-Ok "Données conservées : $DataDir"
    Write-Ok 'Secrets conservés dans le Gestionnaire d''identification Windows.'
}
else {
    if (Test-Path $DataDir) { Invoke-Step "Suppression de $DataDir" { Remove-Item $DataDir -Recurse -Force } }
    else { Write-Info "Absent : $DataDir" }

    # CredDelete with CRED_TYPE_GENERIC, matching how the app and install.ps1 store the secrets.
    if (-not ('TimeSheetHelperUninstaller.Credentials' -as [type])) {
        Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
namespace TimeSheetHelperUninstaller
{
    public static class Credentials
    {
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string target, int type, int flags);

        public static bool Delete(string target) { return CredDelete(target, 1, 0); }
    }
}
'@
    }
    foreach ($target in $CredentialTargets) {
        Invoke-Step "Suppression de l'identifiant générique $target" {
            if ([TimeSheetHelperUninstaller.Credentials]::Delete($target)) { Write-Ok "$target supprimé." }
            else { Write-Info "$target absent." }
        }
    }
}

Write-Host ''
if ($DryRun) { Write-Host 'Simulation terminée : aucune modification n''a été effectuée.' -ForegroundColor Green }
else { Write-Host "$AppName a été désinstallé." -ForegroundColor Green }
