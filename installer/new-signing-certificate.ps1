<#
.SYNOPSIS
    Crée un certificat de signature de code self-signed Authenticode et exporte la clé publique et la clé privée chiffrée.

.DESCRIPTION
    Génère un certificat self-signed avec les attributs Authenticode code-signing (EKU):
    - Algorithme de signature: SHA-256
    - Clé RSA: 4096 bits (robuste, minimum 3072)
    - Validité: 365 jours par défaut (1 an, raisonnable pour une clé interne)
    - EKU (Enhanced Key Usage): Code Signing (1.3.6.1.5.5.7.3.3)
    - Outil: Windows certificate stores (PowerShell cmdlets)

    Exporte:
    - .cer (clé publique seule, partageable par canal sécurisé hors-bande)
    - .pfx (clé privée chiffrée, à conserver secrètement)

    Le mot de passe du PFX est demandé en SecureString interactif (jamais en clair ni en paramètre). La clé privée ne quitte le
    certificat que chiffrée dans le PFX. Le certificat temporaire est nettoyé du magasin Windows une fois les exports
    terminés.

    VALIDATION :
    - Les fichiers .cer et .pfx existants ne seront jamais écrasés (fail-fast).
    - ValidityDays doit être entre 1 et 730 jours (par défaut 365).
    - KeySize doit être >= 3072 bits (par défaut 4096).
    - SubjectName, OutputDirectory et CertificateName ne peuvent pas être vides.

    VÉRIFICATION :
    - L'empreinte à vérifier par canal indépendant est le SHA-256 du fichier .cer exporté.
    - Calculez-la: Get-FileHash -Path code-signing.cer -Algorithm SHA256 | Select-Object -ExpandProperty Hash

.PARAMETER SubjectName
    Sujet X.500 du certificat (ex. "CN=Eric De Carufel, O=Time Sheet Helper, C=CA").
    Défaut: "CN=Code Signing, O=Time Sheet Helper, C=CA"

.PARAMETER OutputDirectory
    Dossier de sortie pour les fichiers .cer et .pfx.
    Défaut: $env:LOCALAPPDATA\TimeSheetHelper\signing

.PARAMETER CertificateName
    Nom des fichiers exportés (sans extension; .cer et .pfx seront ajoutés).
    Défaut: "code-signing"

.PARAMETER ValidityDays
    Nombre de jours de validité du certificat (1-730).
    Défaut: 365 (1 an)

.PARAMETER KeySize
    Taille de la clé RSA en bits (minimum 3072).
    Défaut: 4096

.EXAMPLE
    pwsh ./installer/new-signing-certificate.ps1
    # Crée code-signing.cer et code-signing.pfx dans %LOCALAPPDATA%\TimeSheetHelper\signing
    # Demande le mot de passe du PFX en SecureString interactif

.EXAMPLE
    pwsh ./installer/new-signing-certificate.ps1 -OutputDirectory C:\certs -CertificateName "my-app-signer"
    # Crée my-app-signer.cer et my-app-signer.pfx dans C:\certs

.NOTES
    - Le mot de passe est JAMAIS passé en paramètre ni en ligne de commande; lecture interactive SecureString une seule fois.
    - La SecureString est passée directement à Export-PfxCertificate, jamais convertie en chaîne.
    - Le certificat temporaire du magasin est nettoyé même en cas d'erreur (try/finally).
    - Les fichiers existants ne sont jamais écrasés; le script s'arrête si détecte une collision.
    - En cas d'erreur lors du nettoyage du magasin, l'exception est remontée.
    - Le .cer peut être partagé via un canal sécurisé hors-bande distinct du téléchargement du .pfx.
    - L'empreinte SHA-256 du fichier .cer doit être vérifiée par un canal indépendant (IRC privé, appel, message direct).
    - Self-signed n'améliore pas la réputation SmartScreen; certificat OV/EV ou Azure Trusted Signing est préférable pour le public.

