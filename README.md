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
- Per-disk high-usage warning (50–90% range, default 90%, with hysteresis)
- Temp directory redirection to a disk's `Temp` folder, auto-reset on unmount/remount, with a startup warning if TEMP is left on a RAM disk
- Exit confirmation with a saving overlay while pending saves finish; TEMP is reset first if it points at a mounted disk
- Double-click to open a disk in Explorer; right-click for shortcuts or **View Disk Contents...** (a read-only, sortable Name/Size/Type tree)

**UI**
- Bilingual (English / Simplified Chinese) and light/dark themes, both auto-detected with manual override, switching instantly
- Disk cards with status badges (read-only, current-TEMP, backing image, password-protected), a usage bar that warns past the high-usage threshold, and a live read/write throughput chart
- Freely resizable window, no maximize/fullscreen
- About dialog with version, GitHub link, and an "update available" link when a newer release exists
- Optional daily update check against GitHub Releases; a tray balloon + dialog (View Release / Skip / Remind Later) appears on a new release

### Installation

```powershell
winget install coldhighsun.ManagedDrive
```

Or download an artifact directly from the [Releases](https://github.com/coldhighsun/ManagedDrive/releases) page — pick one:

- `ManagedDrive-Setup-X.Y.Z.exe` — a guided installer. It detects whether WinFsp and the .NET 10 Desktop Runtime are already installed, silently installs the bundled WinFsp MSI if missing, prompts you to install the .NET Desktop Runtime if missing, and installs ManagedDrive into Program Files with Start Menu/desktop shortcuts. Recommended for most users.
- `ManagedDrive-vX.Y.Z-win-x64-portable.zip` — small download; requires WinFsp and the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) to be installed separately.

