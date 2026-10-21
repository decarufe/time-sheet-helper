<#
.SYNOPSIS
    Builds the Time Sheet Helper Windows installer (artifacts/TimeSheetHelper-<version>.msi).

.DESCRIPTION
    1. Publishes src/TimeSheetHelper.App as a self-contained win-x64 folder. The publish also places the bundled source
       plugins (plugins\<package>\) in that folder.
    2. Builds the VS Code extension (.vsix) when extensions/vscode exists and pnpm is available.
    3. Builds installer/TimeSheetHelper.Installer (WiX) into a per-user .msi: no administrator rights, no .NET to install.

    The package installs to %LOCALAPPDATA%\Programs\TimeSheetHelper, starts the background application with the user's
    Windows session and adds a Start menu shortcut for the window. User data and settings are never touched.

.PARAMETER Version
    Package version. Defaults to `git describe --tags` (leading "v" stripped), or 0.1.0-<sha>.

.PARAMETER SkipExtension
    Do not build the VS Code extension even if extensions/vscode exists.

.PARAMETER CertificatePath
    Optional Authenticode code-signing certificate (.pfx). When set, the application binaries and the .msi are signed
    (SHA-256, timestamped) so Windows/SmartScreen shows a known publisher. Defaults to $env:SIGNING_CERT_PATH.
    The password is read from $env:SIGNING_CERT_PASSWORD so it never appears on a command line.

.PARAMETER TimestampUrl
    RFC 3161 timestamp server used when signing.

.EXAMPLE
    pwsh ./installer/build-msi.ps1
    pwsh ./installer/build-msi.ps1 -Version 1.2.0
    $env:SIGNING_CERT_PASSWORD = '...'; pwsh ./installer/build-msi.ps1 -CertificatePath C:\certs\code-signing.pfx
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Interactive console script with colored output.')]
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$OutputDir,
    [switch]$SkipExtension,
    [string]$CertificatePath = $env:SIGNING_CERT_PATH,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot 'artifacts' }
$appProject = Join-Path $repoRoot 'src\TimeSheetHelper.App\TimeSheetHelper.App.csproj'
$wixProject = Join-Path $PSScriptRoot 'TimeSheetHelper.Installer\TimeSheetHelper.Installer.wixproj'
$extensionDir = Join-Path $repoRoot 'extensions\vscode'

function Write-Step([string]$Message) { Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Warn([string]$Message) { Write-Host "AVERTISSEMENT : $Message" -ForegroundColor Yellow }

function Invoke-Native {
    # Runs a native command and fails on a non-zero exit code.
    param([string]$FilePath, [string[]]$Arguments)
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "La commande « $FilePath $($Arguments -join ' ') » a échoué (code $LASTEXITCODE)." }
}

function Find-SignTool {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $found = Get-ChildItem -Path $kits -Filter signtool.exe -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Name -eq 'x64' } | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $found) { throw 'signtool.exe est introuvable. Installez le Windows SDK (winget install Microsoft.WindowsSDK.10.0.26100).' }
    return $found.FullName
}

function Invoke-Sign {
    # Signs files with the Authenticode certificate, then verifies them against the Windows trust policy.
    param([string[]]$Files)
    if (-not $Files) { return }
    $arguments = @('sign', '/f', $CertificatePath, '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256', '/q')
    if ($env:SIGNING_CERT_PASSWORD) { $arguments += @('/p', $env:SIGNING_CERT_PASSWORD) }
    Invoke-Native $signTool ($arguments + $Files)
    Invoke-Native $signTool (@('verify', '/pa', '/q') + $Files)
}

$signTool = $null
if ($CertificatePath) {
    if (-not (Test-Path $CertificatePath)) { throw "Certificat de signature introuvable : $CertificatePath" }
    $signTool = Find-SignTool
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
# Windows Installer compares only the numeric part: major and minor up to 255, build up to 65535.
if ([int]$Matches[1] -gt 255 -or [int]$Matches[2] -gt 255 -or [int]$Matches[3] -gt 65535) { throw "Version hors limites pour un MSI : $Version." }
$numericVersion = "$($Matches[1]).$($Matches[2]).$($Matches[3])"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Le SDK .NET (dotnet) est introuvable. Installez le SDK .NET 10 : winget install Microsoft.DotNet.SDK.10'
}

$publishDir = Join-Path $OutputDir 'publish'
$msiName = "TimeSheetHelper-$Version"
Write-Step "Construction de $msiName.msi"

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

