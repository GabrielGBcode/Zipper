# Builds artifacts\Zipper.msi: self-contained Zipper.exe + MSI, both signed with the "CN=Zipper" code-signing certificate.
# -Version is required: MajorUpgrade refuses a lower version and installs the same version as a second copy.
param([Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object Subject -eq "CN=Zipper" | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=Zipper" -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(5)
}
# public part, used by confiar-certificado.ps1
Export-Certificate -Cert $cert -FilePath "$root\installer\Zipper.cer" | Out-Null

function Sign($path) {
    $result = Set-AuthenticodeSignature -FilePath $path -Certificate $cert -HashAlgorithm SHA256 -TimestampServer "http://timestamp.digicert.com"
    # "UnknownError" only means this machine does not trust the self-signed root yet; the signature is written
    if ($result.Status -notin "Valid", "UnknownError") { throw "Falha ao assinar ${path}: $($result.StatusMessage)" }
}

dotnet publish "$root\src\Zipper" -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:Version=$Version -o "$root\artifacts\publish"
if ($LASTEXITCODE) { throw "dotnet publish falhou" }
Sign "$root\artifacts\publish\Zipper.exe"
Sign "$root\artifacts\publish\ZipperShell.dll"

dotnet build "$root\installer\Zipper.wixproj" -c Release -p:Version=$Version -o "$root\artifacts"
if ($LASTEXITCODE) { throw "build do instalador falhou" }
Sign "$root\artifacts\Zipper.msi"

Write-Host "Pronto: $root\artifacts\Zipper.msi"
