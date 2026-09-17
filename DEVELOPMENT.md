# Development

Developer-facing documentation for ManagedDrive — building from source, solution structure, internals, and benchmarks. See [README.md](README.md) for user-facing installation and usage docs.

[English](#english) | [中文](#中文)

---

## English

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
│   ├── ManagedDrive.Service/           # `ManagedDriveHelper.exe`, optional LocalSystem service publishing global DOS-device symlinks for cross-session TEMP visibility (see Known Issues in README.md)
│   └── ManagedDrive.WingetExtension/   # `wingetx.exe`, a transparent winget wrapper (see wingetx: winget wrapper in README.md)
├── tests/
│   └── ManagedDrive.Tests/             # xUnit v3 unit tests (pure-managed code only)
└── benchmarks/
    └── ManagedDrive.Benchmarks/        # BenchmarkDotNet throughput/latency benchmarks
```

### How It Works

ManagedDrive uses **WinFsp** (Windows File System Proxy) to present an in-memory directory tree as a real Windows volume: a signed kernel driver forwards file I/O to a managed file system implementation that stores data in .NET byte arrays and enforces a configurable capacity ceiling. Mounting/unmounting, save/restore to a `.mdr` image, and snapshot history are all handled by `ManagedDrive.Core` (see `CLAUDE.md` for the class-level architecture).

### Disk Image & Snapshot Format

`.mdr` images are a versioned, little-endian binary format (magic `MDRD`) with optional Zstd compression and optional AES-256-GCM password-based encryption; large disks stream to/from the file and encrypt in chunks rather than buffering the whole image in memory. Saves split the image into independent segments and reuse the on-disk bytes of any segment whose files haven't changed since the last save, so periodic auto-save on a large, mostly-unchanged disk only recompresses/re-encrypts the small part that actually changed. Snapshots use a separate format (magic `MDRS`) stored next to the main image, with file content deduplicated by SHA-256 into a shared blob store so snapshots of a mostly-unchanged disk cost little extra space. Both formats stay backward-compatible with older versions produced by earlier releases. See `CLAUDE.md` for the exact binary layout.

### Performance

Measured with [BenchmarkDotNet](https://benchmarkdotnet.org/) (Intel Core i9-13980HX, 64 GB RAM, KIOXIA KXG8AZNV1T02 NVMe SSD, Windows 11 Pro, .NET 10.0.12):

| Scenario | RAM Disk | NVMe SSD | Ratio |
|---|---:|---:|---:|
| Sequential write, 4 KB | 1.7 MB/s | 1.0 MB/s | **RAM 1.8× faster** |
| Sequential write, 1 MB | 385.2 MB/s | 91.8 MB/s | **RAM 4.2× faster** |
| Sequential read (OS cache), 4 KB | 4.0 MB/s | 6.2 MB/s | NVMe 1.5× faster |
| Sequential read (OS cache), 1 MB | 632.2 MB/s | 1,331.4 MB/s | NVMe 2.1× faster |
| Random 4 KB read (uncached), 30 seeks | 1.82 ms | 3.34 ms | **RAM 1.8× faster** |
| Random 4 KB read (OS cache), 30 seeks | 1.93 ms | 0.91 ms | NVMe 2.1× faster |
| 30× small-file (4 KB) create+write | 68.5 ms (2.28 ms/file) | 111.4 ms (3.71 ms/file) | **RAM 1.6× faster** |

Writes win big (up to 4.2×) by skipping block allocation, journaling, and the physical write. Uncached random reads benefit from zero seek latency (1.8× faster). Small-file creates are also faster (1.6×) because metadata operations stay in memory. Cached reads, however, favor the NVMe path — NTFS reads from the OS page cache stay entirely in-kernel, while the RAM disk incurs an extra kernel–userspace round trip through WinFsp. Run `dotnet run --project benchmarks/ManagedDrive.Benchmarks -c Release` for current numbers on your own hardware (see [Running Benchmarks](#running-benchmarks) below).

### Running Tests

```powershell
dotnet test tests/ManagedDrive.Tests
```

Tests cover `FileNode`, `FileNodeMap` (CRUD, lookup, pagination, rename, capacity tracking), `MemoryFileSystem` disk-cloning, directory enumeration and the wildcard matcher, `DiskImageSerializer` (round-trips across compression levels, legacy images, concurrent mutation during save, the segmented incremental format's segment-reuse/rewrite decisions across successive saves), archive import/export, `MountOptionsFactory`, `CreateDiskOptionsBuilder`/`ByteUnitConverter` (create-disk dialog validation, kept WPF-free for testability), and `PasswordStrengthEstimator`. Mount/unmount integration tests need the WinFsp driver and must be run manually.

### Running Benchmarks

WinFsp must be installed. The benchmark project auto-selects the first free drive letter between `D:` and `Z:` — no manual configuration needed.

```powershell
dotnet run --project benchmarks/ManagedDrive.Benchmarks -c Release
```

BenchmarkDotNet will prompt you to pick which benchmark class(es) to run (`SequentialReadWriteBenchmarks`, `RandomAccessBenchmarks`, `ConcurrentAccessBenchmarks`, or any combination). Results are written to `BenchmarkDotNet.Artifacts/results/` in the working directory.

---

## 中文

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
│   ├── ManagedDrive.Service/           # `ManagedDriveHelper.exe`——可选的 LocalSystem 服务，发布全局 DOS 设备符号链接以实现跨会话 TEMP 可见性（见 README.md 的"已知问题"）
│   └── ManagedDrive.WingetExtension/   # `wingetx.exe`——透明的 winget 包装工具（见 README.md 的"wingetx: winget 包装工具"）
├── tests/
│   └── ManagedDrive.Tests/             # xUnit v3 单元测试（仅纯托管代码）
└── benchmarks/
    └── ManagedDrive.Benchmarks/        # BenchmarkDotNet 吞吐量/延迟基准测试
```

### 工作原理

ManagedDrive 使用 **WinFsp**（Windows 文件系统代理）将内存目录树呈现为真实的 Windows 卷：已签名内核驱动把文件 I/O 转发至托管文件系统实现，数据存储在 .NET 字节数组中，并强制容量上限。挂载/卸载、保存/还原为 `.mdr` 镜像、快照历史等均由 `ManagedDrive.Core` 负责（类级别架构见 `CLAUDE.md`）。

### 磁盘镜像与快照格式

`.mdr` 镜像是带版本号的小端序二进制格式（魔数 `MDRD`），可选 Zstd 压缩和基于密码的 AES-256-GCM 加密；大磁盘会流式读写文件并分块加密，而非把整个镜像缓冲到内存中。保存时镜像会被拆分为多个独立分段，自上次保存以来未变化的文件所在分段会直接复用磁盘上的原始字节，因此大容量、内容基本未变的磁盘做周期性自动保存时只需重新压缩/加密真正改动的那一小部分。快照采用独立格式（魔数 `MDRS`），存放在主镜像旁，文件内容按 SHA-256 去重存储到共享的块存储中，因此对基本未变化的磁盘做快照额外占用很小。两种格式都会保持对旧版本发布产物的向后兼容。具体二进制布局见 `CLAUDE.md`。

### 性能基准

使用 [BenchmarkDotNet](https://benchmarkdotnet.org/) 测量（Intel Core i9-13980HX、64 GB 内存、KIOXIA KXG8AZNV1T02 NVMe SSD、Windows 11 Pro、.NET 10.0.12）：

| 场景 | 内存盘 | NVMe SSD | 倍率 |
|---|---:|---:|---:|
| 顺序写入，4 KB | 1.7 MB/s | 1.0 MB/s | **内存盘快 1.8×** |
| 顺序写入，1 MB | 385.2 MB/s | 91.8 MB/s | **内存盘快 4.2×** |
| 顺序读取（OS 缓存），4 KB | 4.0 MB/s | 6.2 MB/s | NVMe 快 1.5× |
| 顺序读取（OS 缓存），1 MB | 632.2 MB/s | 1,331.4 MB/s | NVMe 快 2.1× |
| 随机 4 KB 读取（未缓存），30 次寻址 | 1.82 ms | 3.34 ms | **内存盘快 1.8×** |
| 随机 4 KB 读取（OS 缓存），30 次寻址 | 1.93 ms | 0.91 ms | NVMe 快 2.1× |
| 30 次小文件（4 KB）创建+写入 | 68.5 ms（2.28 ms/文件） | 111.4 ms（3.71 ms/文件） | **内存盘快 1.6×** |

写入优势显著（最高 4.2×），因为跳过了物理块分配、日志记录和实际落盘。未缓存的随机读取受益于零寻址延迟（快 1.8×）。小文件创建也更快（1.6×），因为元数据操作全在内存中完成。但缓存读取方面 NVMe 更优——NTFS 从 OS 页缓存读取时全程在内核态完成，而内存盘需要经过 WinFsp 的内核–用户态往返，增加了额外开销。运行 `dotnet run --project benchmarks/ManagedDrive.Benchmarks -c Release` 可在你自己的硬件上获取当前数据（见下方[运行基准测试](#running-benchmarks-zh)）。

### 运行测试

```powershell
dotnet test tests/ManagedDrive.Tests
```

测试覆盖 `FileNode`、`FileNodeMap`（增删改查、查找、分页、重命名、容量追踪）、`MemoryFileSystem` 的磁盘克隆逻辑、目录枚举及通配符匹配、`DiskImageSerializer`（各压缩级别的保存/加载往返、旧版本镜像、并发修改、分段增量格式在连续多次保存中的分段复用/重写决策）、压缩包导入/导出、`MountOptionsFactory`、`CreateDiskOptionsBuilder`/`ByteUnitConverter`（下沉到 Core 以便脱离 WPF 单测），以及 `PasswordStrengthEstimator`。挂载/卸载集成测试需要 WinFsp 驱动，须手动运行。

<a id="running-benchmarks-zh"></a>
### 运行基准测试

须已安装 WinFsp。基准测试项目会自动选择 `D:` 到 `Z:` 之间第一个空闲盘符，无需手动配置。

```powershell
dotnet run --project benchmarks/ManagedDrive.Benchmarks -c Release
```

BenchmarkDotNet 会提示你选择要运行的基准测试类（`SequentialReadWriteBenchmarks`、`RandomAccessBenchmarks`、`ConcurrentAccessBenchmarks`，或任意组合）。结果将写入工作目录下的 `BenchmarkDotNet.Artifacts/results/`。
