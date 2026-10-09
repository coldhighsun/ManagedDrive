# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```powershell
# Build all projects
dotnet build

# Run the app (Release avoids debug-build overhead)
dotnet run --project src/ManagedDrive.App -c Release

# Run tests
dotnet test tests/ManagedDrive.Tests

# Run a single test class
dotnet test tests/ManagedDrive.Tests --filter "FullyQualifiedName~FileNodeTests"

# Run the mdrive CLI against an already-running ManagedDrive.exe
dotnet run --project src/ManagedDrive.Cli -- list
```

The solution file is `ManagedDrive.slnx` (Visual Studio 2022+ format).

**WinFsp prerequisite:** `winfsp-msil.dll` must be present at `C:\Program Files (x86)\WinFsp\bin\`. Install exactly [WinFsp 2.2.26215 (2026 Beta4)](https://github.com/winfsp/winfsp/releases/tag/v2.2B4) before building or running — download the MSI directly; do not use `winget install WinFsp.WinFsp`, as the winget package lags behind this release.

## Architecture

Nine projects, all inheriting `net10.0-windows` from `Directory.Build.props`:

- **`ManagedDrive.Core`** — Pure file-system engine, no UI. Sub-namespaces: `FileSystem`, `Mounting`, `Persistence`, `Snapshots`, `Archive`, `DiskCreation`, `Diagnostics`. No types at the bare `ManagedDrive.Core` namespace.
- **`ManagedDrive.App`** — WPF + WinForms (`UseWindowsForms=true` for tray icon) desktop app. References Core, Cli.Core, HelperProtocol.
- **`ManagedDrive.Cli.Core`** — Shared CLI parsing/protocol (System.CommandLine + Spectre.Console).
- **`ManagedDrive.Cli`** — `mdrive.exe`, thin client forwarding commands to the running app via named pipe.
- **`ManagedDrive.HelperProtocol`** — Dependency-free named-pipe protocol between app and SYSTEM helper service.
- **`ManagedDrive.Service`** — Optional LocalSystem Windows service for cross-session drive-letter visibility.
- **`ManagedDrive.WingetExtension`** — `wingetx.exe`, standalone `winget` wrapper (no dependency on other projects).
- **`ManagedDrive.Tests`** — xUnit v3 unit tests (pure-managed, no WinFsp driver needed).
- **`ManagedDrive.Benchmarks`** — BenchmarkDotNet comparisons; part of `.slnx` but not shipped.

Data flow: `MountManager` → `RamDisk.Create()` → `MemoryFileSystem` + WinFsp `FileSystemHost`.

### Key conventions

- `Directory.Build.props` sets `Nullable enable`, `ImplicitUsings enable`, `UseArtifactsOutput` (builds go to `artifacts/`, not per-project `bin/`).
- `GlobalUsings.cs` (App and Core each have one) covers all sub-namespaces + common BCL namespaces. Don't re-add `using` for these; only add file-specific ones.
- The implicit `System.Windows.Forms` using is **removed** in `ManagedDrive.App.csproj` — use fully qualified names for WinForms types.
- Central Package Management: versions live in `Directory.Packages.props` only. Never add `Version=` to `<PackageReference>` in `.csproj`.
- `MinVer` derives version from git tags (`v`-prefixed). Tests set `<MinVerSkip>true</MinVerSkip>`.

### Disk image format (`.mdr`) and compression

Binary format: magic `MDRD`. Capacity and volume label are always plaintext header fields; only the node region is compressed and optionally encrypted. Versions 1-5 (gzip, then Zstd) store the node region as one continuous compressed/encrypted stream and are read-only now — `RamDisk.SaveToImage` always writes version 7 (version 6's segments plus a per-node reparse buffer for symbolic links/junctions; version 6 images stay readable and are rewritten in full on their first save) via `DiskImageSerializer.SaveIncremental(...)`, which splits the node region into independently compressed/encrypted *segments* and reuses the on-disk bytes of any segment whose member nodes are all unchanged since the last save (tracked per-node via `FileNode.SavedContentVersion`/`SavedMetadataVersion`/`SavedSegmentIndex`) instead of recompressing/re-encrypting the whole disk every save, falling back to a full rewrite when there's no compatible existing image to reuse (first save, or the existing image predates version 6). `DiskImageSerializer.Save(...)` (plain, non-incremental) writes version 5 — `ExportToImage` (standalone `.mdr` export, independent of a mounted disk's own persistence) uses this, since that path has no notion of incremental reuse across calls and there's no reason to pay the segmentation overhead — unless the disk holds symbolic links/junctions, which version 5 has no field for: then it writes version 7 with the continuous layout instead. Version 7's header has one extra byte after IsEncrypted selecting the node-region layout (1 = segmented, 0 = continuous, i.e. version 5's layout plus the reparse field); header parsers must skip it. `SaveIncremental`'s oversized-node fallback to `Save` therefore also keeps links. Snapshots carry them from snapshot index version 2 on.

**Compression uses Zstd** (via `ZstdSharp.Port`) with parallel chunked encoding (`ParallelZstd`). `ImageCompressionLevel` enum (`None=0`/`Fastest=1`/`Optimal=2`/`SmallestSize=3`) has stable explicit values persisted to disk/JSON — do not renumber. `DiskOptions.CustomZstdLevel` (`int?`) overrides the preset's Zstd level (1-22) when set. Legacy gzip-compressed v1/v2 images are still readable — do not remove those `Load()` branches.

Encryption: AES-256-GCM envelope encryption (random CEK wrapped by user password via PBKDF2). The wrapped-CEK material is plaintext header; node region (v1-5) or each segment independently (v6) is encrypted under the CEK. Reusing a v6 segment verbatim never touches its ciphertext or nonce/tag — only rewritten segments get a fresh nonce.

Snapshots use a separate format (magic `MDRS`) with content-addressed blob store — don't conflate with `DiskImageSerializer`.

Alternate data streams (`file:Zone.Identifier`, `RamDisk.ConfigureHost` sets `NamedStreams`) are ordinary `FileNode`s keyed `\path:stream` (`AlternateStreamName` normalizes the `::$DATA`/`:s:$DATA` forms WinFsp may pass; a colon never occurs in a real file name, so any key containing one is a stream). That is why images and snapshots needed no format change. `FileNodeMap` keeps streams out of directory listings and moves/removes them together with their file; `MemoryFileSystem.GetStreamEntry` reports them. Everything that loads a node map (image, snapshot) must drop streams without a file via `FileNodeMap.RemoveInvalidStreams()`; archive import skips entries whose name has a colon and archive export skips streams. Hard links are not possible: WinFsp has no link callback.

Integrity/hardening notes: a v6 segment's plaintext is verified against its index `ContentHash` on load, and a snapshot blob against the SHA-256 in its file name; the size fields most able to force a large allocation (Zstd/GCM chunk lengths, security-descriptor length, image node sizes bounded by the header capacity, snapshot entry sizes bounded by the snapshot's capacity) are range-checked before anything is allocated, but not all are: archive entry sizes are only compared with available physical memory, snapshot blob sizes and the v6 segment index's `PayloadLength`/`NodeCount` are not bounded by capacity, and the legacy v3 whole-blob ciphertext is still read into one array sized from the stream length. PBKDF2 iterations live in the header (currently 600,000; images written with 210,000 still load). Known limitations kept for format compatibility: the MDRS snapshot index (paths, sizes, hashes) and blob file names (plaintext SHA-256) are not encrypted even for an encrypted disk, and v6 segments carry no AAD binding them to their position, so an attacker with write access could roll back, reorder or drop whole segments. Fixing either needs a format version bump.

### Threading model

- `MountManager` and `RamDisk._autoSaveLock` use the C# 13 `Lock` type. `FileNodeMap` uses a `ReaderWriterLockSlim` (`_syncRoot`): lookups/enumerations take the read lock, structural mutations the write lock.
- WinFsp callbacks fire on driver threads; state access through `FileNodeMap`'s lock.
- Auto-save timer: periodic path uses `Lock.TryEnter()` (skip if busy); `Dispose()` uses blocking `lock` (wait then final save).
- `FileNodeMap.GetTotalAllocated()` is O(1) via incremental `_totalAllocated`. Only mutate `AllocationSize` through `UpdateAllocationSize()` — direct assignment drifts the cached total.
- The low-memory guard (`MemoryHeadroomBudget`) charges memory actually materialized, not allocation-size growth (growth is sparse: a chunk gets a backing array only when written). New paths that allocate content must go through `FileContent.TryWriteFrom`/`TryResize`/`FillFromStream`, or charge a precomputed cost (e.g. `CloneCost`) first — calling `WriteFrom`/`Resize` directly bypasses the guard.

### App layer patterns

- Standard WPF MVVM. `App.xaml.cs` orchestrates startup/shutdown; specific concerns delegated to `Services/` classes (`TrayIconController`, `TrayTooltipController`, `DiskNotificationService`, `TempDirCompatChecker`, `SessionEndingSaveHandler`, `UpdateCheckService`, `GlobalMountCoordinator`, `ShellContextMenuManager`).
- Disk operations (`Mount`/`Unmount`/`Save`/`Dispose`) dispatched via `Task.Run` to keep UI responsive.
- Startup splash: `App_Startup` checks WinFsp, then shows `SplashWindow` before `StartUiAsync` (also with `StartMinimized`; `ShowActivated=False` so it does not take focus; on top only for `SplashTopmostState.StayOnTopFor`, never over a dialog from the tray (`ShowMainWindow` releases it); it has a taskbar button so a buried splash can be brought back, except when starting minimized) and keeps the main window hidden while `AutoMountDisksAsync` loads the disks (progress and "Loading disk x of y" go to the splash instead of the busy overlay). `CloseSplashAndShowMainWindowAsync` (ordering in the pure, tested `SplashLifecycle`: at least `SplashPolicy.MinimumDisplay`, then close) closes it and shows the main window unless `StartMinimized`; it also runs early when a disk needs a password, so the prompt isn't hidden behind the splash, and the busy overlay takes over. The splash also has random background circles (`SplashAnimationPlanner`) and a scrolling feature list (`Splash.Feature{n}` strings, `SplashTicker`; add a feature by adding the next number in both language files). `ShowSplashAsync` switches `ShutdownMode` to `OnExplicitShutdown` only while the splash is the sole window (`CloseSplashAndShowMainWindowAsync` restores `OnLastWindowClose`), and `StartUi` sets `MainWindow` explicitly (the splash is the first window created) — keep both.
- Disk presets: `DiskPreset`/`BuiltInPresets`/`PresetComposer`/`EnvRedirectPolicy` (`Core/DiskCreation`, pure) describe combinable templates; a disk card's context-menu "Presets" submenu (filled in `MainWindow.DiskContextMenu_Opened`) ticks/unticks presets live through `MainViewModel.GetActivePresetIds`/`SetPresetAsync`; the edit dialog ticks the presets a disk already has (`PresetSelection.Detect`), where for variable-redirecting presets the options are first reconciled with what the user's environment really points at (`MainViewModel.WithEnvironmentPresets` -> `PresetSelection.Reconcile`/`UserEnvironmentRedirector.PointsInto`) and ticking/unticking only changes folders and variables, keeping what no preset owns (`PresetSelection.Apply`); a live (no-remount) edit syncs the mounted disk via `DiskEffectsDiff` and `MainViewModel.SyncDiskEffectsAsync` (new variables applied, dropped ones restored, dropped folders left alone); a preset that redirects variables (`PresetSelection.IsExclusive`; not the folder-only browser one) lives on one disk only: creating or editing a disk to have it takes it from the others first (`MainViewModel.ReleaseClaimedPresetsAsync` -> `PresetSelection.Release`, variables restored, folders kept, unmounted profiles trimmed too), whereas auto-mount never takes over and still rejects a variable another disk owns; `DiskProfile` saves a disk's variable-redirecting presets as ids only (`DiskProfile.PresetIds`, via `PresetSelection.Split`/`Expand` in `ToProfile`/`ProfileToOptions`; older expanded profiles still load and are re-saved as ids), while the runtime `DiskOptions` keeps the expanded `Folders`/`EnvRedirects`; a disk keeps the result in `DiskOptions.Folders`/`EnvRedirects` (mirrored in `DiskProfile`, and `MainViewModel.ToProfile`/`ProfileToOptions` must carry any new field). A disk without an image is empty on every mount, so `MainViewModel.AddDiskSorted` runs `UserEnvironmentRedirector.ApplyDiskEffects` after each mount (creates the folders, points the per-user environment variables into the disk). The redirector stores each variable's previous registry value and kind in `EnvRedirectBackupStore` (`env-redirects.json` next to `settings.json`, so a damaged settings file cannot lose it) *before* writing, restores it when the disk leaves `Disks`, on exit (`RestoreAllEnvRedirects` in `ShutdownAsync`, and on Windows logoff/shutdown via `SessionEndingSaveHandler`'s `beforeSave` hook, which also resets TEMP and skips the broadcast) and for leftovers after a crash (`RestoreDanglingEnvRedirects` at startup, before the auto-mount); a variable is only restored while it still holds the value we wrote, and a variable already owned by another disk is refused. TEMP/TMP are ordinary redirects of the temp preset (`EnvRedirectPolicy` no longer reserves them), so they get the same backup/restore, and the context-menu "set as temp" action applies the same redirects; `MainViewModel.RestoreUserTemp` restores them from the backup and falls back to `TempDirResetService.Reset` (Windows defaults) only when nothing was recorded, e.g. a TEMP set by an older version. Ticking a temp-redirecting preset in the create dialog asks the one-time compatibility warning (`ConfirmTempDirWarning`). `IUserEnvironment` is the registry seam for tests. The `WM_SETTINGCHANGE` broadcast waits on every top-level window, so `TempDirResetService` sends it through `CoalescingBroadcaster` on a background thread (merged requests, `WaitForPendingBroadcasts` at exit) instead of on the caller's thread. Browsers have no cache environment variable, so that preset only creates a folder. The user-triggered restore (toolbar menu and tray submenu, `MainViewModel.GetEnvRestoreGroups`/`RestoreEnvAsync`) is a second path on the same backups: `EnvRestoreGroups` (Core, pure) groups the redirected variables by preset, `EnvRestoreService` restores one group (TEMP/TMP through `RestoreRecordedTemp`, which also covers a legacy TEMP without backup), and `DiskRestoreCoordinator` plus `DiskRestorePlanner` (Core, pure) make the mounted disks agree afterwards: before the restore it notes which presets/custom redirects point into each disk (`Plan`), afterwards it keeps only what really stopped pointing there (`Settle`, so a partly failed restore never leaves a disk claiming less than the environment backs) and drops it from the disk's options (`Apply`, same release as unticking the preset, folders on the disk stay). Call `DiskRestoreCoordinator.RunAsync`, not `Plan`/`TrimAsync` directly: it plans, runs the restore, trims even when the restore throws midway (some variables may already be back) and then rethrows (as an `AggregateException` with the report failure if the after-trim callback fails too). It runs on the UI thread under the busy overlay; `EnvRestoreMenuEntry` lays out the menu rows for both the window and the tray (`EnvRestoreTrayAction`). Keep these in step with the preset and backup rules above.
- Notifications: `DiskNotificationService` turns `DiskViewModel` events into status-bar text and tray balloons (balloons only while the main window is hidden). Besides high usage and save failures it reports refused writes: `MemoryFileSystem` raises `WriteRejected` (`WriteRejectionReason.LowMemory`/`DiskFull`, only on the refusal paths, at most once a second per reason) which `RamDisk` and `DiskViewModel` forward to the UI thread; the balloon is limited by `NotificationCooldown` (10 minutes per disk and reason, spent only when a balloon is actually shown). A 10 s poll feeds `LowMemoryMonitor` (pure; warns under 512 MB, recovers above 768 MB, 10-minute warning cooldown) so the user hears about low memory before the write guard starts refusing. `DiskViewModel.SaveRecovered` clears the sticky save-failure status once a save succeeds again. The disk card's redirect badge (`DiskViewModel.HasActiveRedirects`/`ActiveRedirectsTooltip`) shows what the environment really redirects into the disk, not only TEMP: `PresetSelection.DetectActive` (variable-redirecting presets whose variables all point into the disk, except that Temp also counts when TEMP alone does (`PresetSelection.TempPointsIntoDisk`, shared with `Reconcile`, which then syncs TMP too); plus custom variables) fed by `UserEnvVarCache` (5 s shared cache that bumps `Generation` only when a value changed or the app wrote the user environment) through `RedirectBadgeTracker`; all matching goes through `EnvRedirectPolicy.IsRedirectedInto`, also for `IsCurrentTempDir`, which stays TEMP-only and drives the unmount/reset logic. The status-bar file name is throttled to 500 ms (`DiskViewModel.ActivityThrottleWindow`).
- Self-update: `UpdateCheckService` (check) -> `UpdateDialog` (prompt/progress) -> `UpdateInstaller` (download via GitHubReleaseUpdater into `%LOCALAPPDATA%\ManagedDrive\updates`, never `%TEMP%`, then start the Inno Setup installer with `/SILENT`). The app does not exit itself: `installer/ManagedDrive.iss` asks the running app to exit via `mdrive.exe exit` (saves disks) and restarts it afterwards. A TEMP on a RAM disk is reset first because the installer aborts silently in that case. Generic download/verify/launch logic lives in the separate GitHubReleaseUpdater library; keep only ManagedDrive-specific policy here.
- `RamDisk.TryApplyOptions()` applies non-destructive changes live; drive-letter or read-only changes require full remount.
- `CreateDiskDialog` has four modes: create, edit, import `.mdr`, import archive. Its `MainTabControl` has a fixed `MinHeight` (set to tallest tab) — bump if a tab's content grows.
- Custom window chrome (`WindowStyle="None"` + `WindowChrome`): interactive elements in caption area need `WindowChrome.IsHitTestVisibleInChrome="True"`.
- Logging: Serilog file logger (`%APPDATA%\ManagedDrive\logs/`), bridged to Core via `AppLog.Configure`.

### CLI layer

`mdrive.exe` is a thin pipe client → running `ManagedDrive.exe`. If no server, it launches the app and polls until connected. `CliCommandProcessor` renders via `Spectre.Console` into memory buffers (not real console). `ICliDiskController` is the seam avoiding circular references. `--json` is a recursive option on the root command: handlers fill `CliOutcome` as usual (data commands also set `Data`) and `ExecuteAsync` converts the outcome at the end via `CliJson`, so the renderer just prints `Message` verbatim when `Json` is set. `watch` is the one command that is not request/response: `CliPipeServer.HandleConnectionAsync` branches on `CliCommandProcessor.IsWatchCommand` before taking the execution gate and streams `CliResponse` lines from an `ICliEventSource` (the app's implementation hooks `MainViewModel.Disks` and each `DiskViewModel`'s events), capped at `MaxWatchers` so ordinary commands keep pipe instances; the client side is `CliPipeClient.WatchAsync`. `snapshot extract` loads a snapshot with `SnapshotManager.LoadSnapshot` and writes it out through `SnapshotExtractor` (streams and links skipped, conflicts checked before anything is written). `usage` and the app's Space Usage dialog share `SpaceUsageAnalyzer` (`Core/Diagnostics`): sizes are allocation sizes so the total equals the disk's used bytes, alternate streams count towards their file, and the dialog's treemap is `TreemapLayout`/`TreemapBuilder` (pure, unit-tested geometry) drawn by `Controls/TreemapControl`.

### Localization & Theming

- Strings: `Localization/Strings.{tag}.xaml`, swapped at runtime via `LanguageManager`. Use `{DynamicResource Key}` in XAML.
- Themes: `Themes/AppTheme.Colors.{Light,Dark}.xaml` palettes, swapped via `ThemeManager`. Structural styles in `AppTheme.xaml` reference colors by `{DynamicResource}`.
- Persist `SavedLanguage`/`SavedTheme` (raw user choice, `null` = system default), not `CurrentLanguage`/`CurrentTheme` (resolved concrete value).
- Icons: **Segoe Fluent Icons** font. No third-party UI framework.
- Adding a language: create `Strings.{tag}.xaml`, add tag to `LanguageManager.SupportedLanguages` and `<SatelliteResourceLanguages>` in `Directory.Build.props`.

### SingleFile publish caveat

`ManagedDrive.App.csproj` has `PublishSingleFile=true`. `winfsp-msil.dll` is mixed-mode (C++/CLI) and excluded via `ExcludeWinFspMsilFromSingleFile` target — do not remove it or the app throws at startup. This DLL (from NuGet, next to the exe) is distinct from the system-installed one at `C:\Program Files (x86)\WinFsp\bin\`.

### Release pipeline

`.github/workflows/ci.yml`: build + test on every push. `v*` tag → framework-dependent publish (`win-x64`, not self-contained) → GitHub Release with portable ZIP + Inno Setup installer. Installer bundles WinFsp MSI and auto-downloads .NET 10 Desktop Runtime if missing.

Local installer test: publish App/Cli into `installer/publish-fx/`, then `iscc.exe /DAppVersion=0.0.0-test installer/ManagedDrive.iss`.

### Benchmarks

`dotnet run --project benchmarks/ManagedDrive.Benchmarks -c Release` — three classes: `SequentialReadWriteBenchmarks`, `RandomAccessBenchmarks`, `ConcurrentAccessBenchmarks`. Pass `--filter '*ClassName*'` to run non-interactively.
