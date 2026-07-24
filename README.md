# ManagedDrive

[![CI / Release](https://github.com/coldhighsun/ManagedDrive/actions/workflows/ci.yml/badge.svg)](https://github.com/coldhighsun/ManagedDrive/actions/workflows/ci.yml)
[![Latest Release](https://img.shields.io/github/v/release/coldhighsun/ManagedDrive)](https://github.com/coldhighsun/ManagedDrive/releases/latest)
[![GitHub All Releases](https://img.shields.io/github/downloads/coldhighsun/ManagedDrive/total)](https://github.com/coldhighsun/ManagedDrive/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

[English](#english) | [中文](#中文)

---

## English

A Windows RAM disk manager built on .NET 10 and [WinFsp](https://winfsp.dev).  
Create, mount and manage in-memory volumes that appear as normal drive letters in Explorer.

### Features

**Core**
- Mount multiple RAM disks simultaneously, each with its own drive letter, capacity, volume label and read-only flag
- Dynamic memory allocation — capacity is a ceiling, not a reservation; memory is used only by actual file data
- Edit a mounted disk live (label, capacity, auto-mount, image path) without losing data; changing the drive letter or read-only flag remounts the disk
- NTFS-compatible volume identity, so RAM disks work as targets for tools that require NTFS (e.g. WinGet, Windows Update staging, BITS)
- Auto-mount saved profiles on startup
- Format a disk to instantly delete all its contents (**Format**; read-only disks are protected)

**Persistence, snapshots & cloning**
- Save disk contents to a `.mdr` image file and restore it on next mount, or import an existing image directly (**Import Disk...**)
- Import an archive (zip, 7z, rar, tar, and other formats [SharpCompress](https://github.com/adamhathcock/sharpcompress) can read) directly as a read-only disk (**Import Archive...**) — capacity and label are derived from the archive up front
- Optional auto-save on a 1–60 minute interval, plus an automatic final save before unmount/exit (individually disableable per disk via **Save on exit**); both are skipped when nothing has changed, and failures raise a tray/status-bar notification
- Selectable image compression (Off / Fast / Balanced / Max, default Fast)
- Snapshot / version history — cap retained snapshots by count and/or size; deduplicated by content hash so many snapshots cost little extra space; restore any snapshot via **Restore Snapshot...**, which also lets you delete an individual snapshot on demand
- Clone a disk onto another mounted disk or export it to a new `.mdr` file (**Clone Disk...**)
- Optional password protection for `.mdr` images (AES-256-GCM envelope encryption; the password only wraps a random per-disk key, so changing the password never re-encrypts the file contents) — set from the "Encrypt Image" option in the disk dialog (password must be 8–64 characters); prompted for on mount (including auto-mount at startup) whenever an image is encrypted
- Progress feedback for long operations — image save, archive import, and export all show a busy overlay with a progress bar instead of leaving the app looking unresponsive on large disks

**CLI**
- `mdrive` command-line tool (ships alongside `ManagedDrive.exe`) for scripting mount/unmount/format/save/list/exit against the running app, forwarded over a named pipe
- Auto-launches `ManagedDrive.exe` if it isn't already running and waits for it to become ready before sending the command

**Convenience & safety**
- Optional Explorer right-click integration: enable the setting to add **"Mount as RAM disk (ManagedDrive)"** to the right-click menu for zip/7z/rar/tar archives, mounting one with a single click — no need to open the app first — launches `ManagedDrive.exe` automatically if it isn't already running, and opens the new drive in Explorer as soon as it's mounted
- System tray icon with hover tooltip (live usage per disk, plus available system memory), quick-access menu, and optional start-minimized mode; the icon briefly flashes a read/write indicator whenever any mounted disk is accessed
- Available system memory shown live in the main window's status bar (refreshed every 2 seconds)
- Main window status bar also shows the file most recently read from or written to, pushed live as it happens (throttled to at most once every 300 ms) rather than polled — automatically paused while the window is hidden in the tray and resumed when it's shown again
- High-usage warning per disk (configurable threshold, default 90%, with hysteresis)
- Temp directory redirection — point Windows TEMP/TMP at a disk's `Temp` folder, with automatic reset on unmount/remount and startup warnings if TEMP is left pointing at a RAM disk
- Exit confirmation with a saving overlay while pending image saves complete; TEMP is reset first if it points at a mounted disk
- Double-click a disk to open it in Explorer; right-click for Explorer/image-folder shortcuts, or **View Disk Contents...** for a read-only, sortable Name/Size/Type tree view without leaving the app

**UI**
- Bilingual (English / Simplified Chinese) and light/dark themes, both auto-detected with manual override in Settings, switching instantly without restart
- At-a-glance disk cards with status badges (read-only, current-TEMP, backing image, password-protected) and a usage bar that turns warning-colored past the high-usage threshold
- Main window is freely resizable by dragging its edges, but has no maximize/fullscreen mode
- About dialog with app version, GitHub link, and an inline "update available" link when a newer release exists
- Optional daily automatic update check against GitHub Releases (toggle in Settings); a tray balloon plus a dialog (View Release / Skip This Version / Remind Me Later) appears when a newer formal release is found

### Installation

Two artifacts are published on the [Releases](https://github.com/coldhighsun/ManagedDrive/releases) page for each version — pick one:

- `ManagedDrive-Setup-X.Y.Z.exe` — a guided installer. It detects whether WinFsp and the .NET 10 Desktop Runtime are already installed, silently installs the bundled WinFsp MSI if missing, prompts you to install the .NET Desktop Runtime if missing, and installs ManagedDrive into Program Files with Start Menu/desktop shortcuts. Recommended for most users.
- `ManagedDrive-vX.Y.Z-win-x64-portable.zip` — small download; requires WinFsp and the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) to be installed separately.

If using the ZIP, extract it anywhere and run `ManagedDrive.exe` directly. `ManagedDrive.exe` is a single-file executable — the ZIP contains it plus one small companion `winfsp-msil.dll` (the managed WinFsp interop assembly, which can't be embedded in the single-file bundle) that must stay next to it. The only registry write is the optional "Run at startup" setting; nothing else touches the registry. WinFsp must be installed separately first with the ZIP (see Prerequisites below); the installer handles this automatically.

The ZIP also includes `mdrive.exe`, a companion CLI (see [CLI Usage](#cli-usage) below). Add the extraction folder to your `PATH` to run `mdrive` from any shell.

### Prerequisites

| Requirement | Notes |
|---|---|
| **Windows 10 / 11 (64-bit)** | ARM64 is not currently tested |
| **[WinFsp 2.2.26194 (2026 Beta3)](https://github.com/winfsp/winfsp/releases/tag/v2.2B3)** | Must be installed before running ManagedDrive. Download the installer directly: [winfsp-2.2.26194.msi](https://github.com/winfsp/winfsp/releases/download/v2.2B3/winfsp-2.2.26194.msi) — do not use `winget install WinFsp.WinFsp`, as the winget package lags behind the latest release. The managed assembly `winfsp-msil.dll` is installed to `C:\Program Files (x86)\WinFsp\bin\` and is referenced by the project automatically. |
| **[.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)** | Required for the `-portable` ZIP (framework-dependent). |
| **.NET 10 SDK** | Required to build. |

### Getting Started

```powershell
# 1. Download and install WinFsp 2.2.26194 (2026 Beta3)
# https://github.com/winfsp/winfsp/releases/download/v2.2B3/winfsp-2.2.26194.msi

# 2. Clone the repository
git clone https://github.com/coldhighsun/ManagedDrive
cd ManagedDrive

# 3. Build
dotnet build

# 4. Run
dotnet run --project src/ManagedDrive.App -c Release
```

Alternatively open `ManagedDrive.slnx` in Visual Studio 2022+ and press **F5**.

### Solution Structure

```
ManagedDrive/
├── src/
│   ├── ManagedDrive.Core/          # In-memory file system engine (WinFsp); organized into sub-namespace folders, GlobalUsings.cs global-uses all of them
│   │   ├── FileSystem/             #   FileNode, FileNodeMap, MemoryFileSystem (FileSystemBase, all WinFsp callbacks), WildcardMatcher, DirectoryEnumeration, ContentAccessInfo
│   │   ├── Mounting/               #   DiskOptions (+ ImageCompressionLevel), RamDisk, MountManager, MountOptionsFactory (CLI mount-options merge)
│   │   ├── Persistence/            #   DiskImageSerializer (.mdr format), ImageEncryptionExceptions
│   │   ├── Snapshots/              #   SnapshotManager, SnapshotStore
│   │   ├── Archive/                #   ArchiveNodeMapBuilder (import), ArchiveNodeMapWriter (export)
│   │   └── DiskCreation/           #   CreateDiskOptionsBuilder, ByteUnitConverter — pure, unit-tested validation logic for the App layer's create-disk dialog
│   │
│   └── ManagedDrive.App/           # WPF desktop application
│       ├── Localization/           #   ResourceDictionary strings (en-US, zh-CN)
│       ├── Themes/                 #   AppTheme.xaml styles + light/dark color palettes, ThemeManager
│       ├── Helpers/                #   ByteFormatter, HintHelper (watermark/placeholder text)
│       ├── Infrastructure/         #   RelayCommand
│       ├── Models/                 #   AppConfiguration, DiskProfile
│       ├── Services/               #   SettingsStore, StartupManager, TempDirResetService, ShellContextMenuManager, SystemMemoryInfo, UpdateCheckService, plus six services split out of App.xaml.cs: TrayIconController, TrayTooltipController, DiskNotificationService, TempDirCompatChecker, SessionEndingSaveHandler, WinFspPrerequisite
│       ├── ViewModels/             #   MainViewModel, DiskViewModel
│       ├── Views/                  #   CreateDiskDialog, CloneDiskDialog, DiskContentDialog, RestoreSnapshotDialog, SettingsDialog, SnapshotDiffDialog, ConfirmDialog, AboutDialog, UpdateAvailableDialog, TrayTooltipView, PasswordPromptDialog
│       ├── GlobalUsings.cs         #   Project-wide global using directives
│       ├── MainWindow.xaml(.cs)    #   Main window
│       ├── App.xaml(.cs)           #   Startup/shutdown orchestration and window navigation; tray/notification/TEMP/prerequisite concerns live in Services/ (above)
│       └── Cli/                    #   Named-pipe server forwarding CLI commands into the running app
│
│   ├── ManagedDrive.Cli.Core/      # Shared CLI parsing/protocol library — no reference to ManagedDrive.Core (keeps the pipe-only mdrive.exe client free of winfsp.net/SharpCompress) and no Spectre.Console (keeps ManagedDrive.App's dependency footprint small)
│   │   ├── CliCommandProcessor.cs  #   System.CommandLine subcommands (mount/unmount/format/save/list/exit); returns a structured CliOutcome, not rendered text
│   │   ├── ICliDiskController.cs   #   Abstraction the App layer implements to execute CLI commands
│   │   ├── CliPipeClient.cs        #   Sends a command to the running app's named pipe
│   │   ├── CliPipeProtocol.cs      #   Wire format shared by client and server (structured CliResponse, not pre-rendered text)
│   │   ├── CliMountOverrides.cs    #   Optional per-mount overrides parsed from CLI flags
│   │   ├── ImageCompressionLevel.cs #  Standalone copy of Core's enum of the same name, kept in sync manually; the App layer casts between the two at the CLI/app boundary
│   │   └── ByteFormatter.cs        #   Human-readable byte-size formatting (shared with the App layer)
│   │
│   └── ManagedDrive.Cli/           # `mdrive` console-subsystem entry point (only project referencing Spectre.Console)
│       ├── Program.cs              #   Forwards args over the pipe, auto-launching ManagedDrive.exe if needed
│       └── CliOutputRenderer.cs    #   Renders a CliResponse to the terminal (colors, tables) via Spectre.Console
│
├── tests/
│   └── ManagedDrive.Tests/         # xUnit v3 unit tests (pure-managed code only)
│       ├── FileNodeTests.cs
│       ├── FileNodeMapTests.cs
│       ├── MemoryFileSystemCloneTests.cs
│       ├── DirectoryEnumerationTests.cs
│       ├── WildcardMatchTests.cs
│       ├── DiskImageSerializerTests.cs
│       ├── RamDiskCapacityTests.cs
│       ├── SnapshotManagerTests.cs
│       ├── ArchiveNodeMapBuilderTests.cs
│       ├── ArchiveNodeMapWriterTests.cs
│       ├── MountOptionsFactoryTests.cs
│       ├── CreateDiskOptionsBuilderTests.cs
│       ├── ByteUnitConverterTests.cs
│       └── RecordingProgress.cs           #   Shared IProgress<double> test double
│
└── benchmarks/
    └── ManagedDrive.Benchmarks/    # BenchmarkDotNet throughput/latency benchmarks
        ├── Program.cs
        ├── DriveLetterHelper.cs             #   Picks a free mount point (D:-Z:) for the RAM disk
        ├── SequentialReadWriteBenchmarks.cs #  Sequential read/write at 4 KB and 1 MB
        ├── RandomAccessBenchmarks.cs        #  Random-seek reads and small-file high-frequency writes
        └── ConcurrentAccessBenchmarks.cs    #  Multi-threaded reads/writes to disjoint files, measuring FileNodeMap lock contention
```

### How It Works

ManagedDrive uses **WinFsp** (Windows File System Proxy) to present an in-memory directory tree as a real Windows volume. WinFsp ships a signed kernel driver that acts as a bridge; all file I/O is forwarded to `MemoryFileSystem`, which stores data in .NET byte arrays.

Key classes:

- **`FileNode`** — holds `Fsp.Interop.FileInfo` metadata, a `byte[]` data buffer, a cached leaf name, and a security descriptor.
- **`FileNodeMap`** — a case-insensitive `SortedDictionary<string, FileNode>` that maps full paths to nodes, supports paginated child enumeration via a bounded prefix walk, and tracks total allocated bytes with an incrementally-maintained counter (O(1) reads instead of a full scan). Thread-safe via the C# 13 `Lock` type.
- **`MemoryFileSystem : FileSystemBase`** — overrides all 21 required WinFsp callbacks (`Create`, `Open`, `Read`, `Write`, `Rename`, `CanDelete`, `ReadDirectoryEntry`, etc.) and enforces a configurable capacity ceiling, returning `STATUS_DISK_FULL` when exceeded; memory is not pre-allocated — each `FileNode` holds only the bytes actually written.
- **`RamDisk`** — composes `MemoryFileSystem` with a `FileSystemHost`. The static `Create()` factory mounts the volume and polls until the drive letter is visible in the OS (up to 2.5 s), then broadcasts `SHCNE_DRIVEADD` to refresh Explorer. `Dispose()` unmounts. When an auto-save interval is configured, a background timer saves the image immediately and then on every interval, skipping ticks where nothing has changed since the last save. Independently of that timer, whenever an image path is configured and save-on-exit isn't disabled for the disk, `Dispose()` performs a final save before unmounting — even if no auto-save interval was set — skipped only when nothing changed since the last save, so unmounting, remounting, or exiting never loses an edit or performs a redundant write. A `SaveFailed` event fires whenever an image save or snapshot write fails, including on background auto-save ticks and the final save in `Dispose()` that would otherwise fail silently, so the App layer can surface the error via a tray balloon and the status bar. Optional per-disk password protection keeps a random content-encryption key in memory only, wrapped by the user's password.
- **`MountManager`** — thread-safe registry of active `RamDisk` instances. Fires `DiskMounted` / `DiskUnmounted` events.
- **`DiskImageSerializer`** — reads/writes `.mdr` files (full FS state including metadata, ACLs, and file data), optionally gzip-compressed at a user-selectable level. `Save` accepts an optional `IProgress<double>`, reported per node, that the App layer surfaces as a progress bar.
- **`SnapshotManager` / `SnapshotStore`** — write a timestamped, read-only copy of the disk's contents next to the main `.mdr` image after every save (when snapshot limits are configured), list/prune them, and restore one back onto a live disk. File content is deduplicated by SHA-256 hash into a shared blob store, so snapshots of a mostly-unchanged disk cost little extra space.

### Disk Image Format (`.mdr`)

A little-endian binary format:

| Field | Type | Description |
|---|---|---|
| Magic | `byte[4]` | `MDRD` |
| Version | `int32` | Currently `3` |
| CompressionLevel | `byte` | `ImageCompressionLevel` value (0=None/1=Fastest/2=Optimal/3=SmallestSize) |
| Capacity | `uint64` | Configured capacity in bytes (always plaintext, even when encrypted) |
| VolumeLabel | `string` | Length-prefixed UTF-8 (always plaintext, even when encrypted) |
| *Encryption info* | — | Present only when the image is password-protected: PBKDF2 salt and the wrapped content-encryption key |
| NodeCount | `int32` | Number of nodes that follow |
| *Node entries* | — | Path, metadata (10 fields), security descriptor, file data — gzip-compressed as a block when `CompressionLevel != None`, then AES-256-GCM encrypted on top when the image is password-protected |

Version `1` images (no `CompressionLevel` byte, always uncompressed) and version `2` images (whole node region compressed the same way, no encryption support) remain readable for backward compatibility.

### Snapshot Format

Snapshots use a separate, independent format from `.mdr` images. For a main image `disk.mdr`, snapshots are named `disk.yyyyMMdd-HHmmss.mdr` in the same folder, each a small binary index file (magic `MDRS`) listing every file/directory's metadata plus, for non-empty files, a SHA-256 hash. The actual file content lives in a shared, content-addressed blob store at `disk.snapblobs/` (sharded into 2-character hex subfolders), gzip-compressed per-blob at the disk's configured compression level — identical content across snapshots is stored only once. When the parent disk is password-protected, each blob is additionally AES-256-GCM encrypted with the same key. Pruning old snapshots, or deleting a single one via **Restore Snapshot...**, also garbage-collects any blob no longer referenced by a remaining snapshot; clearing a disk's password deletes all of its snapshots outright, since the old blobs are unrecoverable without the discarded key.

### Settings & Persistence

- Settings are stored as JSON at `%APPDATA%\ManagedDrive\settings.json`, including each disk's own high-usage warning threshold (or its disabled state).
- Windows startup registration uses `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run` (no elevation required).
- Version is derived from git tags (`v`-prefixed, e.g. `v0.1.0`) via MinVer.

### Performance

Read/write, random-access, and small-file throughput measured with [BenchmarkDotNet](https://benchmarkdotnet.org/) on:

| | |
|---|---|
| **CPU** | Intel Core i9-13980HX, 24C/32T, 2.2 GHz base |
| **RAM** | 64 GB |
| **Disk** | KIOXIA KXG8AZNV1T02 NVMe SSD (1 TB) |
| **OS** | Windows 11 Pro (Build 26200) |
| **Runtime** | .NET 10.0.9 · BenchmarkDotNet 0.15.8 |

`SequentialReadWriteBenchmarks` creates or reads a single file via `FileStream` at 4 KB and 1 MB. Writes use `FileOptions.WriteThrough` (no OS write-back cache) and reads use `FileOptions.SequentialScan`.

| File Size | Operation | RAM Disk | NVMe SSD | Ratio |
|---:|---|---:|---:|---:|
| 4 KB | Write | 1.5 MB/s | 0.8 MB/s | **1.9× faster** |
| 4 KB | Read | 2.2 MB/s | 4.5 MB/s | 0.5× slower |
| 1 MB | Write | 279 MB/s | 85 MB/s | **3.3× faster** |
| 1 MB | Read | 547 MB/s | 1,139 MB/s | 0.5× slower |

> **Why are RAM disk reads slower?**  
> Physical disk reads appear fast because the OS page cache keeps recently-written files in DRAM. The RAM disk reads go through WinFsp's kernel–userspace bridge, which adds IPC overhead compared to the direct page-cache path. In workloads where data is written once and read many times from cold cache (e.g. build outputs, temp files that outlive page-cache pressure), the RAM disk will consistently outperform SSD on reads too.
>
> **4 KB note:** small-file results are dominated by file-open/close syscall overhead, not transfer speed.

Raw latency (mean, `[SimpleJob(warmupCount: 2, iterationCount: 3)]`):

| File Size | RamDisk Write | RamDisk Read | NVMe Write | NVMe Read |
|---:|---:|---:|---:|---:|
| 4 KB | 2,582 μs | 1,785 μs | 4,988 μs | 871 μs |
| 1 MB | 3,580 μs | 1,829 μs | 11,740 μs | 878 μs |

`RandomAccessBenchmarks` covers seek-heavy and small-file-heavy patterns not exercised by the sequential benchmarks above:

| Scenario | RAM Disk | NVMe SSD | Ratio |
|---|---:|---:|---:|
| Random 4 KB read (30 seeks over a 16 MB file) | 3.26 ms | 1.00 ms | 3.3× slower |
| 30× small-file (4 KB) create+write | 75.5 ms (2.52 ms/file) | 115.1 ms (3.84 ms/file) | **1.5× faster** |

Random reads are slower on the RAM disk for the same reason sequential reads are (WinFsp kernel–userspace round-trip per I/O), while small-file writes are faster because RAM disk file creation skips physical block allocation and journaling entirely — at the cost of far higher managed memory allocation per operation (see `Alloc Ratio` in the raw BenchmarkDotNet output).

### Running Tests

```powershell
dotnet test tests/ManagedDrive.Tests
```

Tests cover `FileNode` (allocation unit alignment, index numbers, deep-copy cloning), `FileNodeMap` (CRUD, case-insensitive lookup, child pagination, rename, capacity tracking), `MemoryFileSystem` disk-cloning (copying contents between disks, target-too-small and read-only-target rejection, clone independence), directory enumeration and the wildcard glob matcher used by directory listing, `DiskImageSerializer` (save/load round-trips across every compression level, legacy version-1 images, concurrent map mutation during save), archive import/export, `MountOptionsFactory` (the saved-profile/CLI-overrides merge used by `mdrive mount`/`mount-archive`), and `CreateDiskOptionsBuilder`/`ByteUnitConverter` (the create-disk dialog's validation logic, extracted into Core specifically so it's testable without WPF). Mount/unmount integration tests require the WinFsp driver and must be run manually.

### Running Benchmarks

WinFsp must be installed. The benchmark project auto-selects the first free drive letter between `D:` and `Z:` — no manual configuration needed.

```powershell
dotnet run --project benchmarks/ManagedDrive.Benchmarks -c Release
```

BenchmarkDotNet will prompt you to pick which benchmark class(es) to run (`SequentialReadWriteBenchmarks`, `RandomAccessBenchmarks`, `ConcurrentAccessBenchmarks`, or any combination). Results are written to `BenchmarkDotNet.Artifacts/results/` in the working directory.

### CLI Usage

`mdrive.exe` ships alongside `ManagedDrive.exe` and forwards commands to the running app over a named pipe, so scripts can drive ManagedDrive without opening the UI. If the app isn't already running, `mdrive` launches it and retries for up to 10 seconds before giving up.

```powershell
mdrive mount C:\disks\scratch.mdr R: --auto-mount --compression Optimal
mdrive list
mdrive save R:
mdrive format R: --yes
mdrive unmount R:
mdrive exit
```

| Command | Description |
|---|---|
| `mount <image-path> <drive-letter> [options]` | Mounts an existing `.mdr` image at a drive letter. Options: `--read-only`, `--auto-mount`, `--auto-save-minutes`, `--compression <None\|Fastest\|Optimal\|SmallestSize>`, `--max-snapshot-count`, `--max-snapshot-size-mb`, `--high-usage-warn-percent`, `--password`, `--password-file` (mutually exclusive; needed only if the image is encrypted — `--password-file` reads the first line of a file and is recommended over `--password` to avoid exposing it in shell history or the process list). Any option left unset keeps the image's saved profile value (or its default). |
| `mount-archive <archive-path> [drive-letter]` | Imports an archive (zip/7z/rar/tar/...) as a read-only disk and opens it in Explorer once mounted. `drive-letter` is optional — if omitted, the first free letter from `Z:` down to `D:` is used. Used internally by the Explorer right-click menu entry. |
| `unmount <drive-letter>` | Unmounts a mounted disk. |
| `format <drive-letter> --yes` | Deletes all files on a mounted disk. Requires `--yes`/`-y` to confirm. |
| `save <drive-letter>` | Saves a mounted disk's contents to its backing image immediately. |
| `list` | Lists currently mounted disks with usage and capacity. |
| `exit` | Exits the running ManagedDrive application. |

Run `mdrive --help` or `mdrive <command> --help` for the full option list.

### Known Issues

#### Certain installers may fail when TEMP is set to a RAM disk

The core issue is that the installer **executable itself is running from the RAM disk** — not merely that files were extracted there. WinFsp mounts drives in the **current user's session device namespace**. If an installer is extracted to TEMP and then launched by a system-level process — such as the Windows Package Manager service used by winget — that process operates in the global device namespace and cannot resolve user-session drive letters. Attempting to execute such an installer from a path like `Z:\Temp\WinGet\...\setup.exe` fails with:

> `0x800704b3` — The network path was not found

Known affected packages include **WeChatWin_\*.exe** (WeChat installer), **7z\*.exe** (7-Zip installer), and **Git-\*.exe** (Git installer). Not all winget packages are affected — many install without issue.

This is an architectural limitation of WinFsp user-mode file systems and cannot be worked around in user space.

**Recommendation:** If you encounter installation errors, you have two options:
- Restore TEMP to the Windows default using the toolbar button in ManagedDrive, then retry the installation.
- Download the installer directly from the software's official website and run it manually, bypassing winget entirely.

ManagedDrive will warn you when you attempt to set a RAM disk as TEMP. On every subsequent startup, if TEMP still points to a RAM disk, a warning is shown again — reset TEMP to the Windows default to stop the recurring prompt.

### License

MIT

This project bundles [WinFsp](https://winfsp.dev/) and [SharpCompress](https://github.com/adamhathcock/sharpcompress); see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for their copyright and license information.

---

## 中文

基于 .NET 10 和 [WinFsp](https://winfsp.dev) 构建的 Windows RAM 虚拟磁盘管理器。  
创建、挂载并管理内存盘，它们在文件资源管理器中以普通驱动器号的形式呈现。

### 功能特性

**核心功能**
- 同时挂载多个 RAM 磁盘，每个磁盘拥有独立的驱动器号、容量、卷标和只读标志
- 动态内存分配——磁盘容量为上限而非预分配，内存只随实际文件数据占用
- 实时编辑已挂载磁盘（卷标、容量、自动挂载、镜像路径）无需重挂即可生效；更改盘符或只读标志时自动重挂
- NTFS 兼容卷标识，使内存盘可作为需要 NTFS 卷的工具（如 WinGet、Windows Update 暂存、BITS 下载）的目标路径
- 应用启动时自动挂载已保存的磁盘配置
- 格式化磁盘可立即清空所有内容（**格式化**；只读磁盘受保护）

**持久化、快照与克隆**
- 将磁盘内容保存为 `.mdr` 镜像文件，下次挂载时自动还原；也可直接导入已有镜像（**导入磁盘...**）
- 直接导入压缩包（zip、7z、rar、tar 等 [SharpCompress](https://github.com/adamhathcock/sharpcompress) 支持的格式）作为只读磁盘（**导入压缩包...**）——容量与卷标从压缩包内容自动推算
- 可选自动保存（1-60 分钟间隔），并在卸载/退出前自动执行一次收尾保存（可按磁盘通过**退出时保存**单独关闭）；内容未变化时自动跳过，保存失败会通过托盘/状态栏提示
- 可选镜像压缩级别（不压缩／快速／均衡／最高，默认快速）
- 快照／版本历史——按数量和/或大小上限保留快照，相同内容跨快照去重存储，占用空间远小于逻辑大小之和；可随时通过**还原快照...**还原到某个历史版本，也可在其中按需删除某个单独的快照
- 克隆磁盘到另一已挂载磁盘，或导出为新的 `.mdr` 文件（**克隆磁盘...**）
- 可选的 `.mdr` 镜像密码保护（AES-256-GCM 信封加密；密码仅用于加密一个随机生成的每盘专属密钥，因此更改密码无需重新加密文件内容）——在磁盘对话框中通过"加密镜像"选项设置（密码长度需为 8–64 位）；挂载时（包括启动时的自动挂载）若镜像已加密会提示输入密码
- 长耗时操作的进度反馈——保存镜像、导入压缩包、导出磁盘时均会显示带进度条的忙碌遮罩，避免大盘操作时应用看起来无响应

**便利与安全**
- 可选的资源管理器右键集成：在设置中启用后，会为 zip/7z/rar/tar 压缩包添加右键菜单项**"挂载为内存盘 (ManagedDrive)"**，一键挂载，无需先打开应用——若 `ManagedDrive.exe` 尚未运行会自动启动，挂载完成后会自动打开该盘符的资源管理器窗口
- 系统托盘图标，悬浮显示所有磁盘实时使用率及可用系统内存，提供快捷菜单及可选的最小化启动模式；任意已挂载磁盘被读写时，图标会短暂闪烁一次读/写指示
- 主窗口状态栏实时显示当前可用系统内存（每 2 秒刷新）
- 主窗口状态栏同时实时显示最近一次读/写的文件——事件发生时即主动推送（节流至最多每 300 毫秒一次），而非轮询；主窗口最小化到托盘时自动暂停推送，重新显示时自动恢复
- 每磁盘可配置的高用量警告（默认阈值 90%，带回滞防抖）
- 临时目录重定向——将 Windows TEMP/TMP 指向某磁盘的 `Temp` 文件夹，卸载/重挂时自动恢复默认值，TEMP 遗留指向内存盘时会在启动时提示
- 退出确认，并在待处理的镜像保存完成前显示保存遮罩；若 TEMP 指向已挂载磁盘会先重置
- 双击磁盘在资源管理器中打开；右键提供资源管理器/镜像文件夹等快捷方式，或**查看磁盘内容...**，无需离开应用即可查看可排序的 名称/大小/类型 只读树状列表

**界面**
- 双语界面（中文/英文）与浅色/深色主题，均可自动检测或在设置中手动切换，即时生效无需重启
- 一目了然的磁盘卡片，带状态角标（只读、当前临时目录、是否绑定镜像、密码保护）及使用率超阈值时变色的进度条
- 主窗口可通过拖拽边缘自由调整大小，但不支持最大化/全屏
- 关于对话框显示应用版本及 GitHub 仓库链接，如检测到新版本还会内嵌一个"有可用更新"链接
- 可选的每日自动检查更新（在设置中开关），检测到 GitHub 上有新的正式版本时会弹出托盘气泡及对话框（查看发布页/忽略此版本/稍后提醒）

**命令行**
- `mdrive` 命令行工具（随 `ManagedDrive.exe` 一同发布），可通过命名管道对运行中的应用执行 mount/unmount/format/save/list/exit 等脚本化操作
- 若 `ManagedDrive.exe` 尚未运行，会自动启动并等待其就绪后再发送命令

### 安装

[Releases](https://github.com/coldhighsun/ManagedDrive/releases) 页面为每个版本发布了两种安装方式，任选其一：

- `ManagedDrive-Setup-X.Y.Z.exe` —— 引导式安装程序。会自动检测 WinFsp 和 .NET 10 桌面运行时是否已安装，若缺少 WinFsp 会静默安装内置的 WinFsp 安装包，若缺少 .NET 桌面运行时会提示安装，并将 ManagedDrive 安装到 Program Files，创建开始菜单/桌面快捷方式。推荐大多数用户使用。
- `ManagedDrive-vX.Y.Z-win-x64-portable.zip` —— 体积较小；需要单独安装 WinFsp 和 [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)。

若使用 ZIP，解压到任意目录后直接运行 `ManagedDrive.exe` 即可。`ManagedDrive.exe` 是单文件可执行程序——ZIP 中还附带一个体积很小的 `winfsp-msil.dll`（WinFsp 托管互操作程序集，无法打包进单文件中），需与 exe 保持在同一目录下。唯一会写入注册表的操作是可选的"开机自启"设置，除此之外不会写入注册表。使用 ZIP 时仍需提前单独安装 WinFsp（见下方环境要求）；安装程序会自动处理这一步。

ZIP 中还包含 `mdrive.exe`，一个配套的命令行工具（见下方[命令行用法](#命令行用法)）。将解压目录加入 `PATH` 后即可在任意终端中运行 `mdrive`。

### 环境要求

| 要求 | 说明 |
|---|---|
| **Windows 10 / 11（64 位）** | 暂未测试 ARM64 |
| **[WinFsp 2.2.26194（2026 Beta3）](https://github.com/winfsp/winfsp/releases/tag/v2.2B3)** | 必须安装此版本才能运行 ManagedDrive。请直接下载安装包：[winfsp-2.2.26194.msi](https://github.com/winfsp/winfsp/releases/download/v2.2B3/winfsp-2.2.26194.msi)——不要使用 `winget install WinFsp.WinFsp` 安装，因为该 winget 包更新不及时，落后于最新发布版本。托管程序集 `winfsp-msil.dll` 将安装至 `C:\Program Files (x86)\WinFsp\bin\`，项目会自动引用。 |
| **[.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)** | "绿色版"（框架依赖型）ZIP 需要。 |
| **.NET 10 SDK** | 编译所需。 |

### 快速开始

```powershell
# 1. 下载并安装 WinFsp 2.2.26194（2026 Beta3）
# https://github.com/winfsp/winfsp/releases/download/v2.2B3/winfsp-2.2.26194.msi

# 2. 克隆仓库
git clone https://github.com/coldhighsun/ManagedDrive
cd ManagedDrive

# 3. 编译
dotnet build

# 4. 运行
dotnet run --project src/ManagedDrive.App -c Release
```

或者在 Visual Studio 2022+ 中打开 `ManagedDrive.slnx` 并按 **F5**。

### 解决方案结构

```
ManagedDrive/
├── src/
│   ├── ManagedDrive.Core/          # 内存文件系统引擎（WinFsp）；按子命名空间分文件夹组织，GlobalUsings.cs 统一 global-use 所有子命名空间
│   │   ├── FileSystem/             #   FileNode、FileNodeMap、MemoryFileSystem（FileSystemBase，全部 WinFsp 回调）、WildcardMatcher、DirectoryEnumeration、ContentAccessInfo
│   │   ├── Mounting/               #   DiskOptions（含 ImageCompressionLevel）、RamDisk、MountManager、MountOptionsFactory（CLI 挂载选项合并）
│   │   ├── Persistence/            #   DiskImageSerializer（.mdr 格式）、ImageEncryptionExceptions
│   │   ├── Snapshots/              #   SnapshotManager、SnapshotStore
│   │   ├── Archive/                #   ArchiveNodeMapBuilder（导入）、ArchiveNodeMapWriter（导出）
│   │   └── DiskCreation/           #   CreateDiskOptionsBuilder、ByteUnitConverter——App 层创建磁盘对话框的纯校验逻辑，已配单元测试
│   │
│   └── ManagedDrive.App/           # WPF 桌面应用程序
│       ├── Localization/           #   ResourceDictionary 字符串（en-US、zh-CN）
│       ├── Themes/                 #   AppTheme.xaml 样式 + 浅色/深色配色字典、ThemeManager
│       ├── Helpers/                #   ByteFormatter、HintHelper（水印/占位文本）
│       ├── Infrastructure/         #   RelayCommand
│       ├── Models/                 #   AppConfiguration、DiskProfile
│       ├── Services/               #   SettingsStore、StartupManager、TempDirResetService、ShellContextMenuManager、SystemMemoryInfo、UpdateCheckService，以及从 App.xaml.cs 拆出的六个服务：TrayIconController、TrayTooltipController、DiskNotificationService、TempDirCompatChecker、SessionEndingSaveHandler、WinFspPrerequisite
│       ├── ViewModels/             #   MainViewModel、DiskViewModel
│       ├── Views/                  #   CreateDiskDialog、CloneDiskDialog、DiskContentDialog、RestoreSnapshotDialog、SettingsDialog、SnapshotDiffDialog、ConfirmDialog、AboutDialog、UpdateAvailableDialog、TrayTooltipView、PasswordPromptDialog
│       ├── GlobalUsings.cs         #   项目级全局 using 指令
│       ├── MainWindow.xaml(.cs)    #   主窗口
│       ├── App.xaml(.cs)           #   启动/关闭编排与窗口导航；托盘、通知、TEMP、前置检查等职责已拆分至上面的 Services/
│       └── Cli/                    #   将 CLI 命令转发进运行中应用的命名管道服务端
│
│   ├── ManagedDrive.Cli.Core/      # 共享的 CLI 解析/协议库——不引用 ManagedDrive.Core（使仅需管道通信的 mdrive.exe 客户端无需携带 winfsp.net/SharpCompress），也不依赖 Spectre.Console（避免拖大 ManagedDrive.App 的发布体积）
│   │   ├── CliCommandProcessor.cs  #   System.CommandLine 子命令（mount/unmount/format/save/list/exit）；返回结构化的 CliOutcome，而非渲染好的文本
│   │   ├── ICliDiskController.cs   #   App 层实现的接口，用于执行 CLI 命令
│   │   ├── CliPipeClient.cs        #   向运行中应用的命名管道发送命令
│   │   ├── CliPipeProtocol.cs      #   客户端与服务端共用的线上协议格式（结构化的 CliResponse，而非预渲染文本）
│   │   ├── CliMountOverrides.cs    #   由 CLI 参数解析出的可选挂载覆盖项
│   │   ├── ImageCompressionLevel.cs #  Core 中同名枚举的独立副本，需手动保持同步；App 层在 CLI/应用边界做两者间的转换
│   │   └── ByteFormatter.cs        #   人类可读的字节大小格式化（与 App 层共用）
│   │
│   └── ManagedDrive.Cli/           # `mdrive` 控制台子系统入口点（唯一引用 Spectre.Console 的项目）
│       ├── Program.cs              #   将参数通过管道转发，必要时自动启动 ManagedDrive.exe
│       └── CliOutputRenderer.cs    #   用 Spectre.Console 把 CliResponse 渲染为终端输出（颜色、表格）
│
├── tests/
│   └── ManagedDrive.Tests/         # xUnit v3 单元测试（仅纯托管代码）
│       ├── FileNodeTests.cs
│       ├── FileNodeMapTests.cs
│       ├── MemoryFileSystemCloneTests.cs
│       ├── DirectoryEnumerationTests.cs
│       ├── WildcardMatchTests.cs
│       ├── DiskImageSerializerTests.cs
│       ├── RamDiskCapacityTests.cs
│       ├── SnapshotManagerTests.cs
│       ├── ArchiveNodeMapBuilderTests.cs
│       ├── ArchiveNodeMapWriterTests.cs
│       ├── MountOptionsFactoryTests.cs
│       ├── CreateDiskOptionsBuilderTests.cs
│       ├── ByteUnitConverterTests.cs
│       └── RecordingProgress.cs           #   共用的 IProgress<double> 测试替身
│
└── benchmarks/
    └── ManagedDrive.Benchmarks/    # BenchmarkDotNet 吞吐量/延迟基准测试
        ├── Program.cs
        ├── DriveLetterHelper.cs             #   为内存盘自动选择一个空闲盘符（D:-Z:）
        ├── SequentialReadWriteBenchmarks.cs #  4 KB / 1 MB 顺序读写
        ├── RandomAccessBenchmarks.cs        #  随机寻址读取 + 小文件高频写入
        └── ConcurrentAccessBenchmarks.cs    #  多线程并发读写互不重叠的文件，测量 FileNodeMap 锁竞争
```

### 工作原理

ManagedDrive 使用 **WinFsp**（Windows 文件系统代理）将内存目录树呈现为真实的 Windows 卷。WinFsp 附带一个已签名的内核驱动程序作为桥接层；所有文件 I/O 均转发至 `MemoryFileSystem`，后者将数据存储在 .NET 字节数组中。

核心类：

- **`FileNode`** — 持有 `Fsp.Interop.FileInfo` 元数据、`byte[]` 数据缓冲区、缓存的叶子节点名称及安全描述符。
- **`FileNodeMap`** — 不区分大小写的 `SortedDictionary<string, FileNode>`，将完整路径映射到节点，通过有界前缀遍历支持分页子节点枚举，并通过增量维护的计数器追踪已分配字节总量（O(1) 读取，无需全量扫描）。通过 C# 13 `Lock` 类型保证线程安全。
- **`MemoryFileSystem : FileSystemBase`** — 覆写全部 21 个所需的 WinFsp 回调（`Create`、`Open`、`Read`、`Write`、`Rename`、`CanDelete`、`ReadDirectoryEntry` 等），并强制执行可配置的容量上限，超出时返回 `STATUS_DISK_FULL`；内存不预分配——每个 `FileNode` 仅保留实际写入的字节数。
- **`RamDisk`** — 组合 `MemoryFileSystem` 与 `FileSystemHost`。静态工厂方法 `Create()` 挂载卷，并轮询直至驱动器号在系统中可见（最长 2.5 秒），随后向资源管理器广播 `SHCNE_DRIVEADD`。`Dispose()` 执行卸载。配置了自动保存间隔时，后台计时器会立即保存一次镜像，随后按间隔重复保存，若自上次保存后内容未变化则跳过该次保存。与该计时器无关，只要配置了镜像路径且该磁盘未关闭"退出时保存"，`Dispose()` 在卸载前就会执行一次收尾保存——即使从未设置自动保存间隔；同样只在内容未变化时跳过，确保卸载、重新挂载或退出应用时既不遗漏最新的改动，也不产生多余的写入。镜像保存或快照写入失败时会触发 `SaveFailed` 事件——包括原本会被静默吞掉的后台自动保存和 `Dispose()` 收尾保存失败——供 App 层通过托盘气泡通知和状态栏呈现错误。可选的每磁盘密码保护仅在内存中保存一个随机生成的内容加密密钥，由用户密码进行包裹。
- **`MountManager`** — 线程安全的活动 `RamDisk` 实例注册表，提供 `DiskMounted` / `DiskUnmounted` 事件。
- **`DiskImageSerializer`** — 读写 `.mdr` 文件（保存完整文件系统状态，包含元数据、ACL 和文件数据），可选按用户指定的级别进行 gzip 压缩。`Save` 支持可选的 `IProgress<double>` 参数，按节点上报进度，App 层据此驱动进度条。
- **`SnapshotManager` / `SnapshotStore`** — 在配置了快照上限的情况下，每次保存后都会在主 `.mdr` 镜像旁写入一份带时间戳的只读快照副本，并支持列出、清理旧快照及将某个快照还原到实时磁盘。文件内容按 SHA-256 哈希去重存储于共享的块存储中，因此对一个大部分内容未变化的磁盘做快照，额外占用的空间非常小。

### 磁盘镜像格式（`.mdr`）

小端序二进制格式：

| 字段 | 类型 | 说明 |
|---|---|---|
| 魔数 | `byte[4]` | `MDRD` |
| 版本 | `int32` | 当前为 `3` |
| 压缩级别 | `byte` | `ImageCompressionLevel` 取值（0=不压缩/1=快速/2=均衡/3=最高） |
| 容量 | `uint64` | 配置的容量（字节，即便镜像已加密也始终为明文） |
| 卷标 | `string` | 长度前缀 UTF-8（即便镜像已加密也始终为明文） |
| *加密信息* | — | 仅当镜像已加密时存在：PBKDF2 盐值及被包裹的内容加密密钥 |
| 节点数 | `int32` | 后续节点数量 |
| *节点条目* | — | 路径、元数据（10 个字段）、安全描述符、文件数据——压缩级别非 0 时整体经 gzip 压缩，镜像已加密时再整体经 AES-256-GCM 加密 |

版本 `1` 的镜像（不含压缩级别字段，始终不压缩）和版本 `2` 的镜像（节点区整体按同样方式压缩，不支持加密）仍可正常读取，保持向后兼容。

### 快照格式

快照采用与 `.mdr` 镜像完全独立的格式。对于主镜像 `disk.mdr`，其快照命名为同目录下的 `disk.yyyyMMdd-HHmmss.mdr`，每个都是一个小型二进制索引文件（魔数 `MDRS`），列出所有文件/目录的元数据，非空文件还会记录一个 SHA-256 哈希。实际文件内容存储在共享的内容寻址块存储 `disk.snapblobs/` 中（按哈希前 2 位十六进制分片到子文件夹），按磁盘配置的压缩级别逐块 gzip 压缩——多个快照间相同的内容只保存一份。当所属磁盘已设置密码保护时，每个块还会额外使用同一密钥进行 AES-256-GCM 加密。清理旧快照，或通过**还原快照...**删除某个单独的快照时，都会一并垃圾回收不再被任何剩余快照引用的块；清除磁盘密码时会直接删除该磁盘的所有快照，因为旧的块在密钥丢弃后已无法恢复。

### 配置与持久化

- 配置以 JSON 格式存储于 `%APPDATA%\ManagedDrive\settings.json`，包括每个磁盘各自的高用量告警阈值（或已禁用状态）。
- 开机自启通过 `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run` 注册表项实现（无需提升权限）。
- 版本号由 MinVer 从 git 标签派生（`v` 前缀，例如 `v0.1.0`）。

### 性能基准

使用 [BenchmarkDotNet](https://benchmarkdotnet.org/) 测量读写、随机访问及小文件吞吐量，测试环境：

| | |
|---|---|
| **CPU** | Intel Core i9-13980HX，24C/32T，基础频率 2.2 GHz |
| **内存** | 64 GB |
| **磁盘** | KIOXIA KXG8AZNV1T02 NVMe SSD（1 TB） |
| **系统** | Windows 11 Pro（Build 26200） |
| **运行时** | .NET 10.0.9 · BenchmarkDotNet 0.15.8 |

`SequentialReadWriteBenchmarks` 通过 `FileStream` 在 4 KB 和 1 MB 大小下对单个文件进行创建或读取操作。写入使用 `FileOptions.WriteThrough`（禁用操作系统写回缓存），读取使用 `FileOptions.SequentialScan`。

| 文件大小 | 操作 | 内存盘 | NVMe SSD | 倍率 |
|---:|---|---:|---:|---:|
| 4 KB | 写入 | 1.5 MB/s | 0.8 MB/s | **快 1.9×** |
| 4 KB | 读取 | 2.2 MB/s | 4.5 MB/s | 慢 0.5× |
| 1 MB | 写入 | 279 MB/s | 85 MB/s | **快 3.3×** |
| 1 MB | 读取 | 547 MB/s | 1,139 MB/s | 慢 0.5× |

> **为何内存盘读取反而更慢？**  
> 物理磁盘的读取速度之所以快，是因为操作系统页面缓存将最近写入的文件保留在 DRAM 中。内存盘的读取则需要经过 WinFsp 的内核–用户态桥接，引入了额外的 IPC 开销，比直接走页面缓存路径更慢。在数据写入一次、多次读取且页面缓存已失效的场景下（如构建产物、临时文件），内存盘的读取性能同样会超越 SSD。
>
> **4 KB 说明：** 小文件结果主要受文件打开/关闭的系统调用开销主导，不反映实际传输速率。

原始延迟（均值，`[SimpleJob(warmupCount: 2, iterationCount: 3)]`）：

| 文件大小 | 内存盘写入 | 内存盘读取 | NVMe 写入 | NVMe 读取 |
|---:|---:|---:|---:|---:|
| 4 KB | 2,582 μs | 1,785 μs | 4,988 μs | 871 μs |
| 1 MB | 3,580 μs | 1,829 μs | 11,740 μs | 878 μs |

`RandomAccessBenchmarks` 补充了顺序读写未覆盖的寻址密集与小文件密集场景：

| 场景 | 内存盘 | NVMe SSD | 倍率 |
|---|---:|---:|---:|
| 随机 4 KB 读取（对 16 MB 文件随机寻址 30 次） | 3.26 ms | 1.00 ms | 慢 3.3× |
| 30 次小文件（4 KB）创建+写入 | 75.5 ms（2.52 ms/文件） | 115.1 ms（3.84 ms/文件） | **快 1.5×** |

随机读取在内存盘上更慢，原因与顺序读取相同（每次 I/O 都要经过 WinFsp 内核–用户态往返）；而小文件写入更快，是因为内存盘创建文件完全跳过了物理块分配和日志记录——代价是每次操作的托管内存分配量显著更高（详见原始 BenchmarkDotNet 输出中的 `Alloc Ratio`）。

### 运行测试

```powershell
dotnet test tests/ManagedDrive.Tests
```

测试覆盖 `FileNode`（分配单元对齐、索引编号、深拷贝克隆）、`FileNodeMap`（增删改查、大小写无关查找、子节点分页、重命名、容量追踪）、`MemoryFileSystem` 的磁盘克隆逻辑（跨磁盘复制内容、目标容量不足与目标只读时的拒绝、克隆节点的独立性）、目录枚举及目录列表所用的通配符匹配逻辑、`DiskImageSerializer`（各压缩级别的保存/加载往返、旧版本 1 镜像、保存期间并发修改磁盘节点）、压缩包导入/导出、`MountOptionsFactory`（`mdrive mount`/`mount-archive` 所用的已存配置与 CLI 覆盖项合并逻辑），以及 `CreateDiskOptionsBuilder`/`ByteUnitConverter`（创建磁盘对话框的校验逻辑，专门下沉到 Core 以便脱离 WPF 单测）。挂载/卸载集成测试需要 WinFsp 驱动程序，须手动运行。

### 运行基准测试

须已安装 WinFsp。基准测试项目会自动选择 `D:` 到 `Z:` 之间第一个空闲盘符，无需手动配置。

```powershell
dotnet run --project benchmarks/ManagedDrive.Benchmarks -c Release
```

BenchmarkDotNet 会提示你选择要运行的基准测试类（`SequentialReadWriteBenchmarks`、`RandomAccessBenchmarks`、`ConcurrentAccessBenchmarks`，或任意组合）。结果将写入工作目录下的 `BenchmarkDotNet.Artifacts/results/`。

### 命令行用法

`mdrive.exe` 随 `ManagedDrive.exe` 一同发布，通过命名管道将命令转发给正在运行的应用，因此脚本无需打开界面即可操作 ManagedDrive。若应用尚未运行，`mdrive` 会自动启动它，并在最长 10 秒内重试。

```powershell
mdrive mount C:\disks\scratch.mdr R: --auto-mount --compression Optimal
mdrive list
mdrive save R:
mdrive format R: --yes
mdrive unmount R:
mdrive exit
```

| 命令 | 说明 |
|---|---|
| `mount <镜像路径> <盘符> [选项]` | 将已有的 `.mdr` 镜像挂载到指定盘符。可选项：`--read-only`、`--auto-mount`、`--auto-save-minutes`、`--compression <None\|Fastest\|Optimal\|SmallestSize>`、`--max-snapshot-count`、`--max-snapshot-size-mb`、`--high-usage-warn-percent`、`--password`、`--password-file`（二者互斥；仅当镜像已加密时需要——推荐使用 `--password-file`（读取文件首行作为密码）而非 `--password`，以避免密码出现在 shell 历史或进程列表中）。未指定的选项沿用该镜像已保存的配置值（或其默认值）。 |
| `mount-archive <压缩包路径> [盘符]` | 将压缩包（zip/7z/rar/tar 等）作为只读磁盘导入挂载，挂载完成后会自动在资源管理器中打开该盘符。`盘符`可省略——省略时自动从 `Z:` 向下查找第一个可用盘符。资源管理器右键菜单项内部即调用此命令。 |
| `unmount <盘符>` | 卸载已挂载的磁盘。 |
| `format <盘符> --yes` | 清空已挂载磁盘上的所有文件，须加 `--yes`/`-y` 确认。 |
| `save <盘符>` | 立即将已挂载磁盘的内容保存到其绑定的镜像文件。 |
| `list` | 列出当前已挂载的磁盘及其用量与容量。 |
| `exit` | 退出正在运行的 ManagedDrive 应用。 |

运行 `mdrive --help` 或 `mdrive <命令> --help` 可查看完整的选项列表。

### 已知问题

#### 将 TEMP 设为内存盘后，某些安装包可能报错

核心问题在于安装程序**本身正是从内存盘路径运行**，而不仅仅是文件被解压到了内存盘上。WinFsp 将驱动器挂载在**当前用户的会话设备命名空间**中。若安装包被解压到 TEMP 后由系统级进程启动（例如 winget 所使用的 Windows 软件包管理器服务），该进程运行于全局设备命名空间，无法解析用户会话级别的驱动器号。尝试从 `Z:\Temp\WinGet\...\setup.exe` 之类的路径执行安装程序时，会报错：

> `0x800704b3` — 网络路径键入不正确 / The network path was not found

已知受影响的安装包包括 **WeChatWin\_\*.exe**（微信安装程序）、**7z\*.exe**（7-Zip 安装程序）和 **Git-\*.exe**（Git 安装程序）。并非所有 winget 包都受影响——大多数包可正常安装。

这是 WinFsp 用户态文件系统的架构性限制，无法在用户空间层面绕过。

**建议：** 如遇安装报错，可采用以下任一方式解决：
- 通过 ManagedDrive 工具栏的按钮将 TEMP 恢复为 Windows 默认值，再重试安装。
- 直接前往软件官网下载安装包手动安装，绕过 winget。

每次将内存盘设置为临时目录时，ManagedDrive 均会弹出警告提示。此后每次启动，只要 TEMP 仍指向内存盘，警告便会再次弹出——将 TEMP 恢复为 Windows 默认值后即可停止重复提示。

### 许可证

MIT

本项目内置了 [WinFsp](https://winfsp.dev/) 和 [SharpCompress](https://github.com/adamhathcock/sharpcompress)，其版权与许可证信息见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
