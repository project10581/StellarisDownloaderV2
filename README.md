# StellarisDownloader V2

StellarisDownloader V2 is a Windows x64 desktop rewrite of StellarisDownloader. It will provide a WPF interface for downloading and maintaining Stellaris Steam Workshop mods without carrying forward the V1 Python/Qt implementation.

## Current status

Batches 0-5 are complete. The Core library provides settings and SQLite cache persistence, library scanning and Windows junction switching, Workshop metadata lookup, SteamCMD installation/process handling, serial download and update flows, and guarded Recycle Bin/permanent deletion. The WPF application provides first-run setup, live English/Simplified Chinese resources, a left-detail/right-list library view, title/ID search, four sort modes, asynchronous preview caching, safe library switching, one application-wide download queue, strict multiline ID/URL input, progress and cancellation, update selection, safe delete-and-requeue behavior, and a restricted WebView2 Workshop browser. Browser card state is derived from the shared queue and verified local files rather than remote metadata. Application update and portable release work begins in Batch 6. The implementation plan and audited V1 feature baseline are available in [`docs/V2_PLAN.md`](docs/V2_PLAN.md) and [`docs/V1_FEATURE_MATRIX.md`](docs/V1_FEATURE_MATRIX.md).

## Requirements

- Windows x64
- .NET 10 SDK
- Microsoft Edge WebView2 Evergreen Runtime for the embedded Workshop browser

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
