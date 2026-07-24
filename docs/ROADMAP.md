# ManagedDrive 功能增强与优化清单 (Roadmap)

> 状态:方向梳理草案(2026-07-21)。本文件为增强方向备份,非承诺实现范围。
> 确定优先项后,应针对所选功能再单独出一份详细实施计划(具体文件改动、数据结构、测试用例)。

## 背景

ManagedDrive 已是一个功能相当完整的 WinFsp 内存盘管理工具:创建/编辑/卸载/格式化、`.mdr`
镜像持久化(压缩 + AES-256-GCM 加密)、周期/退出自动保存、内容寻址去重快照、归档导入、克隆/导出、
CLI + 命名管道、TEMP 重定向、多语言 + 明暗主题。代码质量高(src 内零 TODO/FIXME)。

经代码核对,确认三个明确的能力空白:(1) 没有任何盘内文件浏览器/内容视图;(2) 快照只能整体恢复,
无法浏览内容或对比差异;(3) 大盘保存 / 归档导入 / 快照写入等长耗时操作**完全没有进度反馈**(全仓无 `IProgress` 使用)。

---

## 优先级 P0 — 体验短板,投入产出比最高

### 1. 长耗时操作的进度反馈
**问题:** `SaveImageCommand`(序列化 + gzip)、归档导入、快照写入、克隆/导出对大盘可能耗时数秒到数十秒,
目前 UI 只有一个已有的"正在保存"全屏遮罩(退出时),常规保存/导入过程无任何进度提示。全仓零 `IProgress`。
**方案:**
- `DiskImageSerializer.Save/Load`、`ArchiveNodeMapBuilder.BuildNodeMap`、`SnapshotStore.WriteSnapshot`
  增加可选 `IProgress<double>`(按节点数或字节数上报)。
- App 层给 `ExecuteSaveImage`、`ImportArchive`、`ExecuteCloneDisk` 挂进度条(可复用现有
  `SlimProgressBar` 样式 + `IsExiting` 遮罩的模式,做成通用"忙碌 + 进度"覆盖层)。
- 相关文件:`src/ManagedDrive.Core/DiskImageSerializer.cs`、`ArchiveNodeMapBuilder.cs`、
  `SnapshotStore.cs`;`src/ManagedDrive.App/MainViewModel.cs`、`MainWindow.xaml`。

### 2. 只读文件夹快照导入(把现有目录做成内存盘)
**问题:** 目前只支持归档(zip/7z/…)导入。把一个物理目录一次性载入内存盘是很自然的需求
(加速编译中间产物、只读缓存等),现在做不到。
**方案:** 新增 `FolderNodeMapBuilder`(与 `ArchiveNodeMapBuilder` 对称,递归读目录/文件到 `FileNodeMap`),
`DiskOptions` 增加 `SourceFolderPath`,`CreateDiskDialog` 增加"文件夹导入模式"(复用 `_isImportMode` 那套),
`MainViewModel.ImportFolderCommand`。可只读也可作为一次性填充后允许读写。

---

## 优先级 P1 — 明确空白,提升产品完整度

### 3. 盘内文件浏览器 / 空间占用视图
**问题:** 只有百分比进度条,用户无法看到盘里有什么、哪个文件/目录占空间。定位内存占用只能去 Explorer。
**方案:** 新增 `Views/DiskContentDialog`,用 `TreeView` 或大文件排行列表展示 `FileNodeMap`
(数据已在内存,`GetChildren()` 现成)。可先做"Top N 大文件 + 目录大小"轻量版,再考虑完整树。
只读展示,不改动盘内容。

### 4. 快照浏览 / 差异对比
**问题:** 快照只能整体恢复,恢复前无法确认里面有什么、和当前盘差在哪,误恢复代价高。
**方案:** `SnapshotManager` 增加 `LoadSnapshotSummary`/`DiffAgainstCurrent`(基于快照索引里已有的
每文件 SHA-256,可 O(n) 算出新增/删除/修改的文件列表,无需读 blob)。`RestoreSnapshotDialog`
增加"查看内容 / 对比当前盘"面板。与 #3 的浏览器组件可共用 UI。

---

## 优先级 P2 — 健壮性 / 安全 / 收尾

### 5. 加密密钥的内存保护与密码强度提示
- `RamDisk._cek`/`_password` 目前是明文 `byte[]`/`string` 常驻内存。可考虑用完即清零(`CryptographicOperations.ZeroMemory`)、
  或 `SecureString`/DPAPI(`ProtectData`)包裹,降低内存转储泄露风险。
- `CreateDiskDialog` 加密码强度指示(已有 8–64 长度限制,可加弱密码提示)。

### 6. 导出为标准格式(zip / VHD)
现在只能导出为私有 `.mdr`。增加"导出为 zip"(复用 SharpCompress 写入能力)便于跨工具使用;
VHD 较重,列为可选远期项。

### 7. 测试补强
现有 8 个测试类未覆盖:加密路径的 Save/Load 往返(`ImageEncryptionInfo`、错误密码抛
`ImagePasswordIncorrectException`)、快照加密 blob、`TryApplyOptions` 的 live-vs-remount 判定。
建议新增 `DiskImageEncryptionTests`、扩充 `SnapshotManagerTests` 加密分支。

---

## 建议的落地顺序

先做 **P0-#1(进度反馈)**:改动集中、对所有长操作立即受益、风险低,是最好的起点。
其次 **P0-#2(文件夹导入)** 与 **P1-#3(文件浏览器)**,二者都基于现成的 `FileNodeMap` API,组件可复用。
P1-#4 依赖 #3 的 UI 组件,随后跟进。P2 项按需插入。

## 验证方式

- 单元测试:`dotnet test tests/ManagedDrive.Tests`(新增/扩充对应测试类)。
- 端到端(需 WinFsp 驱动,手动):`dotnet run --project src/ManagedDrive.App -c Release`,
  手工验证进度条在大盘保存/大归档导入时正确推进、文件夹导入内容正确、快照对比列表准确。
- 每次提交前 `dotnet build` 确保编译通过。
