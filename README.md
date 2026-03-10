# Virtual Encrypted Disk (C#)

这是一个“基于第三方驱动的虚拟硬件系统”示例：

- 使用 **AES-GCM + PBKDF2** 对虚拟硬盘容器加密。
- 用户必须输入正确密码后，才会调用驱动执行挂载。
- 默认提供 **Dokan.NET** 驱动适配器。

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
2. 写入临时目录中的 `disk.bin`。
3. 使用项目内的 `DokanPassthroughOperations`（`IDokanOperations` 实现）并通过 `Dokan` 实例 API 挂载到盘符。
4. 卸载时释放 `IDokanInstance`、调用 `RemoveMountPoint` 并清理临时目录。

## 使用前准备（Windows）

1. 安装 Dokan Runtime（驱动）
2. 用“管理员身份”启动终端运行程序
3. 确认目标盘符（如 `R:`）没有被占用

## 快速接入真实第三方驱动

1. 保留 `IThirdPartyDiskDriver` 接口。
2. 可按同样方式新增 `WinFspDiskDriver` / `ImDiskDriver`。
3. 在 `Program.cs` 中替换为你的驱动实现。

## 安全建议

- 生产环境请将密码输入改为安全输入控件，并考虑敏感内存清理。
- 可加入 TPM/证书二次认证，避免单一口令风险。
- 建议增加容器完整性版本头、审计日志与失败重试锁定策略。
