<#
.SYNOPSIS
    Builds the Time Sheet Helper installation package (artifacts/TimeSheetHelper-<version>.zip).

.DESCRIPTION
    1. Publishes src/TimeSheetHelper.App as a self-contained win-x64 folder (not single-file:
       Microsoft.Playwright needs its .playwright driver folder next to the executable).
    2. Builds the VS Code extension (.vsix) when extensions/vscode exists and Node.js is available.
    3. Assembles app/, vscode/, install.ps1, uninstall.ps1, Install.cmd and README.txt into a zip.

.PARAMETER Version
    Package version. Defaults to `git describe --tags` (leading "v" stripped), or 0.1.0-<sha>.

.PARAMETER SkipExtension
    Do not build the VS Code extension even if extensions/vscode exists.

.EXAMPLE
    pwsh ./installer/build-installer.ps1
    pwsh ./installer/build-installer.ps1 -Version 1.2.0
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Interactive console script with colored output.')]
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$OutputDir,
    [switch]$SkipExtension
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot 'artifacts' }
$appProject = Join-Path $repoRoot 'src\TimeSheetHelper.App\TimeSheetHelper.App.csproj'
$extensionDir = Join-Path $repoRoot 'extensions\vscode'

function Write-Step([string]$Message) { Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Warn([string]$Message) { Write-Host "AVERTISSEMENT : $Message" -ForegroundColor Yellow }

function Invoke-Native {
    # Runs a native command and fails on a non-zero exit code.
    param([string]$FilePath, [string[]]$Arguments)
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "La commande « $FilePath $($Arguments -join ' ') » a échoué (code $LASTEXITCODE)." }
}

# --- Version -----------------------------------------------------------------------------------
if (-not $Version) {
    $described = $null
    if (Get-Command git -ErrorAction SilentlyContinue) {
        $described = (& git -C $repoRoot describe --tags --always --dirty 2>$null)
        if ($LASTEXITCODE -ne 0) { $described = $null }
    }
    if ($described -and $described -match '^v?(\d+\.\d+\.\d+.*)$') {
        $Version = $Matches[1]
    }
    elseif ($described) {
        # No tag yet: describe returned only the abbreviated commit id.
        $Version = "0.1.0-$described"
    }
    else {
        $Version = '0.1.0'
    }
}
if ($Version -notmatch '^(\d+)\.(\d+)\.(\d+)') { throw "Version invalide : « $Version » (attendu : X.Y.Z[-suffixe])." }
$numericVersion = "$($Matches[1]).$($Matches[2]).$($Matches[3])"

$packageName = "TimeSheetHelper-$Version"
$stagingRoot = Join-Path $OutputDir 'staging'
$stagingDir = Join-Path $stagingRoot $packageName
$zipPath = Join-Path $OutputDir "$packageName.zip"

Write-Step "Construction du paquet $packageName"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Le SDK .NET (dotnet) est introuvable. Installez le SDK .NET 10 : winget install Microsoft.DotNet.SDK.10'
}

if (Test-Path $stagingDir) { Remove-Item $stagingDir -Recurse -Force }
New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null

# --- Application ---------------------------------------------------------------------------------
Write-Step 'Publication de l''application (.NET autonome, win-x64)'
$appOut = Join-Path $stagingDir 'app'
Invoke-Native dotnet @(
    'publish', $appProject,
    '-c', $Configuration,
    '-r', $Runtime,
    '--self-contained', 'true',
    '-o', $appOut,
    '-p:PublishSingleFile=false',
    '-p:PublishTrimmed=false',
    '-p:DebugType=none',
    "-p:Version=$numericVersion",
    "-p:InformationalVersion=$Version",
    '-nologo'
)
if (-not (Test-Path (Join-Path $appOut 'TimeSheetHelper.exe'))) {
    throw "TimeSheetHelper.exe est absent de la sortie de publication ($appOut)."
}

# --- VS Code extension ---------------------------------------------------------------------------
$vscodeOut = Join-Path $stagingDir 'vscode'
if ($SkipExtension) {
    Write-Warn 'Extension VS Code ignorée (-SkipExtension).'
}
elseif (-not (Test-Path (Join-Path $extensionDir 'package.json'))) {
    Write-Warn "Aucune extension VS Code trouvée dans $extensionDir : étape ignorée."
}
elseif (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
    Write-Warn 'Node.js/npm introuvable : l''extension VS Code ne sera pas incluse (winget install OpenJS.NodeJS.LTS).'
}
else {
    Write-Step 'Construction de l''extension VS Code (.vsix)'
    Push-Location $extensionDir
    try {
        $buildStart = Get-Date
        if (Test-Path 'package-lock.json') { Invoke-Native npm @('ci') } else { Invoke-Native npm @('install') }
        Invoke-Native npm @('run', 'package')
        $vsix = Get-ChildItem -Path $extensionDir -Filter '*.vsix' -Recurse -File |
            Where-Object { $_.FullName -notmatch '\\node_modules\\' -and $_.LastWriteTime -ge $buildStart.AddSeconds(-5) } |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if (-not $vsix) { throw 'npm run package n''a produit aucun fichier .vsix.' }
        New-Item -ItemType Directory -Path $vscodeOut -Force | Out-Null
        Copy-Item $vsix.FullName -Destination $vscodeOut

        # Record the extension id (publisher.name) so uninstall.ps1 can remove it later.
        $manifest = Get-Content (Join-Path $extensionDir 'package.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($manifest.publisher -and $manifest.name) {
            Set-Content -Path (Join-Path $vscodeOut 'extension-id.txt') -Value "$($manifest.publisher).$($manifest.name)" -Encoding ASCII
        }
        Write-Host "    $($vsix.Name)"
    }
    finally {
        Pop-Location
    }
}

# --- Installer scripts ---------------------------------------------------------------------------
Write-Step 'Copie des scripts d''installation'
foreach ($file in 'install.ps1', 'uninstall.ps1', 'Install.cmd', 'README.txt') {
    Copy-Item (Join-Path $PSScriptRoot $file) -Destination $stagingDir
}
Set-Content -Path (Join-Path $stagingDir 'version.txt') -Value $Version -Encoding ASCII

# --- Zip -----------------------------------------------------------------------------------------
Write-Step 'Création de l''archive'
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $stagingDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

$sizeMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host ''
Write-Host "Paquet prêt : $zipPath ($sizeMb Mo)" -ForegroundColor Green
Write-Host "Contenu décompressé : $stagingDir"
Write-Host 'Installation : décompressez l''archive puis double-cliquez sur Install.cmd.'
