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
- 阶段 3（本次，过渡版）：管理器可切换到分块容器存储链路（`UseChunkedContainerExperimental`），为后续 Dokan 读写直连分块容器做准备。
- 阶段 4：移除现有“整包解密->目录展开->整包回写”主路径。
