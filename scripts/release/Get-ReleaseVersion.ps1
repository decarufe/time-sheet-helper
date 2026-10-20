<#
.SYNOPSIS
  Calcule la prochaine version semantique (vX.Y.Z) a partir des tags git et des commits.
.DESCRIPTION
  - Reference : plus haute version stable (tag vX.Y.Z) atteignable depuis -Ref. Aucun tag => 0.1.0.
  - Suggestion (-Bump auto) : changement incompatible => major (minor tant que major = 0),
    feat => minor, sinon patch.
  - -Override force un numero (X.Y.Z[-prerelease]) ; il doit etre superieur a la reference
    et le tag ne doit pas deja exister.
  Ecrit les sorties dans $GITHUB_OUTPUT et un resume dans $GITHUB_STEP_SUMMARY quand ils existent.
#>
[CmdletBinding()]
param(
    [ValidateSet('auto', 'major', 'minor', 'patch')]
    [string]$Bump = 'auto',
    [string]$Override = '',
    [string]$Ref = 'HEAD'
)

$ErrorActionPreference = 'Stop'

$semverPattern = '^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-(?<pre>(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*))?$'

function Invoke-Git {
    $output = & git @args
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') a echoue (code $LASTEXITCODE)." }
    $output
}

function Compare-SemVer([string]$a, [string]$b) {
    $ma = [regex]::Match($a, $semverPattern)
    $mb = [regex]::Match($b, $semverPattern)
    foreach ($part in 'major', 'minor', 'patch') {
        $d = [int64]$ma.Groups[$part].Value - [int64]$mb.Groups[$part].Value
        if ($d -ne 0) { return [math]::Sign($d) }
    }
    $pa = $ma.Groups['pre'].Value
    $pb = $mb.Groups['pre'].Value
    if ($pa -eq $pb) { return 0 }
    if (-not $pa) { return 1 }
    if (-not $pb) { return -1 }
    $ia = $pa.Split('.')
    $ib = $pb.Split('.')
    for ($i = 0; $i -lt [math]::Max($ia.Count, $ib.Count); $i++) {
        if ($i -ge $ia.Count) { return -1 }
        if ($i -ge $ib.Count) { return 1 }
        $na = $ia[$i] -match '^\d+$'
        $nb = $ib[$i] -match '^\d+$'
        if ($na -and $nb) {
            $d = [int64]$ia[$i] - [int64]$ib[$i]
            if ($d -ne 0) { return [math]::Sign($d) }
        }
        elseif ($na) { return -1 }
        elseif ($nb) { return 1 }
        else {
            $c = [string]::CompareOrdinal($ia[$i], $ib[$i])
            if ($c -ne 0) { return [math]::Sign($c) }
        }
    }
    return 0
}

$stableTags = @(Invoke-Git tag --list 'v*' --merged $Ref) | Where-Object { $_ -match '^v\d+\.\d+\.\d+$' }
$baseline = $null
foreach ($t in $stableTags) {
    $v = $t.Substring(1)
    if (-not $baseline -or (Compare-SemVer $v $baseline) -gt 0) { $baseline = $v }
}
$previousTag = if ($baseline) { "v$baseline" } else { '' }

$range = if ($previousTag) { "$previousTag..$Ref" } else { $Ref }
$subjects = @(Invoke-Git log --no-merges --format='%s' $range)
$commitCount = $subjects.Count

if ($previousTag -and $commitCount -eq 0) {
    throw "Aucun commit depuis $previousTag : rien a publier."
}

$bodies = (Invoke-Git log --no-merges --format='%B' $range) -join "`n"

$detected = 'patch'
if ($subjects | Where-Object { $_ -match '^\w+(\([^)]*\))?!:' }) { $detected = 'major' }
elseif ($bodies -match '(?m)^BREAKING[ -]CHANGE:') { $detected = 'major' }
elseif ($subjects | Where-Object { $_ -match '^feat(\([^)]*\))?:' }) { $detected = 'minor' }

function Get-Bumped([string]$from, [string]$kind) {
    $m = [regex]::Match($from, $semverPattern)
    $maj = [int]$m.Groups['major'].Value
    $min = [int]$m.Groups['minor'].Value
    $pat = [int]$m.Groups['patch'].Value
    if ($kind -eq 'major' -and $maj -eq 0) { $kind = 'minor' }
    switch ($kind) {
        'major' { "$($maj + 1).0.0" }
        'minor' { "$maj.$($min + 1).0" }
        default { "$maj.$min.$($pat + 1)" }
    }
}

$bumpUsed = if ($Bump -eq 'auto') { $detected } else { $Bump }
$suggested = if ($baseline) { Get-Bumped $baseline $bumpUsed } else { '0.1.0' }

$Override = $Override.Trim()
if ($Override) {
    $version = $Override -replace '^[vV]', ''
    if ($version -notmatch $semverPattern) {
        throw "Version '$Override' invalide : format attendu X.Y.Z ou X.Y.Z-prerelease (ex. 1.4.0, 2.0.0-rc.1)."
    }
    if ($baseline -and (Compare-SemVer $version $baseline) -le 0) {
        throw "La version $version doit etre superieure a la derniere version publiee ($baseline)."
    }
}
else {
    $version = $suggested
}

$tag = "v$version"
if (@(Invoke-Git tag --list $tag).Count -gt 0) { throw "Le tag $tag existe deja localement." }
$remote = & git ls-remote --tags origin "refs/tags/$tag" 2>$null
if ($LASTEXITCODE -eq 0 -and $remote) { throw "Le tag $tag existe deja sur origin." }

$prerelease = if ($version.Contains('-')) { 'true' } else { 'false' }
$forced = if ($Override) { 'oui' } else { 'non' }

$outputs = [ordered]@{
    version       = $version
    tag           = $tag
    previous_tag  = $previousTag
    suggested     = $suggested
    bump          = $bumpUsed
    prerelease    = $prerelease
    commit_count  = "$commitCount"
}
foreach ($k in $outputs.Keys) { Write-Host "$k=$($outputs[$k])" }

if ($env:GITHUB_OUTPUT) {
    $outputs.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" } | Add-Content -Path $env:GITHUB_OUTPUT -Encoding utf8
}

if ($env:GITHUB_STEP_SUMMARY) {
    @(
        '### Version',
        '',
        "- Version publiee : **$version** (tag ``$tag``)",
        "- Suggestion : $suggested (increment $bumpUsed, $commitCount commit(s) depuis $(if ($previousTag) { $previousTag } else { 'le debut' }))",
        "- Version forcee : $forced"
    ) | Add-Content -Path $env:GITHUB_STEP_SUMMARY -Encoding utf8
}
