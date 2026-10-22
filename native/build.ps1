# Builds src\Zipper\tools\ZipperShell.dll (Explorer menu for .zip/.7z) with MinGW-w64 and runs its self-check.
# The DLL is kept in the repo like 7zr.exe, so building Zipper or the installer does not need a C++ compiler.
# MinGW: winget install BrechtSanders.WinLibs.POSIX.UCRT
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$gpp = (Get-Command g++ -ErrorAction SilentlyContinue).Source
if (-not $gpp) {
    $gpp = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Recurse -Filter g++.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $gpp) { throw "g++ não encontrado (instale com: winget install BrechtSanders.WinLibs.POSIX.UCRT)" }

$out = "$here\..\src\Zipper\tools\ZipperShell.dll"
# fully static: Explorer must not need MinGW's runtime DLLs
& $gpp -shared -O2 -s -static -municode -o $out "$here\ZipperShell.cpp" "$here\ZipperShell.def" -lole32 -lshell32 -lshlwapi -luuid -lgdi32
if ($LASTEXITCODE) { throw "falha ao compilar ZipperShell.dll" }

$test = Join-Path ([IO.Path]::GetTempPath()) "ZipperShellTest"
New-Item -ItemType Directory -Force $test | Out-Null
& $gpp -O2 -static -municode -o "$test\test.exe" "$here\test.cpp" -lole32 -lshell32 -luuid
if ($LASTEXITCODE) { throw "falha ao compilar o teste" }
& "$test\test.exe" (Resolve-Path $out) $test
if ($LASTEXITCODE) { throw "teste da ZipperShell.dll falhou" }
Write-Host "Pronto: $out"
