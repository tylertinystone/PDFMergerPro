# Virtual Encrypted Disk (C#)

这是一个“基于第三方驱动的虚拟硬件系统”示例：

- 使用 **AES-GCM + PBKDF2** 对虚拟硬盘容器加密。
- 用户必须输入正确密码后，才会调用驱动执行挂载。
- 默认使用 **Dokan.NET（直接 Builder 调用）** 驱动适配器。

## 目录

- `src/VirtualEncryptedDisk/Program.cs`：控制台入口，读取密码并挂载。
- `SecureVirtualDiskManager.cs`：核心流程（创建容器、验密、挂载/卸载）。
- `EncryptionService.cs`：加解密与密钥派生。
- `ContainerFileService.cs`：容器文件格式读写。
- `DokanThirdPartyDiskDriver.cs`：通过 Dokan.NET 挂载盘符。
- `DokanPassthroughOperations.cs`：自定义 `IDokanOperations` 本地透传实现。
- `IThirdPartyDiskDriver.cs`：第三方驱动统一抽象。

## 关于 “Dokan.Mirror 不可用”

本项目已不再依赖 `DokanNet.Mirror` 包。

当前实现改为：

1. 解密得到内存中的虚拟磁盘数据。
2. 将明文归档展开到运行目录（默认：容器同目录下 `.ved-runtime`，也可通过 `RuntimeRootPath` 指定）。
3. 使用项目内的 `DokanPassthroughOperations`（`IDokanOperations` 实现）并通过 `DokanInstanceBuilder` 直接挂载到盘符（不使用反射）。
4. 卸载时释放挂载实例（`IDisposable`），回收运行目录并写回容器。

## 使用前准备（Windows）

1. 安装 Dokan Runtime（驱动，确保 `dokan2.dll` 可被加载）
2. 用“管理员身份”启动终端运行程序
3. 确认目标盘符（如 `R:`）没有被占用

## 快速接入真实第三方驱动

1. 保留 `IThirdPartyDiskDriver` 接口。
2. 可按同样方式新增 `WinFspDiskDriver` / `ImDiskDriver`。
3. 在 `Program.cs` 中替换为你的驱动实现。

## 安全建议

- 生产环境请将密码输入改为安全输入控件，并考虑敏感内存清理。
- 挂载期间会存在“已解密明文文件”（用于给 Dokan 提供后端目录）。建议将容器与 `RuntimeRootPath` 放在受 BitLocker/VeraCrypt 等保护的卷，或放在受控 RAM Disk。
- 可加入 TPM/证书二次认证，避免单一口令风险。
- 建议增加容器完整性版本头、审计日志与失败重试锁定策略。


## 内容持久化说明

- 挂载时：容器解密后的明文会被视为目录归档并展开为挂载后端目录。
- 卸载时：挂载目录会重新打包并加密回容器文件。
- 因此在盘符里新建/修改/删除文件后，卸载并再次挂载仍可看到上次内容。


## 掉电场景说明

- 已加入**周期自动快照**（默认约每 5 秒）与**卸载时最终回写**。
- 容器写入采用临时文件 + 原子替换（`File.Replace`/`File.Move`）方式，降低写坏风险。
- 但掉电瞬间仍可能丢失最近一次快照后的少量数据（取决于快照间隔和底层磁盘缓存）。


## 调试建议（安装器兼容性）

- 盘符卷信息对外标识为 `NTFS`（用于提高资源管理器右键“新建”等壳层兼容性）。
- 可通过 `VirtualDiskConfig.AutosaveEnabled=false` 临时关闭自动快照，排查安装程序执行期间的并发快照干扰。
- 可通过 `AutosaveIntervalSeconds` 调整快照周期。

- 可通过 `PersistOnlyWhenChanged=true`（默认）避免“无改动卸载时”重复加密写回，减少磁盘写放大。


## 分阶段计划（流式加解密）

- 阶段 1（本次）：补齐分块加解密原语（每块独立 GCM Tag），不改现有挂载路径。
- 阶段 2（本次）：新增容器块索引（chunk metadata）与块读写 API（`ChunkedContainerStore`）。
- 阶段 3（本次，过渡版）：管理器可切换/自动识别分块容器存储链路（`UseChunkedContainerExperimental` + VEC2 自动识别），为后续 Dokan 读写直连分块容器做准备。
- 阶段 4（本次，推进版）：默认写回路径切到 VEC2 分块容器；Legacy VED1 保留读取兼容，并支持挂载时自动迁移到 VEC2。

- 阶段3补充：即使 `UseChunkedContainerExperimental=false`，也会自动识别已存在的 `VEC2` 容器并按分块链路读取。

- 阶段4补充：可通过 `AllowLegacyVed1Read` 控制是否允许读取旧 `VED1` 容器（默认允许）。

- 阶段4新增：可通过 `AutoMigrateLegacyOnMount=true` 在首次挂载 Legacy VED1 时自动迁移到 VEC2。

- 构建稳定性：项目已在编译前显式创建 `obj` 与 Roslyn editorconfig 目录，降低 `VirtualEncryptedDisk.GeneratedMSBuildEditorConfig.editorconfig` 路径缺失错误。

- 阶段4下一子步：`ChunkedContainerStore` 已提供 `ReadAt/WriteAt/SetLength` 随机读写 API（含旧 VEC2 头兼容读取）。

- 阶段4继续：`DokanPassthroughOperations` 已引入 `IFileContentStore` 抽象，`ReadFile/WriteFile/SetEndOfFile` 通过后端接口执行，为下一步切换到分块存储后端做准备。

- 阶段4继续2：`GetFileInformation` 已通过 `IFileContentStore` 获取存在性/长度/时间戳/属性，减少对本地 `FileInfo/DirectoryInfo` 的直接耦合。
- 阶段4继续3：`FindFiles` 已通过 `IFileContentStore` 执行目录存在性检查与枚举、并读取条目元数据，进一步降低 Dokan 层对 `System.IO` 直接耦合。
- 阶段4继续4：`SetFileAttributes` 与 `SetFileTime` 已通过 `IFileContentStore` 写入属性与时间戳，进一步减少 Dokan 层直接调用 `File/Directory` API。
- 阶段4继续5：修复 Dokan 删除语义，`DeleteFile/DeleteDirectory` 改为删除前检查，实际删除在 `Cleanup(DeleteOnClose)` 执行，避免“请求删除成功但文件未实际移除”的问题。
- 阶段4继续6：兼容部分 DokanNet 版本缺少 `IDokanFileInfo.DeleteOnClose` 的情况，删除流程改为用 `info.Context` 标记待删除并在 `Cleanup` 执行。
