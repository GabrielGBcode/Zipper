# Zipper

A WinRAR-style file archiver for Windows, written in C# (.NET 10, Windows Forms). The interface is in Portuguese.

## Features

- **ZIP and 7z**: create, open, add, delete, test and extract archives. ZIP uses SharpZipLib; 7z uses the bundled `7zr.exe`.
- **Passwords**: encrypted ZIP and 7z (including encrypted file names in 7z). A wrong password never damages an existing archive.
- **Add options**: compression level, exclude and store-only masks, update only newer files, archive comment, filters by archive attribute and date, NTFS timestamps, separate archive per item, name masks with date tokens, solid 7z and volumes.
- **Progress and cancel**: cancelling keeps the original ZIP intact and removes a partial 7z.
- **Safe extraction**: entries that would land outside the destination folder (zip-slip) are rejected.
- **Explorer integration**: a native C++ COM shell extension (`native/ZipperShell.cpp`) adds a "Zipper" context menu for `.zip`/`.7z` and right-drag extraction onto folders.
- **Installer**: MSI built with WiX (`installer/`), with a signed executable.

## Build and test

Requires the .NET 10 SDK.

```bash
dotnet build Zipper.slnx
dotnet test Zipper.slnx
```

The tests (`tests/Zipper.Tests`, xUnit) cover password round trips, wrong-password safety, zip-slip rejection, add options, progress and cancellation for ZIP and 7z.

Optional:

- `native/build.ps1` rebuilds `ZipperShell.dll` with MinGW-w64. The prebuilt DLL is kept in `src/Zipper/tools/`, so a C++ compiler is not needed to build the app.
- `build-installer.ps1 -Version 1.0.0` builds `artifacts/Zipper.msi` (needs WiX). It signs with a self-signed "CN=Zipper" certificate; `installer/confiar-certificado.ps1` trusts it on a machine.

## Third-party components

See [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt): 7-Zip `7zr.exe` (LGPL 2.1) and SharpZipLib (MIT).
