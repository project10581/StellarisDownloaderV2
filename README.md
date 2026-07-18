# StellarisDownloader V2

StellarisDownloader V2 is a Windows x64 desktop rewrite of StellarisDownloader. It will provide a WPF interface for downloading and maintaining Stellaris Steam Workshop mods without carrying forward the V1 Python/Qt implementation.

## Current status

Batches 0-6 are complete. The Core library provides settings and SQLite cache persistence, library scanning and Windows junction switching, Workshop metadata lookup, SteamCMD installation/process handling, serial download and update flows, guarded Recycle Bin/permanent deletion, and Velopack application updates. The WPF application provides first-run setup, live English/Simplified Chinese resources, a left-detail/right-list library view, title/ID search, four sort modes, asynchronous preview caching, safe library switching, one application-wide download queue, strict multiline ID/URL input, progress and cancellation, update selection, safe delete-and-requeue behavior, a restricted WebView2 Workshop browser, and manual or configured startup application-update checks. Browser card state is derived from the shared queue and verified local files rather than remote metadata. Serilog writes bounded rolling logs under Local AppData. The implementation plan and audited V1 feature baseline are available in [`docs/V2_PLAN.md`](docs/V2_PLAN.md) and [`docs/V1_FEATURE_MATRIX.md`](docs/V1_FEATURE_MATRIX.md).

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

## Build a portable package

Run the packaging entry point with a Semantic Version 2.0.0 value:

```powershell
./scripts/package.ps1 -Version 0.1.0-dev.1
```

The script publishes a self-contained `win-x64` application with single-file publishing and trimming disabled, then uses the pinned Velopack 1.2.0 tool to create `artifacts/releases/`. It validates the Portable ZIP, full update package and Windows feed JSON, and rejects installer output, SteamCMD binaries, settings or the SQLite database. A first package has no delta; later packaging can generate deltas when prior release packages are supplied.

The manual `Package portable release` Actions workflow runs the normal CI gates and uploads the Portable ZIP, nupkg files and update feeds as a development artifact. It does not create a GitHub Release. Code signing is deliberately disabled until a certificate is configured, so current packages are unsigned.

## Repository structure

```text
src/
  StellarisDownloader.App/    WPF application shell
  StellarisDownloader.Core/   UI-independent core library
tests/
  StellarisDownloader.Tests/  xUnit test project
docs/                         Plan and feature matrix
scripts/                      Portable packaging entry point
```

## Project boundaries

- V1 remains a separate, read-only reference repository.
- V2 has no CLI project.
- SteamCMD binaries are not stored in this repository.
- Writable application data lives under `%LOCALAPPDATA%\StellarisDownloaderV2\`, outside the application directory.
- Release builds are currently unsigned.
- Development builds run without contacting an update source unless the user explicitly requests a check; Velopack updates are available from packaged builds.

## License

No open-source license has been granted for this repository.