.LINK
    https://learn.microsoft.com/en-us/powershell/module/pki/new-selfsignedcertificate
    https://learn.microsoft.com/en-us/powershell/module/pki/export-certificate
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Interactive console script with colored output.')]
[CmdletBinding()]
param(
    [string]$SubjectName = "CN=Code Signing, O=Time Sheet Helper, C=CA",
    [string]$OutputDirectory = (Join-Path $env:LOCALAPPDATA "TimeSheetHelper\signing"),
    [string]$CertificateName = "code-signing",
    [int]$ValidityDays = 365,
    [int]$KeySize = 4096
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Step([string]$Message) {
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-Success([string]$Message) {
    Write-Host "✓ $Message" -ForegroundColor Green
}

function Write-Info([string]$Message) {
    Write-Host "ℹ $Message" -ForegroundColor Gray
}

function Write-Warn([string]$Message) {
    Write-Host "⚠ $Message" -ForegroundColor Yellow
}

function Write-Error-Custom([string]$Message) {
    Write-Host "✗ $Message" -ForegroundColor Red
}

# Validation des paramètres
Write-Step "Validation des paramètres"

if ([string]::IsNullOrWhiteSpace($SubjectName)) {
    throw "SubjectName ne peut pas être vide."
}
Write-Success "SubjectName: $SubjectName"

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    throw "OutputDirectory ne peut pas être vide."
}
Write-Success "OutputDirectory: $OutputDirectory"

if ([string]::IsNullOrWhiteSpace($CertificateName)) {
    throw "CertificateName ne peut pas être vide."
}

# Valider que CertificateName ne contient pas de caractères invalides
if ($CertificateName -match '[<>:"/\\|?*]') {
    throw "CertificateName contient des caractères invalides pour un nom de fichier."
}
Write-Success "CertificateName: $CertificateName"

# Valider ValidityDays
if ($ValidityDays -le 0 -or $ValidityDays -gt 730) {
    throw "ValidityDays doit être entre 1 et 730 jours (valeur reçue: $ValidityDays)."
}
Write-Success "ValidityDays: $ValidityDays"

# Valider KeySize
if ($KeySize -lt 3072) {
    throw "KeySize doit être au minimum 3072 bits (valeur reçue: $KeySize)."
}
Write-Success "KeySize: $KeySize bits"

# Vérifier que le dossier de sortie n'existe pas ou peut être créé
if (-not (Test-Path $OutputDirectory)) {
    Write-Step "Création du dossier de sortie"
    New-Item -ItemType Directory -Path $OutputDirectory -Force -ErrorAction Stop | Out-Null
    Write-Success "Dossier créé: $OutputDirectory"
} else {
    Write-Info "Le dossier existe déjà: $OutputDirectory"
}

# Vérifier que les fichiers n'existent pas (fail-fast)
Write-Step "Vérification des collisions de fichiers"
$cerPath = Join-Path $OutputDirectory "$CertificateName.cer"
$pfxPath = Join-Path $OutputDirectory "$CertificateName.pfx"

if (Test-Path $cerPath) {
    throw "Le fichier $cerPath existe déjà. Supprimez-le ou utilisez un autre CertificateName."
}
if (Test-Path $pfxPath) {
    throw "Le fichier $pfxPath existe déjà. Supprimez-le ou utilisez un autre CertificateName."
}
Write-Success "Aucune collision détectée."

# Demander le mot de passe en SecureString interactif (une seule fois)
Write-Step "Mot de passe du certificat (PFX)"
Write-Info "Entrez un mot de passe robuste (ne sera jamais affiché ni enregistré):"
$passwordSecure = Read-Host -AsSecureString -Prompt "Mot de passe"

# Créer le certificat self-signed
$cert = $null
try {
    Write-Step "Création du certificat self-signed Authenticode"

    $cert = New-SelfSignedCertificate `
        -Subject $SubjectName `
        -KeyUsage DigitalSignature `
        -KeySpec Signature `
        -KeyAlgorithm RSA `
        -KeyLength $KeySize `
        -HashAlgorithm SHA256 `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3") `
        -NotAfter (Get-Date).AddDays($ValidityDays) `
        -CertStoreLocation Cert:\CurrentUser\My

    Write-Success "Certificat créé"

    # Exporter le certificat public (.cer)
    Write-Step "Export de la clé publique (.cer)"
    Export-Certificate -Cert $cert -FilePath $cerPath -Type CERT | Out-Null
    Write-Success "Fichier créé: $cerPath"

    # Calculer l'empreinte SHA-256 du fichier .cer exporté
    $cerHash = (Get-FileHash -Path $cerPath -Algorithm SHA256 -ErrorAction Stop).Hash
    Write-Info "Empreinte SHA-256 du fichier .cer: $cerHash"

    # Exporter le certificat privé (.pfx)
    Write-Step "Export de la clé privée chiffrée (.pfx)"
    Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $passwordSecure | Out-Null
    Write-Success "Fichier créé: $pfxPath"

    # Afficher les empreintes et conseils
    Write-Host ""
    Write-Step "Résumé"
    Write-Info "Sujet: $SubjectName"
    Write-Info "Validité jusqu'au: $($cert.NotAfter)"
    Write-Info "Empreinte SHA-256 du .cer: $cerHash"
    Write-Host ""
    Write-Info "Fichiers générés:"
    Write-Info "  .cer (public): $cerPath"
    Write-Info "  .pfx (privé): $pfxPath"
    Write-Host ""
    Write-Warn "Recommandations de sécurité:"
    Write-Warn "  • Vérifiez l'empreinte SHA-256 du .cer par un canal indépendant (IRC privé, appel, message direct)."
    Write-Warn "  • Commande pour recalculer l'empreinte localement:"
    Write-Warn "    Get-FileHash -Path `"$cerPath`" -Algorithm SHA256 | Select-Object -ExpandProperty Hash"
    Write-Warn "  • Le .cer peut être partagé via un canal sécurisé hors-bande distinct du .pfx."
    Write-Warn "  • Installer le .cer accordera confiance à TOUS les binaires signés avec cette clé."
    Write-Warn "  • Le .pfx JAMAIS versionnné, commité, ni partagé avec le dépôt ou publiquement."
    Write-Warn "  • build-msi.ps1 vérifie les signatures avec signtool /pa; self-signed peut échouer la vérification locale."
    Write-Warn "  • Ce certificat convient aux tests et déploiements contrôlés uniquement."
    Write-Warn "  • Pour usage public, un certificat CA (OV/EV) ou Azure Trusted Signing est fortement recommandé."
    Write-Host ""
    Write-Info "Utilisation du .pfx:"
    Write-Info "  `$env:SIGNING_CERT_PASSWORD = '<mot de passe>'"
    Write-Info "  pwsh ./installer/build-msi.ps1 -CertificatePath '$pfxPath'"
    Write-Host ""

} finally {
    # Nettoyer le certificat du magasin même en cas d'erreur
    if ($cert) {
        Write-Step "Nettoyage du magasin Windows"
        try {
            Get-ChildItem -Path "Cert:\CurrentUser\My" | Where-Object { $_.Subject -eq $cert.Subject -and $_.Thumbprint -eq $cert.Thumbprint } | Remove-Item -Force -ErrorAction Stop
            Write-Success "Certificat supprimé du magasin (seuls les exports .cer et .pfx demeurent)."
        } catch {
            Write-Error-Custom "Impossible de supprimer le certificat du magasin: $_"
            throw
        }
    }

    # Nettoyer la SecureString
    if ($passwordSecure) {
        $passwordSecure.Dispose()
    }
}

Write-Host ""
Write-Success "Terminé."