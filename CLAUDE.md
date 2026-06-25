# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```powershell
# Build all projects
dotnet build

# Run the app
dotnet run --project src/ManagedDrive.App

# Run tests
dotnet test tests/ManagedDrive.Tests

# Run a single test class
dotnet test tests/ManagedDrive.Tests --filter "FullyQualifiedName~FileNodeTests"
```

The solution file is `ManagedDrive.slnx` (Visual Studio 2022+ format).

**WinFsp prerequisite:** `winfsp-msil.dll` must be present at `C:\Program Files (x86)\WinFsp\bin\`. Install WinFsp 2.x before building or running.

## Architecture

The solution has three projects targeting `net10.0-windows`:

- **`ManagedDrive.Core`** — Pure file-system engine, no UI dependency.
- **`ManagedDrive.App`** — WPF + WinForms (for `NotifyIcon`) desktop app. References Core.
- **`ManagedDrive.Tests`** — xUnit v3 unit tests. Only tests pure-managed code (no WinFsp driver needed).

### Core layer (`ManagedDrive.Core`)

Data flows: `MountManager` → `RamDisk.Create()` → `MemoryFileSystem` + `FileSystemHost` (WinFsp).

- `DiskOptions` — immutable `record` carrying mount configuration (mount point, capacity, label, read-only, auto-mount, optional `.mdr` image path).
- `FileNode` — a single file or directory node: `Fsp.Interop.FileInfo` metadata + `byte[]` data buffer.
- `FileNodeMap` — `SortedDictionary<string, FileNode>` (case-insensitive) mapping full paths to nodes; supports child enumeration and tracks total allocated bytes.
- `MemoryFileSystem : FileSystemBase` — implements all WinFsp callbacks (`Create`, `Open`, `Read`, `Write`, `Rename`, `CanDelete`, `ReadDirectoryEntry`, etc.). Enforces capacity ceiling; returns `STATUS_DISK_FULL` when exceeded.
- `RamDisk` — wraps `MemoryFileSystem` + `FileSystemHost`. `RamDisk.Create(options)` mounts the volume; `Dispose()` unmounts. After mounting, polls until the drive letter is visible in the OS (up to 2.5 s), then broadcasts `SHCNE_DRIVEADD` to Explorer.
- `DiskImageSerializer` — reads/writes the `.mdr` binary format (magic `MDRD`, little-endian; stores capacity, label, and all file nodes including security descriptors).
- `MountManager` — thread-safe registry of active `RamDisk` instances. `Mount(options)` calls `RamDisk.Create`; `Unmount(mountPoint)` disposes the disk.

### App layer (`ManagedDrive.App`)

Standard WPF MVVM:

- `App.xaml.cs` — application entry point. Creates `MountManager` + `SettingsStore`, constructs `MainViewModel`, sets up the system-tray `NotifyIcon`, auto-mounts profiles with `AutoMount = true` on startup, saves settings on exit. Enforces single-instance via a named `Mutex`. Serilog logs to `{AppPath}\logs\log-.txt` with 7-day rolling retention.
- `TrayIconFactory` — generates the 32×32 tray icon entirely in code (System.Drawing); no embedded icon resource.
- `MainViewModel` — owns `ObservableCollection<DiskViewModel>`. Commands: `CreateDiskCommand` (opens `CreateDiskDialog`), `UnmountCommand`, `SaveImageCommand`, `RefreshCommand`, `SettingsCommand`. Mount operations are dispatched to `Task.Run` to keep the UI responsive.
- `DiskViewModel` — wraps a `RamDisk`; exposes bindable properties (mount point, used/free/total bytes). A `DispatcherTimer` refreshes usage stats every 2 s automatically; `Refresh()` triggers a manual refresh.
- `MainWindow` — closing the window hides it to the tray rather than exiting; exit is only via the tray context menu.
- `SettingsStore` — persists `AppConfiguration` (JSON) to `%APPDATA%\ManagedDrive\settings.json`. `AppConfiguration` holds `RunAtStartup` and the list of `DiskProfile` records.
- `StartupManager` — reads/writes the `HKCU\...\Run` registry key to control Windows startup.

### Threading model

- `FileNodeMap` and `MountManager` use the C# 13 `Lock` type (not the older `lock` statement) for thread safety.
- WinFsp callbacks in `MemoryFileSystem` are invoked on WinFsp driver threads; all state access goes through `FileNodeMap`'s `Lock`.

### Central package management

Package versions are pinned in `Directory.Packages.props` (Central Package Management). Do not add `Version=` attributes to `<PackageReference>` elements in individual `.csproj` files — add versions only to `Directory.Packages.props`.

### Versioning

`MinVer` derives the assembly version from git tags (`v`-prefixed, e.g. `v0.1.0`). The test project sets `<MinVerSkip>true</MinVerSkip>` to avoid version errors without a tag.

### Output layout

`<UseArtifactsOutput>true</UseArtifactsOutput>` in `Directory.Build.props` routes all build outputs to the `artifacts/` directory (SDK-style artifacts layout) rather than `bin/` and `obj/` per project.
