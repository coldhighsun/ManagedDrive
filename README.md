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
- Mount multiple RAM disks at once, each with its own drive letter, capacity, volume label and read-only flag
- Dynamic memory allocation — capacity is a ceiling, not a reservation
- Live-edit a mounted disk (label, capacity, auto-mount, image path); changing the drive letter or read-only flag remounts it
- NTFS-compatible, so RAM disks work with tools that require NTFS (WinGet, Windows Update staging, BITS)
- Auto-mount saved profiles on startup
- **Format** instantly clears a disk's contents (read-only disks are protected)

**Persistence, snapshots & cloning**
- Save to a `.mdr` image and restore it on next mount, or import an existing image directly (**Import Disk...**)
- Import an archive (zip, 7z, rar, tar, or anything [SharpCompress](https://github.com/adamhathcock/sharpcompress) reads) as a read-only disk (**Import Archive...**), with capacity/label derived automatically
- Optional auto-save (1–60 min interval) plus a final save before unmount/exit (disableable per disk via **Save on exit**); skipped when nothing changed, failures raise a tray/status-bar notification
- Selectable image compression (Off / Fast / Balanced / Max, default Fast)
- Snapshot / version history capped by count and/or size, deduplicated by content hash; restore via **Restore Snapshot...**, which also lets you delete individual snapshots
- Clone a disk onto another mounted disk or export it to a new `.mdr` file (**Clone Disk...**)
- Optional `.mdr` password protection (AES-256-GCM envelope encryption — the password only wraps a random per-disk key, so changing it never re-encrypts file data); set via "Encrypt Image" in the disk dialog (8–64 characters, with a live strength hint) and prompted for on mount whenever an image is encrypted. Sensitive buffers are zeroed from memory as soon as they're no longer needed.
- Progress bar overlay for long operations (image save, archive import, export) instead of an unresponsive-looking app

**CLI**
- `mdrive` (ships alongside `ManagedDrive.exe`) scripts mount/unmount/format/save/list/exit against the running app over a named pipe
- Auto-launches `ManagedDrive.exe` if needed and waits for it to be ready before sending the command

**Convenience & safety**
- Optional Explorer right-click integration: adds **"Mount as RAM disk (ManagedDrive)"** for zip/7z/rar/tar archives — one click mounts, auto-launching the app if needed and opening the new drive in Explorer
- Tray icon with a hover tooltip (per-disk usage + available memory), quick menu, optional start-minimized mode, and a brief read/write flash on activity
- Available system memory shown live in the status bar (2 s refresh)
- Status bar also shows the most recently accessed file, pushed live (throttled to 300 ms) rather than polled, paused while the window is hidden in the tray
- Per-disk high-usage warning (default 90%, with hysteresis)
- Temp directory redirection to a disk's `Temp` folder, auto-reset on unmount/remount, with a startup warning if TEMP is left on a RAM disk
- Exit confirmation with a saving overlay while pending saves finish; TEMP is reset first if it points at a mounted disk
- Double-click to open a disk in Explorer; right-click for shortcuts or **View Disk Contents...** (a read-only, sortable Name/Size/Type tree)

**UI**
- Bilingual (English / Simplified Chinese) and light/dark themes, both auto-detected with manual override, switching instantly
- Disk cards with status badges (read-only, current-TEMP, backing image, password-protected) and a usage bar that warns past the high-usage threshold
- Freely resizable window, no maximize/fullscreen
- About dialog with version, GitHub link, and an "update available" link when a newer release exists
- Optional daily update check against GitHub Releases; a tray balloon + dialog (View Release / Skip / Remind Later) appears on a new release

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
│       ├── Infrastructure/         #   RelayCommand, PasswordStrengthEstimator
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
│       ├── PasswordStrengthEstimatorTests.cs
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

ManagedDrive uses **WinFsp** (Windows File System Proxy) to present an in-memory directory tree as a real Windows volume, via a signed kernel driver that forwards file I/O to `MemoryFileSystem`, which stores data in .NET byte arrays.

Key classes:

- **`FileNode`** — a node's `Fsp.Interop.FileInfo` metadata, `byte[]` data buffer, cached leaf name, and security descriptor.
- **`FileNodeMap`** — a case-insensitive `SortedDictionary<string, FileNode>` mapping full paths to nodes; supports paginated child enumeration and O(1) allocated-byte tracking. Thread-safe via the C# 13 `Lock` type.
- **`MemoryFileSystem : FileSystemBase`** — implements all 21 WinFsp callbacks (`Create`, `Read`, `Write`, `Rename`, etc.), enforces a configurable capacity ceiling (`STATUS_DISK_FULL` when exceeded), and only allocates the bytes actually written.
- **`RamDisk`** — combines `MemoryFileSystem` with a `FileSystemHost`. `Create()` mounts the volume, waits for the drive letter to appear (up to 2.5 s), and refreshes Explorer; `Dispose()` unmounts. An optional auto-save timer and a final save on `Dispose()` (skippable via "Save on exit") keep the backing image current, skipping saves when nothing changed. A `SaveFailed` event surfaces any save/snapshot failure, including background ones that would otherwise fail silently. Optional per-disk encryption keeps a random content-encryption key in memory only, wrapped by the user's password.
- **`MountManager`** — thread-safe registry of active `RamDisk` instances, firing `DiskMounted`/`DiskUnmounted` events.
- **`DiskImageSerializer`** — reads/writes `.mdr` files (metadata, ACLs, file data), optionally gzip-compressed; `Save` reports progress per node via `IProgress<double>`.
- **`SnapshotManager` / `SnapshotStore`** — write a timestamped, read-only copy of the disk next to its `.mdr` image after each save, list/prune them, and restore one back. Content is deduplicated by SHA-256 into a shared blob store, so snapshots of a mostly-unchanged disk cost little extra space.

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

Version `1` (no `CompressionLevel` byte, always uncompressed) and version `2` (whole node region compressed, no encryption) images remain readable for backward compatibility.

### Snapshot Format

Snapshots use a separate format from `.mdr` images. For `disk.mdr`, snapshots are named `disk.yyyyMMdd-HHmmss.mdr` in the same folder — a small binary index file (magic `MDRS`) listing each file/directory's metadata plus, for non-empty files, a SHA-256 hash. File content lives in a shared, content-addressed blob store at `disk.snapblobs/` (sharded into 2-char hex subfolders), gzip-compressed per blob — identical content across snapshots is stored once. Encrypted disks additionally encrypt each blob with the same key via AES-256-GCM. Pruning or deleting a snapshot (via **Restore Snapshot...**) garbage-collects any now-unreferenced blobs; clearing a disk's password deletes all of its snapshots outright, since old blobs are unrecoverable without the discarded key.

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

`SequentialReadWriteBenchmarks` creates/reads a single file via `FileStream` at 4 KB and 1 MB (writes with `FileOptions.WriteThrough`, reads with `FileOptions.SequentialScan`).

| File Size | Operation | RAM Disk | NVMe SSD | Ratio |
|---:|---|---:|---:|---:|
| 4 KB | Write | 1.5 MB/s | 0.8 MB/s | **1.9× faster** |
| 4 KB | Read | 2.2 MB/s | 4.5 MB/s | 0.5× slower |
| 1 MB | Write | 279 MB/s | 85 MB/s | **3.3× faster** |
| 1 MB | Read | 547 MB/s | 1,139 MB/s | 0.5× slower |

> **Why are RAM disk reads slower?**  
> Physical reads look fast because the OS page cache keeps recent files in DRAM; RAM disk reads instead cross WinFsp's kernel–userspace bridge, adding IPC overhead. For write-once, read-many-from-cold-cache workloads (build outputs, temp files), the RAM disk still wins on reads too.
>
> **4 KB note:** small-file results are dominated by open/close syscall overhead, not transfer speed.

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

Random reads are slower for the same reason as sequential reads (a kernel–userspace round-trip per I/O); small-file writes are faster since RAM disk file creation skips block allocation and journaling — at the cost of higher managed memory allocation per operation (see `Alloc Ratio` in the raw BenchmarkDotNet output).

### Running Tests

```powershell
dotnet test tests/ManagedDrive.Tests
```

Tests cover `FileNode`, `FileNodeMap` (CRUD, lookup, pagination, rename, capacity tracking), `MemoryFileSystem` disk-cloning, directory enumeration and the wildcard matcher, `DiskImageSerializer` (round-trips across compression levels, legacy images, concurrent mutation during save), archive import/export, `MountOptionsFactory`, `CreateDiskOptionsBuilder`/`ByteUnitConverter` (create-disk dialog validation, kept WPF-free for testability), and `PasswordStrengthEstimator`. Mount/unmount integration tests need the WinFsp driver and must be run manually.

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

WinFsp mounts a drive letter into the **current logon session's** device namespace, so processes in another session or logon (a system service in session 0, or an elevated process running under the linked admin token) can't resolve that drive letter. There are two distinct failure modes:

1. **Cross-session drive-letter visibility.** A system-level process (e.g. winget's Package Manager service) launching from `Z:\Temp\...\setup.exe` can't see the drive and fails with `0x800704b3` (*The network path was not found*). Known affected: **WeChatWin_\*.exe**, **7z\*.exe**, **Git-\*.exe**. This class *can* be resolved by the optional SYSTEM helper service (below), which publishes a global (`\GLOBAL??`) symlink for the drive.

2. **MSI installers via the Windows Installer service.** `msiexec`'s server half runs as SYSTEM in session 0 and performs a *volume-identity* query (Mount Manager lookup) on the source volume before reading it. WinFsp's per-session mount is **not registered with the Windows Mount Manager**, so this query fails with system error `1005` (*The volume does not contain a recognized file system*) → MSI error `2755`/`1603`. The helper service's global drive-letter symlink does **not** fix this — the volume still isn't a Mount-Manager-registered system volume. This affects `winget` installs of MSI packages and standalone `.msi` files whose source sits on the RAM disk. **This is out of scope for the helper service** and would require mounting via the Windows Mount Manager (a larger, service-hosted mount rearchitecture).

**Optional SYSTEM helper service** (`ManagedDriveHelper`): when installed and running, it publishes a cross-session global symlink for whichever disk is the current TEMP target, resolving failure mode 1. It does not address failure mode 2.

- **If you used the installer (`ManagedDrive-Setup-*.exe`):** the service is registered and started automatically, and removed automatically on uninstall — no action needed.
- **If you used the portable ZIP:** the service is not installed automatically. To add it yourself, open an elevated (Administrator) terminal in the folder you extracted the ZIP to and run:
  ```
  sc create ManagedDriveHelper binPath= "%cd%\ManagedDriveHelper.exe" start= auto
  sc start ManagedDriveHelper
  ```
  To remove it later:
  ```
  sc stop ManagedDriveHelper
  sc delete ManagedDriveHelper
  ```
  This step is entirely optional — ManagedDrive mounts and works normally without it; skipping it just means failure mode 1 above isn't resolved.

**Fix / workaround for MSI installs:** reset TEMP to the Windows default (toolbar button) before installing MSI-based software, then retry — or download the installer from the vendor and run it manually.

ManagedDrive warns once when TEMP is set to a RAM disk, and again on every startup while it stays that way.

### License

MIT

This project bundles [WinFsp](https://winfsp.dev/) and [SharpCompress](https://github.com/adamhathcock/sharpcompress); see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for their copyright and license information.

---

## 中文

基于 .NET 10 和 [WinFsp](https://winfsp.dev) 构建的 Windows RAM 虚拟磁盘管理器。  
创建、挂载并管理内存盘，它们在文件资源管理器中以普通驱动器号的形式呈现。

### 功能特性

**核心功能**
- 同时挂载多个 RAM 磁盘，各自拥有独立的驱动器号、容量、卷标和只读标志
- 动态内存分配——容量为上限而非预分配
- 实时编辑已挂载磁盘（卷标、容量、自动挂载、镜像路径）；更改盘符或只读标志会自动重挂
- NTFS 兼容，可作为需要 NTFS 卷的工具（WinGet、Windows Update 暂存、BITS）的目标路径
- 启动时自动挂载已保存的磁盘配置
- **格式化**立即清空磁盘内容（只读磁盘受保护）

**持久化、快照与克隆**
- 保存为 `.mdr` 镜像并在下次挂载时还原，或直接导入已有镜像（**导入磁盘...**）
- 导入压缩包（zip/7z/rar/tar 等 [SharpCompress](https://github.com/adamhathcock/sharpcompress) 支持的格式）为只读磁盘（**导入压缩包...**），容量/卷标自动推算
- 可选自动保存（1-60 分钟）及卸载/退出前的收尾保存（可按磁盘通过**退出时保存**关闭）；内容未变时跳过，失败会有托盘/状态栏提示
- 可选镜像压缩级别（不压缩／快速／均衡／最高，默认快速）
- 按数量/大小上限保留的快照版本历史，内容去重存储；通过**还原快照...**还原或删除单个快照
- 克隆磁盘到另一已挂载磁盘，或导出为新 `.mdr` 文件（**克隆磁盘...**）
- 可选 `.mdr` 密码保护（AES-256-GCM 信封加密——密码仅包裹一个随机每盘密钥，改密码无需重新加密文件）；在磁盘对话框中通过"加密镜像"设置（8–64 位，带实时强度提示），加密镜像挂载时会提示输入密码；敏感缓冲区用完即从内存清零
- 长耗时操作（保存镜像、导入/导出压缩包）显示带进度条的忙碌遮罩，避免应用看起来无响应

**便利与安全**
- 可选资源管理器右键集成：为 zip/7z/rar/tar 添加**"挂载为内存盘 (ManagedDrive)"**菜单项，一键挂载并自动启动应用、打开资源管理器
- 托盘图标带悬浮提示（各盘用量+可用内存）、快捷菜单、可选最小化启动，读写活动时短暂闪烁指示
- 状态栏实时显示可用系统内存（2 秒刷新）
- 状态栏同时推送最近访问的文件（节流至 300 毫秒一次，非轮询），窗口最小化到托盘时暂停
- 每磁盘可配置高用量警告（默认 90%，带回滞防抖）
- 临时目录重定向到某磁盘的 `Temp` 文件夹，卸载/重挂自动恢复，TEMP 遗留在内存盘上时启动提示
- 退出确认并显示保存遮罩直至待处理保存完成；TEMP 指向已挂载磁盘时会先重置
- 双击在资源管理器中打开磁盘；右键提供快捷方式或**查看磁盘内容...**（只读、可排序的名称/大小/类型树状列表）

**界面**
- 双语（中/英）及浅色/深色主题，均可自动检测或手动切换，即时生效
- 磁盘卡片带状态角标（只读、当前临时目录、绑定镜像、密码保护）及超阈值变色的使用率进度条
- 窗口可自由拖拽调整大小，不支持最大化/全屏
- 关于对话框显示版本、GitHub 链接，有新版本时显示更新链接
- 可选每日检查更新；发现新版本时弹出托盘气泡+对话框（查看发布页/忽略/稍后提醒）

**命令行**
- `mdrive`（随 `ManagedDrive.exe` 发布）通过命名管道对运行中的应用执行 mount/unmount/format/save/list/exit
- 若应用未运行会自动启动并等待就绪后发送命令

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
│       ├── Infrastructure/         #   RelayCommand, PasswordStrengthEstimator
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
│       ├── PasswordStrengthEstimatorTests.cs
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

ManagedDrive 使用 **WinFsp**（Windows 文件系统代理）将内存目录树呈现为真实的 Windows 卷，通过已签名内核驱动把文件 I/O 转发至 `MemoryFileSystem`，数据存储在 .NET 字节数组中。

核心类：

- **`FileNode`** — 节点的 `Fsp.Interop.FileInfo` 元数据、`byte[]` 数据缓冲区、缓存的叶子名称及安全描述符。
- **`FileNodeMap`** — 不区分大小写的 `SortedDictionary<string, FileNode>`，支持分页子节点枚举和 O(1) 已分配字节追踪，通过 C# 13 `Lock` 保证线程安全。
- **`MemoryFileSystem : FileSystemBase`** — 实现全部 21 个 WinFsp 回调（`Create`、`Read`、`Write`、`Rename` 等），强制容量上限（超出返回 `STATUS_DISK_FULL`），仅分配实际写入的字节。
- **`RamDisk`** — 组合 `MemoryFileSystem` 与 `FileSystemHost`。`Create()` 挂载卷、等待盘符出现（最长 2.5 秒）并刷新资源管理器；`Dispose()` 执行卸载。可选自动保存计时器与 `Dispose()` 时的收尾保存（可通过"退出时保存"关闭）保持镜像最新，内容未变时跳过。`SaveFailed` 事件会上报任何保存/快照失败，包括原本会被静默吞掉的后台失败。可选的每盘加密仅在内存中保留一个由用户密码包裹的随机密钥。
- **`MountManager`** — 线程安全的活动 `RamDisk` 注册表，提供 `DiskMounted`/`DiskUnmounted` 事件。
- **`DiskImageSerializer`** — 读写 `.mdr` 文件（元数据、ACL、文件数据），可选 gzip 压缩；`Save` 通过 `IProgress<double>` 按节点上报进度。
- **`SnapshotManager` / `SnapshotStore`** — 每次保存后在主镜像旁写入带时间戳的只读快照，支持列出、清理及还原。内容按 SHA-256 去重存储，因此对基本未变化的磁盘做快照额外占用很小。

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

版本 `1`（不含压缩级别字段，始终不压缩）和版本 `2`（节点区整体压缩，不支持加密）的镜像仍可正常读取，保持向后兼容。

### 快照格式

快照采用与 `.mdr` 镜像独立的格式。对于主镜像 `disk.mdr`，快照命名为同目录下的 `disk.yyyyMMdd-HHmmss.mdr`——一个小型二进制索引文件（魔数 `MDRS`），列出文件/目录元数据，非空文件附带 SHA-256 哈希。文件内容存储在共享的内容寻址块存储 `disk.snapblobs/` 中（按哈希前 2 位分片），逐块 gzip 压缩——相同内容跨快照只保存一份。加密磁盘的每个块还会用同一密钥做 AES-256-GCM 加密。清理或删除单个快照（**还原快照...**）会垃圾回收不再被引用的块；清除密码会直接删除该磁盘的所有快照，因为旧块在密钥丢弃后已无法恢复。

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

`SequentialReadWriteBenchmarks` 通过 `FileStream` 在 4 KB 和 1 MB 大小下创建/读取单个文件（写入用 `FileOptions.WriteThrough`，读取用 `FileOptions.SequentialScan`）。

| 文件大小 | 操作 | 内存盘 | NVMe SSD | 倍率 |
|---:|---|---:|---:|---:|
| 4 KB | 写入 | 1.5 MB/s | 0.8 MB/s | **快 1.9×** |
| 4 KB | 读取 | 2.2 MB/s | 4.5 MB/s | 慢 0.5× |
| 1 MB | 写入 | 279 MB/s | 85 MB/s | **快 3.3×** |
| 1 MB | 读取 | 547 MB/s | 1,139 MB/s | 慢 0.5× |

> **为何内存盘读取反而更慢？**  
> 物理磁盘读取快是因为页面缓存把最近的文件留在 DRAM 中；内存盘读取要经过 WinFsp 的内核–用户态桥接，多了一层 IPC 开销。在写一次、多次读且缓存已失效的场景（构建产物、临时文件）下，内存盘读取同样会超越 SSD。
>
> **4 KB 说明：** 小文件结果主要受打开/关闭系统调用开销主导，不反映实际传输速率。

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

随机读取更慢的原因与顺序读取相同（每次 I/O 都要经过内核–用户态往返）；小文件写入更快是因为内存盘创建文件跳过了物理块分配和日志记录——代价是托管内存分配量更高（详见 BenchmarkDotNet 输出中的 `Alloc Ratio`）。

### 运行测试

```powershell
dotnet test tests/ManagedDrive.Tests
```

测试覆盖 `FileNode`、`FileNodeMap`（增删改查、查找、分页、重命名、容量追踪）、`MemoryFileSystem` 的磁盘克隆逻辑、目录枚举及通配符匹配、`DiskImageSerializer`（各压缩级别的保存/加载往返、旧版本镜像、并发修改）、压缩包导入/导出、`MountOptionsFactory`、`CreateDiskOptionsBuilder`/`ByteUnitConverter`（下沉到 Core 以便脱离 WPF 单测），以及 `PasswordStrengthEstimator`。挂载/卸载集成测试需要 WinFsp 驱动，须手动运行。

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

WinFsp 把盘符挂载在**当前登录会话（logon session）**的设备命名空间中，因此其他会话或其他登录令牌下的进程（session 0 的系统服务、或提权后跑在链接管理员令牌下的进程）无法解析该盘符。这里有两种不同的失败模式：

1. **跨会话盘符可见性。** 系统级进程（如 winget 的软件包管理器服务）从 `Z:\Temp\...\setup.exe` 启动时看不到该盘，报 `0x800704b3`（*网络路径未找到*）。已知受影响：**WeChatWin\_\*.exe**（微信）、**7z\*.exe**（7-Zip）、**Git-\*.exe**（Git）。这一类**可以**由下面的可选 SYSTEM 辅助服务解决——它会为该盘发布一个全局（`\GLOBAL??`）符号链接。

2. **通过 Windows Installer 服务安装的 MSI。** `msiexec` 的服务端半边以 SYSTEM 身份跑在 session 0，在读取源文件前会对源卷做一次**卷身份查询**（询问卷装载管理器 Mount Manager）。WinFsp 的 per-session 挂载**没有在 Windows Mount Manager 里注册**，所以该查询以系统错误 `1005`（*卷未包含可识别的文件系统*）失败 → MSI 错误 `2755`/`1603`。辅助服务发布的全局盘符符号链接**修不了**这个——卷依然不是 Mount-Manager 注册的系统卷。这会影响 `winget` 安装 MSI 包、以及源文件位于内存盘上的独立 `.msi` 文件。**这不在辅助服务的能力范围内**，根治需要改为通过 Windows Mount Manager 挂载（属于更大的、服务化挂载的重构）。

**可选 SYSTEM 辅助服务**（`ManagedDriveHelper`）：安装并运行后，它会为当前作为 TEMP 目标的那个盘发布跨会话全局符号链接，解决失败模式 1；对失败模式 2 无效。

- **如果你用的是安装包（`ManagedDrive-Setup-*.exe`）：** 该服务会随安装自动注册并启动，卸载时也会自动移除，无需手动操作。
- **如果你用的是便携式 ZIP：** 该服务不会自动安装。需要自己在解压目录下打开一个管理员终端，手动执行：
  ```
  sc create ManagedDriveHelper binPath= "%cd%\ManagedDriveHelper.exe" start= auto
  sc start ManagedDriveHelper
  ```
  之后想移除的话：
  ```
  sc stop ManagedDriveHelper
  sc delete ManagedDriveHelper
  ```
  这一步完全是可选的——不做这一步 ManagedDrive 本身照常挂载和使用，只是上面的失败模式 1 得不到解决。

**MSI 安装的解决办法：** 安装 MSI 类软件前，先用工具栏按钮把 TEMP 恢复为 Windows 默认值再重试；或直接前往官网下载安装包手动安装。

ManagedDrive 会在 TEMP 被设为内存盘时提示一次，此后只要 TEMP 仍指向内存盘，每次启动都会再次提示——恢复默认值即可停止。

### 许可证

MIT

本项目内置了 [WinFsp](https://winfsp.dev/) 和 [SharpCompress](https://github.com/adamhathcock/sharpcompress)，其版权与许可证信息见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
