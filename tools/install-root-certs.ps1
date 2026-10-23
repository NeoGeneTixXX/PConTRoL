<#
  PConTRoL: fehlende Stammzertifikate für die Verbindung zu GitHub einspielen.

  Hintergrund: Auf Rechnern ohne Windows Update (z. B. Leitrechner an der Maschine) scheitert die
  Update-Prüfung mit "PartialChain". GitHub benutzt Zertifikate, die bei diesen beiden Stammzertifikaten enden:
    - USERTrust ECC Certification Authority  (github.com, api.github.com)
    - ISRG Root X1 (Let's Encrypt)           (Downloads der Release-Dateien)

  Ausführen: Rechtsklick auf PowerShell -> "Als Administrator ausführen", dann
      powershell -ExecutionPolicy Bypass -File "<Pfad>\install-root-certs.ps1"

  Das Skript installiert ein Zertifikat NUR, wenn sein Fingerabdruck (SHA-1) exakt stimmt.
  Es nimmt die Dateien aus dem Unterordner "certs" neben dem Skript; fehlen sie, lädt es sie von den
  offiziellen Adressen der Herausgeber (Sectigo bzw. Let's Encrypt).
#>
#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'

$certs = @(
    @{ Name = 'USERTrust ECC Certification Authority'; File = 'USERTrust-ECC-Certification-Authority.cer'
       Url = 'http://crt.usertrust.com/USERTrustECCCertificationAuthority.crt'; Thumbprint = 'D1CBCA5DB2D52A7F693B674DE5F05A1D0C957DF0' },
    @{ Name = 'ISRG Root X1'; File = 'ISRG-Root-X1.cer'
       Url = 'http://x1.i.lencr.org/'; Thumbprint = 'CABD2A79A1076A31F21D253635CB039D4329A5E8' }
)

foreach ($c in $certs) {
    if (Test-Path "Cert:\LocalMachine\Root\$($c.Thumbprint)") {
        Write-Host "$($c.Name): schon installiert." -ForegroundColor Green
        continue
    }

    $file = Join-Path $PSScriptRoot "certs\$($c.File)"
    if (-not (Test-Path $file)) {
        $file = Join-Path $env:TEMP $c.File
        Write-Host "$($c.Name): wird von $($c.Url) geladen ..."
        Invoke-WebRequest -Uri $c.Url -OutFile $file -UseBasicParsing
    }

    $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $file
    if ($cert.Thumbprint -ne $c.Thumbprint) {
        Write-Host "$($c.Name): Fingerabdruck stimmt NICHT ($($cert.Thumbprint)) - wird nicht installiert." -ForegroundColor Red
        continue
    }

    Import-Certificate -FilePath $file -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
    Write-Host "$($c.Name): installiert." -ForegroundColor Green
}

Write-Host ''
Write-Host 'Test der Verbindung zu GitHub ...'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
foreach ($url in 'https://api.github.com', 'https://github.com/NeoGeneTixXX/PConTRoL/releases/latest/download/releases.win.json') {
    try {
        Invoke-WebRequest -Uri $url -UseBasicParsing | Out-Null
        Write-Host "OK:     $url" -ForegroundColor Green
    }
    catch {
        Write-Host "FEHLER: $url - $($_.Exception.Message)" -ForegroundColor Red
    }
}
