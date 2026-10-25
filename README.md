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

## Save and compare mod lists

Open **Mod lists / Compare** in the main window:

1. The window scans your current library on opening and shows it on the right. Click **Save today's mod list** to keep a copy under `%LOCALAPPDATA%\StellarisDownloaderV2\snapshots\`. Every save adds a separate record; previous lists are retained.
2. Choose an older list on the left. Comparison happens automatically: missing mods are pinned at the top of the old list in red, and added mods at the top of the current list in green. Both sides keep their complete lists and original names. **Refresh current list** rescans after changes on disk.
3. The **More** menu contains **Export JSON / TXT copy** and **Compare two list files**. Exports and comparison reports default to `snapshots\exports\` and cannot overwrite saved history. Refresh returns from comparing two files to your current library. You can also copy missing IDs.

Capturing a version or exporting the current library scans the entire selected library on disk, independent of search filters. Cached names are optional and no Workshop connection is needed. Empty numeric folders and directory links are excluded and reported separately. A retained ID means the mod is present in both lists; file contents, versions, launcher enablement, and load order are not compared. Snapshot files contain the source library path and capture time and survive cache rebuilds and application restarts. History is ordered newest first; unreadable files are reported and kept on disk.

## Workshop saved mods and queue status

Hover a Workshop preview to show the blue download plus and a yellow star beside it. The orange queued check and green installed check remain visible without hovering. Click the star to save a mod for later; saved stars stay visible. Saving does not download the mod, add it to the queue, or create an entry in the main library.

The browser's **Saved mods** tab lets you open a saved mod's page, explicitly add it to the download queue, or remove it from saved mods. **Add current mod** and **Save this mod** appear together in the top browser toolbar, available from either sidebar tab. Favorites are local to this application and persist in `%LOCALAPPDATA%\StellarisDownloaderV2\workshop-favorites.json`; they do not change your Steam account favorites. Names are taken from the page, with the Workshop ID as a fallback.

A star shows a pending state until the application confirms that the save reached disk. Closing the browser or application waits for pending favorite writes. Each replacement retains the previous file as `workshop-favorites.json.bak`; it is not automatically restored, so intentional removals stay removed. Browser favorites still restore if the installed-mod lookup fails. To run the optional end-to-end test with an installed WebView2 runtime, set `STELLARIS_WEBVIEW_TEST=1` before `dotnet test`; it uses temporary data and intercepted local pages, without contacting Steam.

The queue displays each verified download result immediately, including success and failure counts. Later downloads, metadata updates, batch errors, and cancellation do not reset completed items to Ready. Metadata/cache finalization still runs after the batch, so the queue can remain busy briefly after all downloads finish.

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
