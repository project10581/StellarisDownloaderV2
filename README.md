# StellarisDownloader V2

StellarisDownloader V2 is a Windows x64 desktop rewrite of StellarisDownloader. It will provide a WPF interface for downloading and maintaining Stellaris Steam Workshop mods without carrying forward the V1 Python/Qt implementation.

## Current status

Batch 0 is repository scaffolding only. The application currently contains an empty WPF shell and does not yet download or manage mods. The implementation plan and audited V1 feature baseline are available in [`docs/V2_PLAN.md`](docs/V2_PLAN.md) and [`docs/V1_FEATURE_MATRIX.md`](docs/V1_FEATURE_MATRIX.md).

## Requirements

- Windows x64
- .NET 10 SDK

## Build and test

```powershell
dotnet restore
dotnet format --verify-no-changes
dotnet build -c Release
dotnet test -c Release
```

## Repository structure

```text
src/
  StellarisDownloader.App/    WPF application shell
  StellarisDownloader.Core/   UI-independent core library
tests/
  StellarisDownloader.Tests/  xUnit test project
docs/                         Plan and feature matrix
scripts/                      Future build and release scripts
```

## Project boundaries

- V1 remains a separate, read-only reference repository.
- V2 has no CLI project.
- SteamCMD binaries are not stored in this repository.
- Future writable application data will live under `%LOCALAPPDATA%\StellarisDownloaderV2\`, outside the application directory.
- Release builds are currently unsigned.

## License

No open-source license has been granted for this repository.
