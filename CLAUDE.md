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
```

The solution file is `ManagedDrive.slnx` (Visual Studio 2022+ format).

**WinFsp prerequisite:** `winfsp-msil.dll` must be present at `C:\Program Files (x86)\WinFsp\bin\`. Install exactly [WinFsp 2.2.26183 (2026 Beta2)](https://github.com/winfsp/winfsp/releases/tag/v2.2B2) before building or running — download the MSI directly; do not use `winget install WinFsp.WinFsp`, as the winget package lags behind this release.

## Architecture

`Directory.Build.props` sets `Nullable enable` and `ImplicitUsings enable` globally — do not add explicit `using` directives for implicitly imported namespaces, and follow nullable annotation conventions throughout.

`ManagedDrive.App/GlobalUsings.cs` adds project-wide `global using` directives for namespaces referenced across most files: all `ManagedDrive.App.*` sub-namespaces, `ManagedDrive.Core`, `Microsoft.Win32`, `System.ComponentModel`, `System.Diagnostics`, `System.IO`, `System.Windows`, and `System.Windows.Threading`. Don't re-add explicit `using` directives for these in individual files — only add file-specific ones (e.g. `System.Windows.Controls`, `System.Windows.Input`, `System.Text.Json`).

The solution has three projects targeting `net10.0-windows`:

- **`ManagedDrive.Core`** — Pure file-system engine, no UI dependency.
- **`ManagedDrive.App`** — WPF + WinForms (`UseWindowsForms=true` for `System.Windows.Forms.NotifyIcon` tray icon) desktop app. References Core. The implicit `System.Windows.Forms` using is removed in the csproj to avoid ambiguity with WPF types; use fully qualified names when accessing WinForms types.
- **`ManagedDrive.Tests`** — xUnit v3 unit tests. Only tests pure-managed code (no WinFsp driver needed).

### Core layer (`ManagedDrive.Core`)

Data flows: `MountManager` → `RamDisk.Create()` → `MemoryFileSystem` + `FileSystemHost` (WinFsp).

- `DiskOptions` — immutable `record` carrying mount configuration (mount point, capacity, label, read-only, auto-mount, optional `.mdr` image path, optional `AutoSaveIntervalMinutes`).
- `FileNode` — a single file or directory node: `Fsp.Interop.FileInfo` metadata + `byte[]` data buffer.
- `FileNodeMap` — `SortedDictionary<string, FileNode>` (case-insensitive) mapping full paths to nodes; supports child enumeration and tracks total allocated bytes.
- `MemoryFileSystem : FileSystemBase` — implements all WinFsp callbacks (`Create`, `Open`, `Read`, `Write`, `Rename`, `CanDelete`, `ReadDirectoryEntry`, etc.). Enforces capacity ceiling; returns `STATUS_DISK_FULL` when exceeded. In read-only mode, all mutating operations return `STATUS_MEDIA_WRITE_PROTECTED`. `ReadDirectoryEntry` builds a snapshot list on the first call (including `.` and `..`), then iterates it statelessly on subsequent calls. `Init()` auto-creates the root directory `\` with a standard security descriptor. `FileSystemHost` is configured with 4 KB sector/allocation size, `FileInfoTimeout=1000 ms`, `CasePreservedNames=true`, `CaseSensitiveSearch=false`, and a randomized `VolumeSerialNumber`.
- `RamDisk` — wraps `MemoryFileSystem` + `FileSystemHost`. `RamDisk.Create(options)` mounts the volume; `Dispose()` unmounts. After mounting, polls `DriveInfo.GetDrives()` up to 25 × 100 ms (2.5 s total) until the drive letter appears, then broadcasts `SHCNE_DRIVEADD` via `SHChangeNotify` so Explorer refreshes immediately.
- **Auto-save** (`RamDisk`) — when `DiskOptions.AutoSaveIntervalMinutes` and `PersistImagePath` are both set, `ConfigureAutoSaveTimer()` (called from `Create()` and `TryApplyOptions()`) starts a `System.Threading.Timer` with `dueTime=TimeSpan.Zero` so the first save fires immediately on a background thread, then repeats every interval. `TryAutoSave()` guards against overlapping saves with a non-blocking `_autoSaveLock.TryEnter()` (a C# 13 `Lock`) — if a save is already running, that tick is skipped rather than queued. `Dispose()` takes the same lock with a blocking `lock` statement (waits for any in-flight save, then performs one final save) before unmounting, so a disk is never left unsaved on unmount/edit-remount/app-exit.
- `DiskImageSerializer` — reads/writes the `.mdr` binary format (magic `MDRD`, little-endian; stores capacity, label, and all file nodes including security descriptors). `Save()` creates any missing parent directories before opening the `FileStream`.
- `MountManager` — thread-safe registry of active `RamDisk` instances. `Mount(options)` calls `RamDisk.Create`; `Unmount(mountPoint)` disposes the disk. Fires `DiskMounted` / `DiskUnmounted` events consumed by the App layer to update the UI.
- `RamDisk.TryApplyOptions(options)` — applies non-destructive changes (label, capacity, auto-mount, image path, auto-save interval) to a live disk without unmounting; returns `false` if the change requires a full remount (drive letter or read-only flag changed). In `MainViewModel`, both this call and `MountManager.Unmount`/`Dispose` (which may perform a final auto-save write) are dispatched via `Task.Run` so periodic or final saves never block the UI thread.

### App layer (`ManagedDrive.App`)

Standard WPF MVVM:

- `App.xaml.cs` — application entry point. Creates `MountManager` + `SettingsStore`, constructs `MainViewModel`, sets up the system-tray icon (`System.Windows.Forms.NotifyIcon`), auto-mounts profiles with `AutoMount = true` on startup, saves settings on exit. Enforces single-instance via a named `Mutex` (GUID `Global\ManagedDrive-4A7C2E1B-…`; bypassed when `DOTNET_ENVIRONMENT="Development"`). On startup, validates WinFsp 2.2.x is installed (checks registry + DLL file version) and shows an install prompt if missing. Also checks if TEMP/TMP points to a non-auto-mount RAM disk drive; if so, resets TEMP to Windows defaults and warns the user.
- `MainViewModel` — owns `ObservableCollection<DiskViewModel>` sorted by mount point. Commands: `CreateDiskCommand` (opens `CreateDiskDialog`), `EditDiskCommand` (edit label/capacity/flags; non-destructive changes apply live via `RamDisk.TryApplyOptions()`; mount-point or read-only changes trigger full remount), `UnmountCommand` (auto-resets TEMP if it points to the disk before unmounting), `FormatCommand` (deletes all files on the disk; blocked when read-only), `SaveImageCommand`, `RefreshCommand`, `SettingsCommand`, `ResetTempDirsCommand` (resets TEMP/TMP to Windows defaults), `ToggleTempDirCommand` (toggles TEMP/TMP between the selected disk's `Temp` folder and the Windows default). Mount operations, `MountManager.Unmount`, and `MountManager.Dispose` (in `App.xaml.cs`) are dispatched to `Task.Run` to keep the UI responsive, since unmounting may perform a synchronous final auto-save write. `GetOtherDiskOptions(excluding)` returns the `DiskOptions` of every other active disk, passed into `CreateDiskDialog` so it can validate that a new/edited disk's image path doesn't collide with another disk's mount point or image file.
- `DiskViewModel` — wraps a `RamDisk`; exposes bindable properties (mount point, used/free/total bytes, `IsCurrentTempDir`). A `DispatcherTimer` refreshes usage stats every 2 s automatically; `Refresh()` triggers a manual refresh. Fires `HighUsageWarning` when usage first crosses 90 %; the warning resets when usage drops below 85 % (hysteresis to prevent rapid flip-flopping). `IsCurrentTempDir` compares `[MountPoint]\Temp` against the user-level TEMP variable (case-insensitive).
- `MainWindow` — uses `WindowStyle="None"` + `WindowChrome` (custom app bar as title bar). Closing the window hides it to the tray; exit is only via the tray menu or the toolbar close button. Any interactive element inside the `WindowChrome` caption area must have `WindowChrome.IsHitTestVisibleInChrome="True"`. The overflow menu uses a `Button` + `ContextMenu` pattern (opened in code-behind). A `TrayTooltipView` popup appears on tray icon hover and auto-hides after 3 s.
- `SettingsStore` — persists `AppConfiguration` (JSON) to `%APPDATA%\ManagedDrive\settings.json`. `AppConfiguration` holds `RunAtStartup`, `StartMinimized`, `Language` (BCP-47 tag or `null` for system default), `Theme` (`"light"`/`"dark"`/`null` for system default), `TempDirCompatWarningShown` (one-time startup warning flag), and the list of `DiskProfile` records. `DiskProfile` is the serializable counterpart of `DiskOptions`.
- `RelayCommand` — thin `ICommand` wrapper in `Infrastructure/`; constructor takes `execute` action and optional `canExecute` predicate. Used for all ViewModel commands.
- `StartupManager` — reads/writes the `HKCU\...\Run` registry key to control Windows startup.
- `TempDirResetService` — static helper that reads/writes `HKCU\Environment` (TEMP and TMP) and broadcasts `WM_SETTINGCHANGE` so running processes pick up the change immediately. `Set(path)` writes `String` values (absolute paths); `Reset()` writes `ExpandString` values (unexpanded `%USERPROFILE%\AppData\Local\Temp`) for portability. Uses `SendMessageTimeoutAbortIfHung` with a 5 s timeout.
- **Known TEMP limitation:** WinFsp mounts into a session-specific namespace (`\Sessions\{id}\...`), so some installers that access TEMP via the global device namespace (`\Device\...`) fail with `0x800704b3`. Known affected: WeChat, 7-Zip, Git for Windows (via winget). The app shows a one-time warning on startup when TEMP points to a non-auto-mount RAM disk.
- **Dialogs** (`Views/`) — `CreateDiskDialog` collects drive letter, capacity, label, read-only, auto-mount, optional image path, and optional auto-save interval, laid out in three bordered sections (`SectionBorder`/`SectionHeaderText` styles in `AppTheme.xaml`): Basic Info, Access, and Persistence. Capacity maximum is derived from `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes` at open time; switching MB/GB units recalculates the maximum. `ImagePathBox` is `IsReadOnly` — clicking it (via `PreviewMouseLeftButtonDown`) always opens a `SaveFileDialog`; the path cannot be typed directly. Checking "Read Only" force-unchecks and disables auto-save (a read-only disk's contents never change). The auto-save interval (1–60 minutes, default 10) uses the same textbox + up/down `RepeatButton` pattern as capacity. `TryBuildOptions()` validates: image path is rooted with an existing parent directory (`IsValidImagePath`), doesn't fall under any active disk's mount point, and isn't already used as another disk's image path (checked against the `otherDisks` list passed into the constructor). The dialog has an edit-mode constructor overload that pre-populates all fields (the in-use drive letter is excluded from the unavailable-drives check). `SettingsDialog` handles language and startup preferences. `ConfirmDialog` is a generic title + body confirmation. `AboutDialog` shows the app version (trimmed at `+` to strip the git hash suffix) and a GitHub hyperlink. All dialogs are shown with `ShowDialog()` from their respective ViewModel commands.

### Localization

Strings live in `Localization/Strings.{tag}.xaml` resource dictionaries. `LanguageManager` swaps the active dictionary at runtime by removing the old one and adding the new one to `Application.Current.Resources.MergedDictionaries`.

- `Loc.Get(key)` / `Loc.Format(key, args)` — retrieve strings from the current resource dictionary.
- `LanguageManager.Instance.Apply(string? saved)` — `null` or empty means "system default"; the method resolves to a concrete tag via `LanguageManager.Resolve()` (matches system locale against `SupportedLanguages`, falls back to `"en-US"`).
- `LanguageManager.Instance.SavedLanguage` — the raw persisted choice (`null` = system default). `CurrentLanguage` is always the resolved concrete tag. Always persist `SavedLanguage`, not `CurrentLanguage`.
- XAML strings use `{DynamicResource Key}` bindings so that a runtime language switch propagates without restart.
- Custom styles are defined in `Themes/AppTheme.xaml`; the app supports light and dark palettes (see Theming below). Icons use the **Segoe Fluent Icons** font (built into Windows 10/11). There is no third-party UI framework — all controls are native WPF with custom styles.
- `Helpers/HintHelper.cs` provides a `Hint.Text` attached property for watermark/placeholder text in TextBox and ComboBox controls.

**Adding a new language:** create `Localization/Strings.{tag}.xaml` (copy an existing one), add the BCP-47 tag to `LanguageManager.SupportedLanguages`, and add the tag to `<SatelliteResourceLanguages>` in `Directory.Build.props`.

### Theming

`Themes/AppTheme.xaml` defines structural styles/templates that reference color keys by `{DynamicResource}`; the actual colors live in separate palette dictionaries swapped at runtime:

- `ThemeManager.Instance` — analogous to `LanguageManager`. `Apply(saved)` takes `"light"`, `"dark"`, or `null` (system default) and merges `Themes/AppTheme.Colors.{Light,Dark}.xaml` into `Application.Current.Resources.MergedDictionaries`, removing the previous palette dictionary first. `SavedTheme` is the raw persisted choice (`null` = system default); `CurrentTheme` is always the resolved concrete value. Always persist `SavedTheme`, not `CurrentTheme`, mirroring the localization convention.
- System-default resolution reads `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme` and falls back to light on any failure. When `SavedTheme` is `null`, `ThemeManager` subscribes to `SystemEvents.UserPreferenceChanged` (`UserPreferenceCategory.General`) to live-follow OS theme switches.
- `TrayColorTable` supplies theme-aware colors for tray-menu/tooltip rendering that can't use `DynamicResource` bindings (e.g. WinForms `ContextMenuStrip`).

### Tests (`ManagedDrive.Tests`)

Tests are synchronous xUnit v3 unit tests for pure-managed code (no WinFsp driver required). `FileNodeTests` covers `FileNode` metadata; `FileNodeMapTests` covers all map operations. Both use two local helpers — `MakeDir()` and `MakeFile()` — to construct test nodes with appropriate `FileAttributes`.

### Threading model

- `FileNodeMap`, `MountManager`, and `RamDisk` (its `_autoSaveLock`) use the C# 13 `Lock` type (not the older `lock` statement) for thread safety.
- WinFsp callbacks in `MemoryFileSystem` are invoked on WinFsp driver threads; all state access goes through `FileNodeMap`'s `Lock`.
- `RamDisk`'s auto-save timer fires on a `System.Threading.Timer` threadpool thread; the periodic path uses `Lock.TryEnter()` (skip if a save is already running) while `Dispose()`'s final save uses a blocking `lock` (wait for any in-flight save, then save once more) — see the Core layer section above.

### Central package management

Package versions are pinned in `Directory.Packages.props` (Central Package Management). Do not add `Version=` attributes to `<PackageReference>` elements in individual `.csproj` files — add versions only to `Directory.Packages.props`.

### Versioning

`MinVer` derives the assembly version from git tags (`v`-prefixed, e.g. `v0.1.0`). The test project sets `<MinVerSkip>true</MinVerSkip>` to avoid version errors without a tag.

### Output layout

`<UseArtifactsOutput>true</UseArtifactsOutput>` in `Directory.Build.props` routes all build outputs to the `artifacts/` directory (SDK-style artifacts layout) rather than `bin/` and `obj/` per project.

### Benchmarks

`ManagedDrive.Benchmarks` (separate project, not in the solution) uses BenchmarkDotNet to compare RamDisk vs physical disk for sequential read/write at 4 KB, 1 MB, and 64 MB. Drive letter `R:` must be free. Run in Release mode: `dotnet run --project benchmarks/ManagedDrive.Benchmarks -c Release`.

### Release pipeline

`.github/workflows/ci.yml` builds and runs tests on every push. Pushing a `v*` tag additionally publishes a self-contained Windows executable (`-r win-x64`) and builds a **multilingual MSI** via WiX v4 (`installer/ManagedDrive.wxs`), then creates a GitHub Release with both artifacts attached.

The multilingual MSI requires two WiX builds followed by transform embedding:
1. Build the base MSI (`-culture en-US -loc installer/en-US.wxl`) → `ManagedDrive-en-US.msi`
2. Build a second MSI (`-culture zh-CN -loc installer/zh-CN.wxl`) → `ManagedDrive-zh-CN.msi`
3. Generate a language transform: `wix msi transform ManagedDrive-en-US.msi ManagedDrive-zh-CN.msi -out zh-CN.mst`
4. Embed the transform into the base MSI using `installer/EmbedTransform.vbs` (Windows Installer API via VBScript), which injects `zh-CN.mst` as a substorage and updates the SummaryInformation `Languages` property

The MSI uses a fixed `UpgradeCode` GUID and `MajorUpgrade` to support in-place updates. `MinVer` derives the version from the tag, so the tag must match the `v{major}.{minor}.{patch}` pattern.

The installer also includes:
- `util:CloseApplication` to terminate the running app before file operations (5 s timeout)
- A `TempDirWarningUI` custom dialog injected into the maintenance sequence (between `MaintenanceWelcomeDlg` and `MaintenanceTypeDlg`) to warn if TEMP/TMP points to the RAM disk
- A launch-after-install checkbox on the exit dialog (shown on install/repair, not uninstall)