# --- Application, window and plugins ---------------------------------------------------------------
Write-Step 'Publication de l''application et des plugins (.NET autonome, win-x64)'
Invoke-Native dotnet @(
    'publish', $appProject,
    '-c', $Configuration,
    '-r', $Runtime,
    '--self-contained', 'true',
    '-o', $publishDir,
    '-p:PublishSingleFile=false',
    '-p:PublishTrimmed=false',
    '-p:DebugType=none',
    "-p:Version=$numericVersion",
    "-p:InformationalVersion=$Version",
    '-nologo'
)
foreach ($required in 'TimeSheetHelper.exe', 'plugins\windows-activity\plugin.json', 'plugins\devops\plugin.json', 'plugins\microsoft-365\plugin.json') {
    if (-not (Test-Path (Join-Path $publishDir $required))) { throw "$required est absent de la sortie de publication ($publishDir)." }
}

# Settings of the machine this runs on must never end up in a package.
foreach ($local in 'appsettings.Local.json', 'appsettings.Development.json') {
    Remove-Item (Join-Path $publishDir $local) -Force -ErrorAction SilentlyContinue
}

if ($signTool) {
    # Only our own binaries: third-party and runtime files keep their original signatures.
    Write-Step 'Signature des binaires de l''application'
    $ownBinaries = Get-ChildItem -Path $publishDir -Recurse -File -Include 'TimeSheetHelper*.exe', 'TimeSheetHelper*.dll'
    Invoke-Sign ($ownBinaries.FullName)
}
else {
    Write-Warn 'Aucun certificat de signature (-CertificatePath / SIGNING_CERT_PATH) : le MSI ne sera pas signé et Windows avertira au téléchargement.'
}

# --- VS Code extension ---------------------------------------------------------------------------
$wixProperties = @("-p:ProductVersion=$numericVersion", "-p:OutputName=$msiName", "-p:AppSourceDir=$publishDir")
if ($SkipExtension) {
    Write-Warn 'Extension VS Code ignorée (-SkipExtension).'
}
elseif (-not (Test-Path (Join-Path $extensionDir 'package.json'))) {
    Write-Warn "Aucune extension VS Code trouvée dans $extensionDir : étape ignorée."
}
elseif (-not (Get-Command pnpm -ErrorAction SilentlyContinue)) {
    Write-Warn 'pnpm introuvable : l''extension VS Code ne sera pas incluse (winget install OpenJS.NodeJS.LTS, puis corepack enable pnpm ou winget install pnpm.pnpm).'
}
else {
    Write-Step 'Construction de l''extension VS Code (.vsix)'
    Push-Location $extensionDir
    try {
        $buildStart = Get-Date
        Invoke-Native pnpm @('install', '--frozen-lockfile')
        Invoke-Native pnpm @('run', 'package')
        $vsix = Get-ChildItem -Path $extensionDir -Filter '*.vsix' -Recurse -File |
            Where-Object { $_.FullName -notmatch '\\node_modules\\' -and $_.LastWriteTime -ge $buildStart.AddSeconds(-5) } |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if (-not $vsix) { throw 'pnpm run package n''a produit aucun fichier .vsix.' }
        $manifest = Get-Content (Join-Path $extensionDir 'package.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $wixProperties += @("-p:VsixPath=$($vsix.FullName)", "-p:VsixName=$($vsix.Name)", "-p:ExtensionId=$($manifest.publisher).$($manifest.name)")
        Write-Host "    $($vsix.Name)"
    }
    finally {
        Pop-Location
    }
}

# --- MSI -----------------------------------------------------------------------------------------
Write-Step 'Construction du MSI (WiX)'
Invoke-Native dotnet (@('build', $wixProject, '-c', $Configuration, '-o', $OutputDir, '-nologo') + $wixProperties)

$msi = Join-Path $OutputDir "$msiName.msi"
if (-not (Test-Path $msi)) { throw "Le MSI attendu est introuvable : $msi" }
if ($signTool) {
    Write-Step 'Signature du MSI'
    Invoke-Sign @($msi)
}
$sizeMb = [math]::Round((Get-Item $msi).Length / 1MB, 1)
Write-Host ''
Write-Host "Installateur prêt : $msi ($sizeMb Mo)" -ForegroundColor Green
Write-Host 'Installation : double-clic, ou en silence : msiexec /i <fichier>.msi /qn (ajoutez AUTOLAUNCH=0 pour ne pas démarrer l''application).'
