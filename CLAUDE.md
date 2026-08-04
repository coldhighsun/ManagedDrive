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

Binary format: magic `MDRD`, version `3` (current). Capacity and volume label are always plaintext header fields; only the node region is compressed and optionally encrypted.

**Compression uses Zstd** (via `ZstdSharp.Port`) with parallel chunked encoding (`ParallelZstd`). `ImageCompressionLevel` enum (`None=0`/`Fastest=1`/`Optimal=2`/`SmallestSize=3`) has stable explicit values persisted to disk/JSON — do not renumber. `DiskOptions.CustomZstdLevel` (`int?`) overrides the preset's Zstd level (1-22) when set. Legacy gzip-compressed v1/v2 images are still readable — do not remove those `Load()` branches.

Encryption: AES-256-GCM envelope encryption (random CEK wrapped by user password via PBKDF2). The wrapped-CEK material is plaintext header; node region is encrypted under the CEK.

Snapshots use a separate format (magic `MDRS`) with content-addressed blob store — don't conflate with `DiskImageSerializer`.

### Threading model

- `FileNodeMap`, `MountManager`, `RamDisk._autoSaveLock` use C# 13 `Lock` type.
- WinFsp callbacks fire on driver threads; state access through `FileNodeMap`'s lock.
- Auto-save timer: periodic path uses `Lock.TryEnter()` (skip if busy); `Dispose()` uses blocking `lock` (wait then final save).
- `FileNodeMap.GetTotalAllocated()` is O(1) via incremental `_totalAllocated`. Only mutate `AllocationSize` through `UpdateAllocationSize()` — direct assignment drifts the cached total.

### App layer patterns

- Standard WPF MVVM. `App.xaml.cs` orchestrates startup/shutdown; specific concerns delegated to `Services/` classes (`TrayIconController`, `TrayTooltipController`, `DiskNotificationService`, `TempDirCompatChecker`, `SessionEndingSaveHandler`, `UpdateCheckService`, `GlobalMountCoordinator`, `ShellContextMenuManager`).
- Disk operations (`Mount`/`Unmount`/`Save`/`Dispose`) dispatched via `Task.Run` to keep UI responsive.
- `RamDisk.TryApplyOptions()` applies non-destructive changes live; drive-letter or read-only changes require full remount.
- `CreateDiskDialog` has four modes: create, edit, import `.mdr`, import archive. Its `MainTabControl` has a fixed `MinHeight` (set to tallest tab) — bump if a tab's content grows.
- Custom window chrome (`WindowStyle="None"` + `WindowChrome`): interactive elements in caption area need `WindowChrome.IsHitTestVisibleInChrome="True"`.
- Logging: Serilog file logger (`%APPDATA%\ManagedDrive\logs/`), bridged to Core via `AppLog.Configure`.

### CLI layer

`mdrive.exe` is a thin pipe client → running `ManagedDrive.exe`. If no server, it launches the app and polls until connected. `CliCommandProcessor` renders via `Spectre.Console` into memory buffers (not real console). `ICliDiskController` is the seam avoiding circular references.

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
