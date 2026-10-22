# Makes this Windows user trust the self-signed "CN=Zipper" certificate, so Zipper.msi/Zipper.exe show a verified publisher.
# Run it yourself: Windows asks for confirmation before adding a root certificate.
$cer = Join-Path $PSScriptRoot "Zipper.cer"
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\CurrentUser\Root | Out-Null
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\CurrentUser\TrustedPublisher | Out-Null
Write-Host "Certificado do Zipper confiável para este usuário."
