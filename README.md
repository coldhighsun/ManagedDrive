# ManagedDrive

[![CI / Release](https://github.com/coldhighsun/ManagedDrive/actions/workflows/ci.yml/badge.svg)](https://github.com/coldhighsun/ManagedDrive/actions/workflows/ci.yml)
[![Latest Release](https://img.shields.io/github/v/release/coldhighsun/ManagedDrive)](https://github.com/coldhighsun/ManagedDrive/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

[English](#english) | [中文](#中文)

---

## English

A Windows RAM disk manager built on .NET 10 and [WinFsp](https://winfsp.dev).  
Create, mount and manage in-memory volumes that appear as normal drive letters in Explorer.

### Features

- Mount multiple RAM disks simultaneously, each with its own drive letter
- Configurable capacity, volume label and read-only flag
- Dynamic memory allocation — disk capacity is a ceiling, not a reservation; memory is consumed only as files are written and released when files are deleted
- Edit a mounted disk — change label, capacity, auto-mount, and image path live without data loss; changing the drive letter or read-only flag remounts the disk
- Optional persistence — save the disk contents to a `.mdr` image file and restore it on next mount; Save Image is always available and prompts for a file path if none is set
- Auto-mount saved profiles on application startup
- Double-click a disk card to open it in Explorer
- System-tray icon for quick access; minimizes to tray on window close
- Tray icon tooltip — hover to see all mounted disks with live usage percentages
- High-usage warning — system tray notification when a disk exceeds 90% capacity
- Optional start-minimized mode — launch directly to tray without showing the main window
- Temp directory redirection — right-click a disk to set it as the Windows TEMP/TMP directory; reset to the system default from the toolbar, context menu, or tray menu; automatically resets to the system default when the disk is unmounted or remounted; on startup, if TEMP/TMP points to any RAM disk profile a warning is shown — if the disk is not set to auto-mount, TEMP is also automatically reset to the system default; on user-initiated exit (menu button or tray menu), if TEMP/TMP points to any mounted RAM disk the app interrupts the exit, brings the main window to the foreground, and asks for confirmation before resetting and exiting — system-initiated shutdown bypasses this prompt
- NTFS-compatible volume identity — the RAM disk reports its filesystem type as NTFS, making it fully usable as a destination for tools that require an NTFS volume (e.g. WinGet, Windows Update staging, BITS downloads)
- Format disk — right-click a disk and choose **Format** to delete all files and folders instantly (read-only disks are protected); the context menu is organized into three groups: navigation, configuration, and destructive operations
- System tray menu — quick access to **Reset Temp Directory** (executes silently with a notification bubble result) and **Settings** in addition to Show, New Disk, and Exit
- Main window opens centered on the primary screen and is brought to the foreground on startup
- Bilingual UI — English and Simplified Chinese, auto-detected from system locale with manual override in Settings
- About dialog — accessible from the overflow menu; shows the app version and a link to the GitHub repository

### Installation

Download the latest MSI from the [Releases](https://github.com/coldhighsun/ManagedDrive/releases) page and run it. The installer automatically installs WinFsp if it is not already present, and selects the UI language based on your Windows locale (English or Simplified Chinese).

> **Before repairing or uninstalling:** if you have redirected your Windows user temporary directory (TEMP/TMP) to a ManagedDrive RAM disk, reset it to the Windows default first — either from inside the app using **Reset to Default Temp Directory**, or manually via System Properties → Advanced → Environment Variables. The installer will remind you of this during repair and uninstall.

### Prerequisites

| Requirement | Notes |
|---|---|
| **Windows 10 / 11 (64-bit)** | ARM64 is not currently tested |
| **[WinFsp 2.1.25156](https://winfsp.dev/rel/)** | Exactly this version must be installed before running ManagedDrive. Install via winget: `winget install WinFsp.WinFsp -v 2.1.25156`. The managed assembly `winfsp-msil.dll` is installed to `C:\Program Files (x86)\WinFsp\bin\` and is referenced by the project automatically. |
| **.NET 10 SDK** | Required to build. |

### Getting Started

```powershell
# 1. Install WinFsp 2.1.25156
winget install WinFsp.WinFsp -v 2.1.25156

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
│   ├── ManagedDrive.Core/          # In-memory file system engine (WinFsp)
│   │   ├── DiskOptions.cs          #   Immutable mount configuration record
│   │   ├── FileNode.cs             #   Single file or directory node
│   │   ├── FileNodeMap.cs          #   Thread-safe path→node dictionary
│   │   ├── MemoryFileSystem.cs     #   FileSystemBase implementation (all WinFsp callbacks)
│   │   ├── RamDisk.cs              #   Mount/unmount lifecycle wrapper
│   │   ├── MountManager.cs         #   Multi-disk manager
│   │   └── DiskImageSerializer.cs  #   Binary persistence (.mdr format)
│   │
│   └── ManagedDrive.App/           # WPF desktop application
│       ├── Localization/           #   ResourceDictionary strings (en-US, zh-CN)
│       ├── Infrastructure/         #   RelayCommand
│       ├── Models/                 #   AppConfiguration, DiskProfile
│       ├── Services/               #   SettingsStore, StartupManager, TempDirResetService
│       ├── ViewModels/             #   MainViewModel, DiskViewModel
│       ├── Views/                  #   CreateDiskDialog, SettingsDialog, ConfirmDialog, AboutDialog, TrayTooltipView
│       ├── MainWindow.xaml(.cs)    #   Main window
│       └── App.xaml(.cs)           #   Startup, tray icon, auto-mount
│
├── tests/
│   └── ManagedDrive.Tests/         # xUnit v3 unit tests (pure-managed code only)
│       ├── FileNodeTests.cs
│       └── FileNodeMapTests.cs
│
└── benchmarks/
    └── ManagedDrive.Benchmarks/    # BenchmarkDotNet sequential read/write throughput benchmarks
        ├── Program.cs
        └── ReadWriteBenchmarks.cs
```

### How It Works

ManagedDrive uses **WinFsp** (Windows File System Proxy) to present an in-memory directory tree as a real Windows volume. WinFsp ships a signed kernel driver that acts as a bridge; all file I/O is forwarded to `MemoryFileSystem`, which stores data in .NET byte arrays.

Key classes:

- **`FileNode`** — holds `Fsp.Interop.FileInfo` metadata, a `byte[]` data buffer, and a security descriptor.
- **`FileNodeMap`** — a case-insensitive `SortedDictionary<string, FileNode>` that maps full paths to nodes, supports paginated child enumeration, and tracks total allocated bytes. Thread-safe via the C# 13 `Lock` type.
- **`MemoryFileSystem : FileSystemBase`** — overrides all 21 required WinFsp callbacks (`Create`, `Open`, `Read`, `Write`, `Rename`, `CanDelete`, `ReadDirectoryEntry`, etc.) and enforces a configurable capacity ceiling, returning `STATUS_DISK_FULL` when exceeded; memory is not pre-allocated — each `FileNode` holds only the bytes actually written.
- **`RamDisk`** — composes `MemoryFileSystem` with a `FileSystemHost`. The static `Create()` factory mounts the volume and polls until the drive letter is visible in the OS (up to 2.5 s), then broadcasts `SHCNE_DRIVEADD` to refresh Explorer. `Dispose()` unmounts.
- **`MountManager`** — thread-safe registry of active `RamDisk` instances. Fires `DiskMounted` / `DiskUnmounted` events.
- **`DiskImageSerializer`** — reads/writes `.mdr` files (full FS state including metadata, ACLs, and file data).

### Disk Image Format (`.mdr`)

A little-endian binary format:

| Field | Type | Description |
|---|---|---|
| Magic | `byte[4]` | `MDRD` |
| Version | `int32` | Currently `1` |
| Capacity | `uint64` | Configured capacity in bytes |
| VolumeLabel | `string` | Length-prefixed UTF-8 |
| NodeCount | `int32` | Number of nodes that follow |
| *Node entries* | — | Path, metadata (10 fields), security descriptor, file data |

### Settings & Persistence

- Settings are stored as JSON at `%APPDATA%\ManagedDrive\settings.json`.
- Windows startup registration uses `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run` (no elevation required).
- Logs are written to `{AppPath}\logs\log-.txt` with daily rolling and 7-day retention (Serilog).
- Version is derived from git tags (`v`-prefixed, e.g. `v0.1.0`) via MinVer.

### Performance

Sequential read/write throughput measured with [BenchmarkDotNet](https://benchmarkdotnet.org/) on:

| | |
|---|---|
| **CPU** | Intel Core Ultra 7 255H, 16C/16T, 2.0 GHz base |
| **RAM** | 32 GB |
| **Disk** | Samsung MZVLC512HFJD NVMe SSD (512 GB) |
| **OS** | Windows 11 25H2 (Build 26200.7462) |
| **Runtime** | .NET 10.0.9 · BenchmarkDotNet 0.15.8 |

Each benchmark creates or reads a single file via `FileStream`. Writes use `FileOptions.WriteThrough` (no OS write-back cache) and reads use `FileOptions.SequentialScan`.

| File Size | Operation | RAM Disk | NVMe SSD | Ratio |
|---:|---|---:|---:|---:|
| 4 KB | Write | 1.6 MB/s | 0.9 MB/s | **1.8× faster** |
| 4 KB | Read | 3.1 MB/s | 6.4 MB/s | 0.5× slower |
| 1 MB | Write | 424 MB/s | 87 MB/s | **4.8× faster** |
| 1 MB | Read | 562 MB/s | 1,290 MB/s | 0.4× slower |
| 64 MB | Write | 3,281 MB/s | 918 MB/s | **3.6× faster** |
| 64 MB | Read | 1,955 MB/s | 3,224 MB/s | 0.6× slower |

> **Why are RAM disk reads slower?**  
> Physical disk reads appear fast because the OS page cache keeps recently-written files in DRAM. The RAM disk reads go through WinFsp's kernel–userspace bridge, which adds IPC overhead compared to the direct page-cache path. In workloads where data is written once and read many times from cold cache (e.g. build outputs, temp files that outlive page-cache pressure), the RAM disk will consistently outperform SSD on reads too.
>
> **4 KB note:** small-file results are dominated by file-open/close syscall overhead, not transfer speed.

Raw latency (DefaultJob mean):

| File Size | RamDisk Write | RamDisk Read | NVMe Write | NVMe Read |
|---:|---:|---:|---:|---:|
| 4 KB | 2,440 μs | 1,259 μs | 4,399 μs | 610 μs |
| 1 MB | 2,361 μs | 1,779 μs | 11,446 μs | 775 μs |
| 64 MB | 19,506 μs | 32,746 μs | 69,695 μs | 19,852 μs |

### Running Tests

```powershell
dotnet test tests/ManagedDrive.Tests
```

Tests cover `FileNode` (allocation unit alignment, index numbers) and `FileNodeMap` (CRUD, case-insensitive lookup, child pagination, rename, capacity tracking). Mount/unmount integration tests require the WinFsp driver and must be run manually.

### Running Benchmarks

Drive letter `R:` must be free. WinFsp must be installed.

```powershell
dotnet run --project benchmarks/ManagedDrive.Benchmarks -c Release
```

Results are written to `BenchmarkDotNet.Artifacts/results/` in the working directory.

### Known Issues

#### Certain installers may fail when TEMP is set to a RAM disk

WinFsp mounts drives in the **current user's session device namespace**. If an installer is extracted to TEMP and then launched by a system-level process — such as the Windows Package Manager service used by winget — that process operates in the global device namespace and cannot resolve user-session drive letters. Attempting to execute such an installer from a path like `Z:\Temp\WinGet\...\setup.exe` fails with:

> `0x800704b3` — The network path was not found / 网络路径键入不正确

Known affected packages include **WeChatWin_\*.exe** (WeChat installer). Not all winget packages are affected — many install without issue.

This is an architectural limitation of WinFsp user-mode file systems and cannot be worked around in user space.

**Recommendation:** If you encounter installation errors, restore TEMP to the Windows default using the toolbar button in ManagedDrive. ManagedDrive will warn you when you attempt to set a RAM disk as TEMP. On every subsequent startup, if TEMP still points to a RAM disk, a warning is shown again — reset TEMP to the Windows default to stop the recurring prompt.

### License

MIT

---

## 中文

基于 .NET 10 和 [WinFsp](https://winfsp.dev) 构建的 Windows RAM 虚拟磁盘管理器。  
创建、挂载并管理内存盘，它们在文件资源管理器中以普通驱动器号的形式呈现。

### 功能特性

- 同时挂载多个 RAM 磁盘，每个磁盘拥有独立的驱动器号
- 可配置容量、卷标和只读标志
- 动态内存分配——磁盘容量为上限而非预分配；内存随文件写入而占用，随文件删除而释放
- 编辑已挂载磁盘——修改卷标、容量、自动挂载和镜像路径无需重挂即可实时生效；更改盘符或只读标志时自动重挂
- 可选持久化——将磁盘内容保存为 `.mdr` 镜像文件，下次挂载时自动还原；保存镜像功能始终可用，未设置镜像路径时自动弹出选择对话框
- 应用启动时自动挂载已保存的磁盘配置
- 双击磁盘卡片可在资源管理器中打开对应盘符
- 系统托盘图标，关闭窗口时最小化到托盘
- 托盘图标悬浮提示——鼠标悬停时显示所有已挂载磁盘及其实时使用率
- 高用量警告——磁盘使用率超过 90% 时通过系统托盘发出通知
- 可选最小化启动——直接启动到托盘，不显示主窗口
- 临时目录重定向——右键单击磁盘可将其设为 Windows TEMP/TMP 目录；通过工具栏、右键菜单或托盘菜单恢复系统默认值；卸载或重挂时自动恢复为系统默认临时目录；启动时若 TEMP/TMP 指向任一内存盘配置，均会显示警告——若该磁盘未设置自动挂载，还会自动将 TEMP 恢复为系统默认值；用户主动退出（菜单按钮或托盘菜单）时，若 TEMP/TMP 指向任一已挂载内存盘，程序将中断退出、将主窗口带到前台，并由用户确认重置后再退出——系统发送关闭信号时不触发此流程
- NTFS 兼容卷标识——内存盘以 NTFS 文件系统类型上报，可作为需要 NTFS 卷的工具（如 WinGet、Windows Update 暂存、BITS 下载）的目标路径
- 磁盘格式化——右键单击磁盘并选择**格式化**可立即删除所有文件和文件夹（只读磁盘受保护）；右键菜单按导航、配置、破坏性操作三个分组排列
- 系统托盘菜单——在显示、新建磁盘、退出之外，新增**重置临时文件夹**（静默执行，结果通过气泡通知反馈）和**设置**快捷入口
- 主窗口启动时居中显示于主屏幕并置于前台
- 双语界面——中文与英文，根据系统语言自动切换，也可在设置中手动更改
- 关于对话框——可从溢出菜单打开，显示应用版本及 GitHub 仓库链接

### 安装

从 [Releases](https://github.com/coldhighsun/ManagedDrive/releases) 页面下载最新的 MSI 安装包并运行。安装程序会在 WinFsp 未安装时自动安装，并根据 Windows 系统语言自动选择界面语言（中文或英文）。

> **修复或卸载前请注意：** 如果您已将 Windows 用户临时目录（TEMP/TMP）重定向到 ManagedDrive 内存盘，请先将其恢复为系统默认值——在应用内使用**恢复默认用户临时文件夹**功能，或手动在「系统属性」→「高级」→「环境变量」中修改。安装程序在执行修复和卸载时会提示您完成此操作。

### 环境要求

| 要求 | 说明 |
|---|---|
| **Windows 10 / 11（64 位）** | 暂未测试 ARM64 |
| **[WinFsp 2.1.25156](https://winfsp.dev/rel/)** | 必须安装此特定版本。可使用 winget 安装：`winget install WinFsp.WinFsp -v 2.1.25156`。托管程序集 `winfsp-msil.dll` 将安装至 `C:\Program Files (x86)\WinFsp\bin\`，项目会自动引用。 |
| **.NET 10 SDK** | 编译所需。 |

### 快速开始

```powershell
# 1. 安装 WinFsp 2.1.25156
winget install WinFsp.WinFsp -v 2.1.25156

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
│   ├── ManagedDrive.Core/          # 内存文件系统引擎（WinFsp）
│   │   ├── DiskOptions.cs          #   不可变挂载配置记录
│   │   ├── FileNode.cs             #   单个文件或目录节点
│   │   ├── FileNodeMap.cs          #   线程安全的路径→节点字典
│   │   ├── MemoryFileSystem.cs     #   FileSystemBase 实现（所有 WinFsp 回调）
│   │   ├── RamDisk.cs              #   挂载/卸载生命周期封装
│   │   ├── MountManager.cs         #   多磁盘管理器
│   │   └── DiskImageSerializer.cs  #   二进制持久化（.mdr 格式）
│   │
│   └── ManagedDrive.App/           # WPF 桌面应用程序
│       ├── Localization/           #   ResourceDictionary 字符串（en-US、zh-CN）
│       ├── Infrastructure/         #   RelayCommand
│       ├── Models/                 #   AppConfiguration、DiskProfile
│       ├── Services/               #   SettingsStore、StartupManager、TempDirResetService
│       ├── ViewModels/             #   MainViewModel、DiskViewModel
│       ├── Views/                  #   CreateDiskDialog、SettingsDialog、ConfirmDialog、AboutDialog、TrayTooltipView
│       ├── MainWindow.xaml(.cs)    #   主窗口
│       └── App.xaml(.cs)           #   启动、托盘图标、自动挂载
│
├── tests/
│   └── ManagedDrive.Tests/         # xUnit v3 单元测试（仅纯托管代码）
│       ├── FileNodeTests.cs
│       └── FileNodeMapTests.cs
│
└── benchmarks/
    └── ManagedDrive.Benchmarks/    # BenchmarkDotNet 顺序读写吞吐量基准测试
        ├── Program.cs
        └── ReadWriteBenchmarks.cs
```

### 工作原理

ManagedDrive 使用 **WinFsp**（Windows 文件系统代理）将内存目录树呈现为真实的 Windows 卷。WinFsp 附带一个已签名的内核驱动程序作为桥接层；所有文件 I/O 均转发至 `MemoryFileSystem`，后者将数据存储在 .NET 字节数组中。

核心类：

- **`FileNode`** — 持有 `Fsp.Interop.FileInfo` 元数据、`byte[]` 数据缓冲区及安全描述符。
- **`FileNodeMap`** — 不区分大小写的 `SortedDictionary<string, FileNode>`，将完整路径映射到节点，支持分页子节点枚举，并追踪已分配字节总量。通过 C# 13 `Lock` 类型保证线程安全。
- **`MemoryFileSystem : FileSystemBase`** — 覆写全部 21 个所需的 WinFsp 回调（`Create`、`Open`、`Read`、`Write`、`Rename`、`CanDelete`、`ReadDirectoryEntry` 等），并强制执行可配置的容量上限，超出时返回 `STATUS_DISK_FULL`；内存不预分配——每个 `FileNode` 仅保留实际写入的字节数。
- **`RamDisk`** — 组合 `MemoryFileSystem` 与 `FileSystemHost`。静态工厂方法 `Create()` 挂载卷，并轮询直至驱动器号在系统中可见（最长 2.5 秒），随后向资源管理器广播 `SHCNE_DRIVEADD`。`Dispose()` 执行卸载。
- **`MountManager`** — 线程安全的活动 `RamDisk` 实例注册表，提供 `DiskMounted` / `DiskUnmounted` 事件。
- **`DiskImageSerializer`** — 读写 `.mdr` 文件（保存完整文件系统状态，包含元数据、ACL 和文件数据）。

### 磁盘镜像格式（`.mdr`）

小端序二进制格式：

| 字段 | 类型 | 说明 |
|---|---|---|
| 魔数 | `byte[4]` | `MDRD` |
| 版本 | `int32` | 当前为 `1` |
| 容量 | `uint64` | 配置的容量（字节） |
| 卷标 | `string` | 长度前缀 UTF-8 |
| 节点数 | `int32` | 后续节点数量 |
| *节点条目* | — | 路径、元数据（10 个字段）、安全描述符、文件数据 |

### 配置与持久化

- 配置以 JSON 格式存储于 `%APPDATA%\ManagedDrive\settings.json`。
- 开机自启通过 `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run` 注册表项实现（无需提升权限）。
- 日志写入 `{AppPath}\logs\log-.txt`，每日滚动，保留 7 天（Serilog）。
- 版本号由 MinVer 从 git 标签派生（`v` 前缀，例如 `v0.1.0`）。

### 性能基准

使用 [BenchmarkDotNet](https://benchmarkdotnet.org/) 测量顺序读写吞吐量，测试环境：

| | |
|---|---|
| **CPU** | Intel Core Ultra 7 255H，16C/16T，基础频率 2.0 GHz |
| **内存** | 32 GB |
| **磁盘** | Samsung MZVLC512HFJD NVMe SSD（512 GB） |
| **系统** | Windows 11 25H2（Build 26200.7462） |
| **运行时** | .NET 10.0.9 · BenchmarkDotNet 0.15.8 |

每个 benchmark 通过 `FileStream` 对单个文件进行创建或读取操作。写入使用 `FileOptions.WriteThrough`（禁用操作系统写回缓存），读取使用 `FileOptions.SequentialScan`。

| 文件大小 | 操作 | 内存盘 | NVMe SSD | 倍率 |
|---:|---|---:|---:|---:|
| 4 KB | 写入 | 1.6 MB/s | 0.9 MB/s | **快 1.8×** |
| 4 KB | 读取 | 3.1 MB/s | 6.4 MB/s | 慢 0.5× |
| 1 MB | 写入 | 424 MB/s | 87 MB/s | **快 4.8×** |
| 1 MB | 读取 | 562 MB/s | 1,290 MB/s | 慢 0.4× |
| 64 MB | 写入 | 3,281 MB/s | 918 MB/s | **快 3.6×** |
| 64 MB | 读取 | 1,955 MB/s | 3,224 MB/s | 慢 0.6× |

> **为何内存盘读取反而更慢？**  
> 物理磁盘的读取速度之所以快，是因为操作系统页面缓存将最近写入的文件保留在 DRAM 中。内存盘的读取则需要经过 WinFsp 的内核–用户态桥接，引入了额外的 IPC 开销，比直接走页面缓存路径更慢。在数据写入一次、多次读取且页面缓存已失效的场景下（如构建产物、临时文件），内存盘的读取性能同样会超越 SSD。
>
> **4 KB 说明：** 小文件结果主要受文件打开/关闭的系统调用开销主导，不反映实际传输速率。

原始延迟（DefaultJob 均值）：

| 文件大小 | 内存盘写入 | 内存盘读取 | NVMe 写入 | NVMe 读取 |
|---:|---:|---:|---:|---:|
| 4 KB | 2,440 μs | 1,259 μs | 4,399 μs | 610 μs |
| 1 MB | 2,361 μs | 1,779 μs | 11,446 μs | 775 μs |
| 64 MB | 19,506 μs | 32,746 μs | 69,695 μs | 19,852 μs |

### 运行测试

```powershell
dotnet test tests/ManagedDrive.Tests
```

测试覆盖 `FileNode`（分配单元对齐、索引编号）和 `FileNodeMap`（增删改查、大小写无关查找、子节点分页、重命名、容量追踪）。挂载/卸载集成测试需要 WinFsp 驱动程序，须手动运行。

### 运行基准测试

驱动器号 `R:` 须处于空闲状态，且已安装 WinFsp。

```powershell
dotnet run --project benchmarks/ManagedDrive.Benchmarks -c Release
```

结果将写入工作目录下的 `BenchmarkDotNet.Artifacts/results/`。

### 已知问题

#### 将 TEMP 设为内存盘后，某些安装包可能报错

WinFsp 将驱动器挂载在**当前用户的会话设备命名空间**中。若安装包被解压到 TEMP 后由系统级进程启动（例如 winget 所使用的 Windows 软件包管理器服务），该进程运行于全局设备命名空间，无法解析用户会话级别的驱动器号。尝试从 `Z:\Temp\WinGet\...\setup.exe` 之类的路径执行安装程序时，会报错：

> `0x800704b3` — 网络路径键入不正确 / The network path was not found

已知受影响的安装包包括 **WeChatWin\_\*.exe**（微信安装程序）。并非所有 winget 包都受影响——大多数包可正常安装。

这是 WinFsp 用户态文件系统的架构性限制，无法在用户空间层面绕过。

**建议：** 如遇安装报错，可通过 ManagedDrive 工具栏的按钮将 TEMP 恢复为 Windows 默认值。每次将内存盘设置为临时目录时，ManagedDrive 均会弹出警告提示。此后每次启动，只要 TEMP 仍指向内存盘，警告便会再次弹出——将 TEMP 恢复为 Windows 默认值后即可停止重复提示。

### 许可证

MIT