If using the ZIP, extract it anywhere and run `ManagedDrive.exe` directly. `ManagedDrive.exe` is a single-file executable — the ZIP contains it plus one small companion `winfsp-msil.dll` (the managed WinFsp interop assembly, which can't be embedded in the single-file bundle) that must stay next to it. The only registry write is the optional "Run at startup" setting; nothing else touches the registry. WinFsp must be installed separately first with the ZIP (see Prerequisites below); the installer handles this automatically.

The ZIP also includes `mdrive.exe`, a companion CLI (see [CLI Usage](#cli-usage) below), and `wingetx.exe`, a `winget` wrapper (see [wingetx: winget wrapper](#wingetx-winget-wrapper) below). Add the extraction folder to your `PATH` to run `mdrive`/`wingetx` from any shell. The installer adds both to the machine-wide `PATH` automatically.

### Prerequisites

| Requirement | Notes |
|---|---|
| **Windows 10 / 11 (64-bit)** | ARM64 is not currently tested |
| **[WinFsp 2.2.26215 (2026 Beta4)](https://github.com/winfsp/winfsp/releases/tag/v2.2B4)** | Must be installed before running ManagedDrive. Download the installer directly: [winfsp-2.2.26215.msi](https://github.com/winfsp/winfsp/releases/download/v2.2B4/winfsp-2.2.26215.msi) — do not use `winget install WinFsp.WinFsp`, as the winget package lags behind the latest release. The managed assembly `winfsp-msil.dll` is installed to `C:\Program Files (x86)\WinFsp\bin\` and is referenced by the project automatically. |
| **[.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)** | Required for the `-portable` ZIP (framework-dependent). |
| **.NET 10 SDK** | Required to build. |

### CLI Usage

`mdrive.exe` ships alongside `ManagedDrive.exe` and forwards commands to the running app over a named pipe, so scripts can drive ManagedDrive without opening the UI. If the app isn't already running, `mdrive` launches it and retries for up to 10 seconds before giving up.

```powershell
mdrive mount C:\disks\scratch.mdr R: --auto-mount --compression Optimal --custom-zstd-level 19
mdrive list
mdrive save R:
mdrive format R: --yes
mdrive unmount R:
mdrive exit
```

| Command | Description |
|---|---|
| `mount <image-path> <drive-letter> [options]` | Mounts an existing `.mdr` image at a drive letter. Options: `--read-only`, `--auto-mount`, `--auto-save-minutes`, `--compression <None\|Fastest\|Optimal\|SmallestSize>`, `--custom-zstd-level <1-22>` (overrides the preset Zstd level mapped from `--compression`; only takes effect when the compression level is not `None`), `--max-snapshot-count`, `--max-snapshot-size-mb`, `--high-usage-warn-percent`, `--password`, `--password-file` (mutually exclusive; needed only if the image is encrypted — `--password-file` reads the first line of a file and is recommended over `--password` to avoid exposing it in shell history or the process list). Any option left unset keeps the image's saved profile value (or its default). |
| `mount-archive <archive-path> [drive-letter]` | Imports an archive (zip/7z/rar/tar/...) as a read-only disk and opens it in Explorer once mounted. `drive-letter` is optional — if omitted, the first free letter from `Z:` down to `D:` is used. Used internally by the Explorer right-click menu entry. |
| `unmount <drive-letter>` | Unmounts a mounted disk. |
| `format <drive-letter> --yes` | Deletes all files on a mounted disk. Requires `--yes`/`-y` to confirm. |
| `save <drive-letter>` | Saves a mounted disk's contents to its backing image immediately. |
| `list` | Lists currently mounted disks with usage and capacity. |
| `exit` | Exits the running ManagedDrive application. |

Run `mdrive --help` or `mdrive <command> --help` for the full option list.

### wingetx: winget wrapper

`wingetx.exe` is a transparent wrapper around `winget` that ships alongside `ManagedDrive.exe`/`mdrive.exe`. Use it as a drop-in replacement for `winget`:

```powershell
wingetx install <package>
wingetx upgrade <package>
```

If `%TEMP%` isn't currently on a ManagedDrive volume, or the requested subcommand isn't `install`/`upgrade`, `wingetx` just forwards the call to `winget.exe` unchanged — so it's always safe to alias `winget` to `wingetx`.

When `%TEMP%` **is** set to a ManagedDrive volume, `wingetx install`/`wingetx upgrade` routes MSI- and exe-based packages through `winget download` followed by a manual launch of the downloaded installer (`msiexec` for MSI/WiX, the installer exe directly otherwise), instead of a plain `winget install`. This sidesteps both failure modes described in [Known Issues](#known-issues) below: `msiexec`'s Mount-Manager source-volume check, and the cross-session exit-code-1 issue affecting exe installers. Installer types it can't confidently handle this way (msix, appx, zip, portable, ...) are forwarded to plain `winget install`/`upgrade` automatically.

- The installer's UI stays visible (`SilentWithProgress` switches) unless `--silent` or `--disable-interactivity` is passed, matching `winget`'s own behavior.
- The downloaded installer is staged in `%LOCALAPPDATA%\Temp\wingetx` — a real, non-WinFsp volume — before it's launched.

### Known Issues

#### Certain installers may fail when TEMP is set to a RAM disk

WinFsp mounts a drive letter into the **current logon session's** device namespace, so processes in another session or logon (a session-0 system service, or an elevated process under the linked admin token) can't resolve it. Two distinct failure modes result:

1. **Cross-session drive-letter visibility** — a system-level process (e.g. winget's Package Manager service) launching from `Z:\Temp\...\setup.exe` fails with `0x800704b3` (*The network path was not found*). Known affected: **WeChatWin_\*.exe**, **7z\*.exe**, **Git-\*.exe**. Fixed by the optional SYSTEM helper service below, which publishes a global (`\GLOBAL??`) symlink for the drive.
2. **MSI installers via the Windows Installer service** — `msiexec`'s SYSTEM/session-0 half does a Mount Manager volume-identity query on the source volume before reading it; WinFsp's per-session mount isn't Mount-Manager-registered, so the query fails with system error `1005` → MSI error `2755`/`1603`. The helper service's symlink doesn't fix this — the volume still isn't Mount-Manager-registered. Affects `winget` MSI installs and standalone `.msi` files sourced from the RAM disk; a proper fix would need a larger Mount-Manager-based mount rearchitecture, out of scope for the helper service.

**Optional SYSTEM helper service** (`ManagedDriveHelper`) resolves failure mode 1 by publishing a cross-session global symlink for whichever disk is the current TEMP target; it does not address failure mode 2.

- Installer builds (`ManagedDrive-Setup-*.exe`) register and remove the service automatically — no action needed.
- Portable ZIP: install it yourself from an elevated (Administrator) terminal in the extracted folder:
  ```
  sc create ManagedDriveHelper binPath= "%cd%\ManagedDriveHelper.exe" start= auto
  sc start ManagedDriveHelper
  ```
  Remove later with `sc stop ManagedDriveHelper` then `sc delete ManagedDriveHelper`. Entirely optional — ManagedDrive works normally without it; skipping it just leaves failure mode 1 unresolved.

**Fixing MSI installs:** reset TEMP to the Windows default (toolbar button) before installing MSI-based software, then retry — or download the installer from the vendor and run it manually. Or use [`wingetx`](#wingetx-winget-wrapper) in place of `winget`, which works around both failure modes without touching TEMP.

ManagedDrive warns once when TEMP is set to a RAM disk, and again on every startup while it stays that way.

### Settings & Persistence

- Settings are stored as JSON at `%APPDATA%\ManagedDrive\settings.json`, including each disk's own high-usage warning threshold (or its disabled state).
- Windows startup registration uses `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run` (no elevation required).
- Version is derived from git tags (`v`-prefixed, e.g. `v0.1.0`) via MinVer.

## For Developers

### Getting Started

```powershell
# 1. Download and install WinFsp 2.2.26215 (2026 Beta4)
# https://github.com/winfsp/winfsp/releases/download/v2.2B4/winfsp-2.2.26215.msi

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
│   ├── ManagedDrive.Core/              # In-memory file system engine (WinFsp), no UI dependency
│   │   ├── FileSystem/                 #   FileNode, FileNodeMap, MemoryFileSystem, WildcardMatcher, DirectoryEnumeration
│   │   ├── Mounting/                   #   DiskOptions, RamDisk, MountManager, MountOptionsFactory
│   │   ├── Persistence/                #   DiskImageSerializer (.mdr format)
│   │   ├── Snapshots/                  #   SnapshotManager, SnapshotStore
│   │   ├── Archive/                    #   ArchiveNodeMapBuilder (import), ArchiveNodeMapWriter (export)
│   │   └── DiskCreation/               #   CreateDiskOptionsBuilder, ByteUnitConverter
│   ├── ManagedDrive.App/               # WPF desktop application — tray icon, dialogs, settings, localization/theming
│   ├── ManagedDrive.Cli.Core/          # Shared CLI parsing/protocol library (System.CommandLine + named-pipe wire format)
│   ├── ManagedDrive.Cli/               # `mdrive.exe`, the console entry point
│   ├── ManagedDrive.HelperProtocol/    # Named-pipe protocol shared between the app and the SYSTEM helper service
│   ├── ManagedDrive.Service/           # `ManagedDriveHelper.exe`, optional LocalSystem service publishing global DOS-device symlinks for cross-session TEMP visibility (see Known Issues)
│   └── ManagedDrive.WingetExtension/   # `wingetx.exe`, a transparent winget wrapper (see wingetx: winget wrapper)
├── tests/
│   └── ManagedDrive.Tests/             # xUnit v3 unit tests (pure-managed code only)
└── benchmarks/
    └── ManagedDrive.Benchmarks/        # BenchmarkDotNet throughput/latency benchmarks
```

### How It Works

ManagedDrive uses **WinFsp** (Windows File System Proxy) to present an in-memory directory tree as a real Windows volume: a signed kernel driver forwards file I/O to a managed file system implementation that stores data in .NET byte arrays and enforces a configurable capacity ceiling. Mounting/unmounting, save/restore to a `.mdr` image, and snapshot history are all handled by `ManagedDrive.Core` (see `CLAUDE.md` for the class-level architecture).

### Disk Image & Snapshot Format

`.mdr` images are a versioned, little-endian binary format (magic `MDRD`) with optional gzip compression and optional AES-256-GCM password-based encryption; large disks stream to/from the file and encrypt in chunks rather than buffering the whole image in memory. Snapshots use a separate format (magic `MDRS`) stored next to the main image, with file content deduplicated by SHA-256 into a shared blob store so snapshots of a mostly-unchanged disk cost little extra space. Both formats stay backward-compatible with older versions produced by earlier releases. See `CLAUDE.md` for the exact binary layout.

### Performance

Measured with [BenchmarkDotNet](https://benchmarkdotnet.org/) (Intel Core i9-13980HX, 64 GB RAM, KIOXIA KXG8AZNV1T02 NVMe SSD, Windows 11 Pro, .NET 10.0.10):

| Scenario | RAM Disk | NVMe SSD | Ratio |
|---|---:|---:|---:|
| Sequential write, 4 KB | 2.4 MB/s | 1.3 MB/s | **RAM 1.9× faster** |
| Sequential write, 1 MB | 561.8 MB/s | 137.4 MB/s | **RAM 4.1× faster** |
| Sequential read (OS cache), 4 KB | 6.0 MB/s | 8.7 MB/s | NVMe 1.4× faster |
| Sequential read (OS cache), 1 MB | 938.5 MB/s | 2,143.3 MB/s | NVMe 2.3× faster |
| Random 4 KB read (uncached), 30 seeks | 1.36 ms | 2.18 ms | **RAM 1.6× faster** |
| Random 4 KB read (OS cache), 30 seeks | 1.36 ms | 0.52 ms | NVMe 2.6× faster |
| 30× small-file (4 KB) create+write | 47.4 ms (1.58 ms/file) | 79.9 ms (2.66 ms/file) | **RAM 1.7× faster** |

Writes win big (up to 4.1×) by skipping block allocation, journaling, and the physical write. Uncached random reads benefit from zero seek latency (1.6× faster). Small-file creates are also faster (1.7×) because metadata operations stay in memory. Cached reads, however, favor the NVMe path — NTFS reads from the OS page cache stay entirely in-kernel, while the RAM disk incurs an extra kernel–userspace round trip through WinFsp. Run `dotnet run --project benchmarks/ManagedDrive.Benchmarks -c Release` for current numbers on your own hardware (see [Running Benchmarks](#running-benchmarks) below).

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
- 每磁盘可配置高用量警告（范围 50%–90%，默认 90%，带回滞防抖）
- 临时目录重定向到某磁盘的 `Temp` 文件夹，卸载/重挂自动恢复，TEMP 遗留在内存盘上时启动提示
- 退出确认并显示保存遮罩直至待处理保存完成；TEMP 指向已挂载磁盘时会先重置
- 双击在资源管理器中打开磁盘；右键提供快捷方式或**磁盘内容...**（可排序的名称/大小/类型树状列表，支持多选删除；只读磁盘禁用删除）

**界面**
- 双语（中/英）及浅色/深色主题，均可自动检测或手动切换，即时生效
- 磁盘卡片带状态角标（只读、当前临时目录、绑定镜像、密码保护）、超阈值变色的使用率进度条，以及实时读写速度曲线图
- 窗口可自由拖拽调整大小，不支持最大化/全屏
- 关于对话框显示版本、GitHub 链接，有新版本时显示更新链接
- 可选每日检查更新；发现新版本时弹出托盘气泡+对话框（查看发布页/忽略/稍后提醒）

**命令行**
- `mdrive`（随 `ManagedDrive.exe` 发布）通过命名管道对运行中的应用执行 mount/unmount/format/save/list/exit
- 若应用未运行会自动启动并等待就绪后发送命令

### 安装

```powershell
winget install coldhighsun.ManagedDrive
```

或前往 [Releases](https://github.com/coldhighsun/ManagedDrive/releases) 页面手动下载，每个版本发布了两种安装方式，任选其一：

- `ManagedDrive-Setup-X.Y.Z.exe` —— 引导式安装程序。会自动检测 WinFsp 和 .NET 10 桌面运行时是否已安装，若缺少 WinFsp 会静默安装内置的 WinFsp 安装包，若缺少 .NET 桌面运行时会提示安装，并将 ManagedDrive 安装到 Program Files，创建开始菜单/桌面快捷方式。推荐大多数用户使用。
- `ManagedDrive-vX.Y.Z-win-x64-portable.zip` —— 体积较小；需要单独安装 WinFsp 和 [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)。

若使用 ZIP，解压到任意目录后直接运行 `ManagedDrive.exe` 即可。`ManagedDrive.exe` 是单文件可执行程序——ZIP 中还附带一个体积很小的 `winfsp-msil.dll`（WinFsp 托管互操作程序集，无法打包进单文件中），需与 exe 保持在同一目录下。唯一会写入注册表的操作是可选的"开机自启"设置，除此之外不会写入注册表。使用 ZIP 时仍需提前单独安装 WinFsp（见下方环境要求）；安装程序会自动处理这一步。

ZIP 中还包含 `mdrive.exe`（配套命令行工具，见下方[命令行用法](#cli-usage-zh)）和 `wingetx.exe`（`winget` 包装工具，见下方[wingetx: winget 包装工具](#wingetx-wrapper-zh)）。将解压目录加入 `PATH` 后即可在任意终端中运行 `mdrive`/`wingetx`。安装程序会自动将两者加入系统级 `PATH`。

### 环境要求

| 要求 | 说明 |
|---|---|
| **Windows 10 / 11（64 位）** | 暂未测试 ARM64 |
| **[WinFsp 2.2.26215（2026 Beta4）](https://github.com/winfsp/winfsp/releases/tag/v2.2B4)** | 必须安装此版本才能运行 ManagedDrive。请直接下载安装包：[winfsp-2.2.26215.msi](https://github.com/winfsp/winfsp/releases/download/v2.2B4/winfsp-2.2.26215.msi)——不要使用 `winget install WinFsp.WinFsp` 安装，因为该 winget 包更新不及时，落后于最新发布版本。托管程序集 `winfsp-msil.dll` 将安装至 `C:\Program Files (x86)\WinFsp\bin\`，项目会自动引用。 |
| **[.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)** | "绿色版"（框架依赖型）ZIP 需要。 |
| **.NET 10 SDK** | 编译所需。 |

<a id="cli-usage-zh"></a>
### 命令行用法

`mdrive.exe` 随 `ManagedDrive.exe` 一同发布，通过命名管道将命令转发给正在运行的应用，因此脚本无需打开界面即可操作 ManagedDrive。若应用尚未运行，`mdrive` 会自动启动它，并在最长 10 秒内重试。

```powershell
mdrive mount C:\disks\scratch.mdr R: --auto-mount --compression Optimal --custom-zstd-level 19
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

<a id="wingetx-wrapper-zh"></a>
### wingetx: winget 包装工具

`wingetx.exe` 是 `winget` 的透明包装工具，随 `ManagedDrive.exe`/`mdrive.exe` 一同发布。可直接把它当作 `winget` 的替代品使用：

```powershell
wingetx install <包名>
wingetx upgrade <包名>
```

如果 `%TEMP%` 当前不在 ManagedDrive 内存盘上，或所调用的子命令不是 `install`/`upgrade`，`wingetx` 会原样把调用转发给 `winget.exe`——因此把 `winget` 直接别名为 `wingetx` 始终是安全的。

当 `%TEMP%` **确实**设为 ManagedDrive 内存盘时，`wingetx install`/`wingetx upgrade` 会将 MSI 及 exe 类型的包改为先执行 `winget download`，再手动启动下载好的安装程序（MSI/WiX 用 `msiexec`，其余直接运行安装包本身），而不是直接执行 `winget install`。这样可以绕开下方[已知问题](#known-issues-zh)中描述的两种失败模式：`msiexec` 的卷装载管理器（Mount Manager）源卷检查，以及影响 exe 安装包的跨会话退出码 1 问题。它无法确信处理的安装包类型（msix、appx、zip、便携版等）会自动转发给普通的 `winget install`/`upgrade`。

- 除非传入 `--silent` 或 `--disable-interactivity`，安装程序界面默认保持可见（`SilentWithProgress` 开关），与 `winget` 自身行为一致。
- 下载的安装包会先暂存到 `%LOCALAPPDATA%\Temp\wingetx`（一个真实的、非 WinFsp 的卷）再启动。

<a id="known-issues-zh"></a>
### 已知问题

#### 将 TEMP 设为内存盘后，某些安装包可能报错

WinFsp 把盘符挂载在**当前登录会话（logon session）**的设备命名空间中，因此其他会话或登录令牌下的进程（session 0 的系统服务、或提权后跑在链接管理员令牌下的进程）无法解析该盘符，由此产生两种失败模式：

1. **跨会话盘符可见性**——系统级进程（如 winget 的软件包管理器服务）从 `Z:\Temp\...\setup.exe` 启动时看不到该盘，报 `0x800704b3`（*网络路径未找到*）。已知受影响：**WeChatWin\_\*.exe**（微信）、**7z\*.exe**（7-Zip）、**Git-\*.exe**（Git）。可由下方可选的 SYSTEM 辅助服务解决——它会为该盘发布一个全局（`\GLOBAL??`）符号链接。
2. **通过 Windows Installer 服务安装的 MSI**——`msiexec` 以 SYSTEM 身份跑在 session 0 的那一半，在读取源文件前会对源卷做一次卷身份查询（询问 Mount Manager）；WinFsp 的 per-session 挂载没有在 Mount Manager 里注册，所以该查询以系统错误 `1005` 失败 → MSI 错误 `2755`/`1603`。辅助服务的符号链接修不了这个——卷依然不是 Mount-Manager 注册的系统卷。影响 `winget` 安装 MSI 包及源文件位于内存盘上的独立 `.msi` 文件；根治需要改为通过 Windows Mount Manager 挂载（更大的服务化挂载重构），不在辅助服务的能力范围内。

**可选 SYSTEM 辅助服务**（`ManagedDriveHelper`）通过为当前 TEMP 目标盘发布跨会话全局符号链接来解决失败模式 1，对失败模式 2 无效。

- 安装包版本（`ManagedDrive-Setup-*.exe`）会自动注册/移除该服务，无需手动操作。
- 便携式 ZIP：需自己在解压目录下打开管理员终端手动执行：
  ```
  sc create ManagedDriveHelper binPath= "%cd%\ManagedDriveHelper.exe" start= auto
  sc start ManagedDriveHelper
  ```
  之后可用 `sc stop ManagedDriveHelper` 再 `sc delete ManagedDriveHelper` 移除。完全是可选的——不做这一步 ManagedDrive 照常挂载和使用，只是失败模式 1 得不到解决。

**MSI 安装的解决办法：** 安装 MSI 类软件前，先用工具栏按钮把 TEMP 恢复为 Windows 默认值再重试；或直接前往官网下载安装包手动安装；也可以用 [`wingetx`](#wingetx-wrapper-zh) 代替 `winget`——它无需重置 TEMP 即可绕开上述两种失败模式。

ManagedDrive 会在 TEMP 被设为内存盘时提示一次，此后只要 TEMP 仍指向内存盘，每次启动都会再次提示——恢复默认值即可停止。

### 配置与持久化

- 配置以 JSON 格式存储于 `%APPDATA%\ManagedDrive\settings.json`，包括每个磁盘各自的高用量告警阈值（或已禁用状态）。
- 开机自启通过 `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run` 注册表项实现（无需提升权限）。
- 版本号由 MinVer 从 git 标签派生（`v` 前缀，例如 `v0.1.0`）。

## 开发者内容

### 快速开始

```powershell
# 1. 下载并安装 WinFsp 2.2.26215（2026 Beta4）
# https://github.com/winfsp/winfsp/releases/download/v2.2B4/winfsp-2.2.26215.msi

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
│   ├── ManagedDrive.Core/              # 内存文件系统引擎（WinFsp），不依赖任何 UI
│   │   ├── FileSystem/                 #   FileNode、FileNodeMap、MemoryFileSystem、WildcardMatcher、DirectoryEnumeration
│   │   ├── Mounting/                   #   DiskOptions、RamDisk、MountManager、MountOptionsFactory
│   │   ├── Persistence/                #   DiskImageSerializer（.mdr 格式）
│   │   ├── Snapshots/                  #   SnapshotManager、SnapshotStore
│   │   ├── Archive/                    #   ArchiveNodeMapBuilder（导入）、ArchiveNodeMapWriter（导出）
│   │   └── DiskCreation/               #   CreateDiskOptionsBuilder、ByteUnitConverter
│   ├── ManagedDrive.App/               # WPF 桌面应用程序——托盘图标、各类对话框、设置、多语言/主题
│   ├── ManagedDrive.Cli.Core/          # 共享的 CLI 解析/协议库（System.CommandLine + 命名管道协议）
│   ├── ManagedDrive.Cli/               # `mdrive.exe` 控制台入口点
│   ├── ManagedDrive.HelperProtocol/    # 应用与 SYSTEM 辅助服务之间共享的命名管道协议
│   ├── ManagedDrive.Service/           # `ManagedDriveHelper.exe`——可选的 LocalSystem 服务，发布全局 DOS 设备符号链接以实现跨会话 TEMP 可见性（见"已知问题"）
│   └── ManagedDrive.WingetExtension/   # `wingetx.exe`——透明的 winget 包装工具（见"wingetx: winget 包装工具"）
├── tests/
│   └── ManagedDrive.Tests/             # xUnit v3 单元测试（仅纯托管代码）
└── benchmarks/
    └── ManagedDrive.Benchmarks/        # BenchmarkDotNet 吞吐量/延迟基准测试
```

### 工作原理

ManagedDrive 使用 **WinFsp**（Windows 文件系统代理）将内存目录树呈现为真实的 Windows 卷：已签名内核驱动把文件 I/O 转发至托管文件系统实现，数据存储在 .NET 字节数组中，并强制容量上限。挂载/卸载、保存/还原为 `.mdr` 镜像、快照历史等均由 `ManagedDrive.Core` 负责（类级别架构见 `CLAUDE.md`）。

### 磁盘镜像与快照格式

`.mdr` 镜像是带版本号的小端序二进制格式（魔数 `MDRD`），可选 gzip 压缩和基于密码的 AES-256-GCM 加密；大磁盘会流式读写文件并分块加密，而非把整个镜像缓冲到内存中。快照采用独立格式（魔数 `MDRS`），存放在主镜像旁，文件内容按 SHA-256 去重存储到共享的块存储中，因此对基本未变化的磁盘做快照额外占用很小。两种格式都会保持对旧版本发布产物的向后兼容。具体二进制布局见 `CLAUDE.md`。

### 性能基准

使用 [BenchmarkDotNet](https://benchmarkdotnet.org/) 测量（Intel Core i9-13980HX、64 GB 内存、KIOXIA KXG8AZNV1T02 NVMe SSD、Windows 11 Pro、.NET 10.0.10）：

| 场景 | 内存盘 | NVMe SSD | 倍率 |
|---|---:|---:|---:|
| 顺序写入，4 KB | 2.4 MB/s | 1.3 MB/s | **内存盘快 1.9×** |
| 顺序写入，1 MB | 561.8 MB/s | 137.4 MB/s | **内存盘快 4.1×** |
| 顺序读取（OS 缓存），4 KB | 6.0 MB/s | 8.7 MB/s | NVMe 快 1.4× |
| 顺序读取（OS 缓存），1 MB | 938.5 MB/s | 2,143.3 MB/s | NVMe 快 2.3× |
| 随机 4 KB 读取（未缓存），30 次寻址 | 1.36 ms | 2.18 ms | **内存盘快 1.6×** |
| 随机 4 KB 读取（OS 缓存），30 次寻址 | 1.36 ms | 0.52 ms | NVMe 快 2.6× |
| 30 次小文件（4 KB）创建+写入 | 47.4 ms（1.58 ms/文件） | 79.9 ms（2.66 ms/文件） | **内存盘快 1.7×** |

写入优势显著（最高 4.1×），因为跳过了物理块分配、日志记录和实际落盘。未缓存的随机读取受益于零寻址延迟（快 1.6×）。小文件创建也更快（1.7×），因为元数据操作全在内存中完成。但缓存读取方面 NVMe 更优——NTFS 从 OS 页缓存读取时全程在内核态完成，而内存盘需要经过 WinFsp 的内核–用户态往返，增加了额外开销。运行 `dotnet run --project benchmarks/ManagedDrive.Benchmarks -c Release` 可在你自己的硬件上获取当前数据（见下方[运行基准测试](#running-benchmarks-zh)）。

### 运行测试

```powershell
dotnet test tests/ManagedDrive.Tests
```

测试覆盖 `FileNode`、`FileNodeMap`（增删改查、查找、分页、重命名、容量追踪）、`MemoryFileSystem` 的磁盘克隆逻辑、目录枚举及通配符匹配、`DiskImageSerializer`（各压缩级别的保存/加载往返、旧版本镜像、并发修改）、压缩包导入/导出、`MountOptionsFactory`、`CreateDiskOptionsBuilder`/`ByteUnitConverter`（下沉到 Core 以便脱离 WPF 单测），以及 `PasswordStrengthEstimator`。挂载/卸载集成测试需要 WinFsp 驱动，须手动运行。

<a id="running-benchmarks-zh"></a>
### 运行基准测试

须已安装 WinFsp。基准测试项目会自动选择 `D:` 到 `Z:` 之间第一个空闲盘符，无需手动配置。

```powershell
dotnet run --project benchmarks/ManagedDrive.Benchmarks -c Release
```

BenchmarkDotNet 会提示你选择要运行的基准测试类（`SequentialReadWriteBenchmarks`、`RandomAccessBenchmarks`、`ConcurrentAccessBenchmarks`，或任意组合）。结果将写入工作目录下的 `BenchmarkDotNet.Artifacts/results/`。

### 许可证

MIT

本项目内置了 [WinFsp](https://winfsp.dev/) 和 [SharpCompress](https://github.com/adamhathcock/sharpcompress)，其版权与许可证信息见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
