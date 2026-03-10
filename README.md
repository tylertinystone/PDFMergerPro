# Virtual Encrypted Disk (C#)

这是一个“基于第三方驱动的虚拟硬件系统”示例：

- 使用 **AES-GCM + PBKDF2** 对虚拟硬盘容器加密。
- 用户必须输入正确密码后，才会调用驱动执行挂载。
- 默认提供 **ImDisk** 真实驱动适配器（不是仅日志模拟）。

## 目录

- `src/VirtualEncryptedDisk/Program.cs`：控制台入口，读取密码并挂载。
- `SecureVirtualDiskManager.cs`：核心流程（创建容器、验密、挂载/卸载）。
- `EncryptionService.cs`：加解密与密钥派生。
- `ContainerFileService.cs`：容器文件格式读写。
- `ImDiskThirdPartyDiskDriver.cs`：通过 `imdisk` 命令实际挂载盘符。
- `IThirdPartyDiskDriver.cs`：第三方驱动统一抽象。

## 为什么“密码正确但看不到盘符”

之前如果使用的是 `MockThirdPartyDiskDriver`，它只打印日志，不会真的创建盘符。
现在默认改为 `ImDiskThirdPartyDiskDriver`，会调用 ImDisk 真正挂载。

此外，新版本会输出更具体的失败细节（退出码、stdout/stderr）：

- `imdisk` 不在 PATH / 未安装。
- 盘符已被占用。
- 权限不足（未管理员运行）。
- ImDisk 命令执行失败。

## 使用前准备（Windows）

1. 安装 ImDisk Toolkit（确保 `imdisk.exe` 在 PATH 中）。
2. 用“管理员身份”启动终端运行程序。
3. 确认目标盘符（如 `R:`）没有被占用。

## 快速接入真实第三方驱动

1. 保留 `IThirdPartyDiskDriver` 接口。
2. 可按同样方式新增 `DokanDiskDriver` / `WinFspDiskDriver`。
3. 在 `Program.cs` 中替换为你的驱动实现。

## 安全建议

- 生产环境请将密码输入改为安全输入控件，并考虑敏感内存清理。
- 可加入 TPM/证书二次认证，避免单一口令风险。
- 建议增加容器完整性版本头、审计日志与失败重试锁定策略。
