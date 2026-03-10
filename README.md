# Virtual Encrypted Disk (C#)

这是一个“基于第三方驱动的虚拟硬件系统”示例：

- 使用 **AES-GCM + PBKDF2** 对虚拟硬盘容器加密。
- 用户必须输入正确密码后，才会调用驱动执行挂载。
- 通过 `IThirdPartyDiskDriver` 抽象第三方驱动（如 Dokan/WinFsp/ImDisk）的对接。

## 目录

- `src/VirtualEncryptedDisk/Program.cs`：控制台入口，读取密码并挂载。
- `SecureVirtualDiskManager.cs`：核心流程（创建容器、验密、挂载/卸载）。
- `EncryptionService.cs`：加解密与密钥派生。
- `ContainerFileService.cs`：容器文件格式读写。
- `MockThirdPartyDiskDriver.cs`：驱动适配器示例实现。

## 快速接入真实第三方驱动

1. 保留 `IThirdPartyDiskDriver` 接口。
2. 新建 `DokanDiskDriver` 或 `ImDiskDriver`，在 `MountAsync` 内调用对应 SDK/API。
3. 将 `Program.cs` 中的 `MockThirdPartyDiskDriver` 替换为真实驱动实现。

## 安全建议

- 生产环境请将密码输入改为安全输入控件，并考虑内存清理。
- 可加入 TPM/证书二次认证，避免单一口令风险。
- 建议增加容器完整性版本头、审计日志与失败重试锁定策略。
