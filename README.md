# ManagedDrive

A Windows RAM disk manager built on .NET 10 and [WinFsp](https://winfsp.dev).  
Create, mount and manage in-memory volumes that appear as normal drive letters in Explorer.

## Features

- Mount multiple RAM disks simultaneously, each with its own drive letter
- Configurable capacity, volume label and read-only flag
- Optional persistence — save the disk contents to a `.mdr` image file and restore it on next mount
- Auto-mount saved profiles on application startup
- System-tray icon for quick access without keeping the main window open
- Minimize-to-tray on window close

## Prerequisites

| Requirement | Notes |
|---|---|
| **Windows 10 / 11 (64-bit)** | ARM64 is not currently tested |
| **[WinFsp 2.x](https://winfsp.dev/rel/)** | Must be installed before running ManagedDrive. Download the installer from the WinFsp releases page and run it. The managed assembly `winfsp-msil.dll` is installed to `C:\Program Files (x86)\WinFsp\bin\` and is referenced by the project automatically. |
| **.NET 10 SDK** | Required to build. The runtime is embedded via self-contained publish or must be installed separately. |
| **Administrator privileges** | Mounting a drive letter requires elevation. The application manifest requests `requireAdministrator`. |

## Getting Started

```powershell
# 1. Install WinFsp (download from https://winfsp.dev/rel/)

# 2. Clone / open the solution
git clone https://github.com/coldhighsun/ManagedDrive
cd ManagedDrive

# 3. Build
dotnet build

# 4. Run (must be run as Administrator)
dotnet run --project ManagedDrive.App
```

Alternatively open `ManagedDrive.slnx` in Visual Studio 2026+ and press **F5**
(Visual Studio will prompt to elevate when the manifest is detected).

## Solution Structure

```
ManagedDrive/
├── ManagedDrive.Core/          # In-memory file system engine (WinFsp)
│   ├── FileNode.cs             #   One file or directory node
│   ├── FileNodeMap.cs          #   Thread-safe path→node dictionary
│   ├── MemoryFileSystem.cs     #   FileSystemBase implementation
│   ├── DiskOptions.cs          #   Immutable mount configuration record
│   ├── RamDisk.cs              #   Mount/unmount lifecycle wrapper
│   ├── MountManager.cs         #   Multi-disk manager
│   └── DiskImageSerializer.cs  #   Binary persistence (*.mdr format)
│
├── ManagedDrive.App/           # WPF desktop application
│   ├── Infrastructure/         #   RelayCommand
│   ├── Models/                 #   DiskProfile (JSON settings model)
│   ├── Services/               #   SettingsStore (%APPDATA%\ManagedDrive)
│   ├── ViewModels/             #   MainViewModel, DiskViewModel
│   ├── Views/                  #   CreateDiskDialog
│   ├── MainWindow.xaml(.cs)    #   Main window
│   └── App.xaml(.cs)           #   Startup, tray icon, auto-mount
│
└── ManagedDrive.Tests/         # xUnit unit tests (pure-managed code only)
	├── FileNodeTests.cs
	└── FileNodeMapTests.cs
```

## How It Works

ManagedDrive uses **WinFsp** (Windows File System Proxy) to present an in-memory
directory tree as a real Windows volume. WinFsp ships a signed kernel driver that
acts as a bridge; all file I/O is forwarded to `MemoryFileSystem` which stores data
in .NET byte arrays.

Key classes:

- **`FileNode`** — holds `Fsp.Interop.FileInfo` metadata plus a `byte[]` data buffer.
- **`FileNodeMap`** — a `SortedDictionary<string, FileNode>` (case-insensitive) that maps
  full paths to nodes and supports efficient child enumeration.
- **`MemoryFileSystem : FileSystemBase`** — overrides all required WinFsp callbacks
  (`Create`, `Open`, `Read`, `Write`, `Rename`, `CanDelete`, `ReadDirectoryEntry`, etc.)
  and enforces a configurable capacity ceiling, returning `STATUS_DISK_FULL` when exceeded.
- **`RamDisk`** — wraps a `FileSystemHost` and drives the mount/unmount lifecycle. It
  optionally loads from / saves to a `.mdr` image file.

## Disk Image Format (`.mdr`)

A simple little-endian binary format:

| Field | Type | Description |
|---|---|---|
| Magic | `byte[4]` | `MDRD` |
| Version | `int32` | Currently `1` |
| Capacity | `uint64` | Configured capacity in bytes |
| VolumeLabel | `string` | Length-prefixed UTF-8 |
| NodeCount | `int32` | Number of nodes that follow |
| *Node entries* | — | Path, metadata, security descriptor, file data |

## Running Tests

```powershell
dotnet test ManagedDrive.Tests
```

Tests cover the pure-managed model (`FileNode`, `FileNodeMap`, capacity arithmetic).
Mount/unmount integration tests require the WinFsp driver and must be run manually.

## License

MIT
