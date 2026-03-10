using System.Reflection;
using VirtualEncryptedDisk;

var cfg = new VirtualDiskConfig(
    ContainerPath: "secure-disk.ved",
    MountPoint: "R:",
    SizeMb: 32,
    ReadOnly: false,
    AutosaveEnabled: false, // 先做安装兼容性排查：禁用自动快照
    AutosaveIntervalSeconds: 5,
    PersistOnlyWhenChanged: true,
    UseChunkedContainerExperimental: false,
    ChunkSizeBytes: 1 * 1024 * 1024
);

var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
Console.WriteLine($"VirtualEncryptedDisk 版本: {version}");
DiagnosticLogger.Info($"Application startup. Version={version}, PID={Environment.ProcessId}.");
Console.WriteLine($"运行日志: {DiagnosticLogger.LogFilePath}");
Console.WriteLine($"自动快照: {(cfg.AutosaveEnabled ? $"开启（{cfg.AutosaveIntervalSeconds}s）" : "关闭")}");
Console.WriteLine($"仅变更写回: {(cfg.PersistOnlyWhenChanged ? "开启" : "关闭")}");
Console.WriteLine($"分块容器实验模式: {(cfg.UseChunkedContainerExperimental ? $"开启（Chunk={cfg.ChunkSizeBytes}）" : "关闭")}");
Console.WriteLine("容器模式: 若检测到现有 VEC2 文件将自动走分块链路。");

IThirdPartyDiskDriver driver = new DokanThirdPartyDiskDriver();
Console.WriteLine($"当前驱动: {driver.GetType().Name}");

var manager = new SecureVirtualDiskManager(
    new EncryptionService(),
    new ContainerFileService(),
    driver
);

if (!File.Exists(cfg.ContainerPath))
{
    Console.Write("首次创建虚拟磁盘，请设置密码: ");
    var initPwd = ReadPassword();
    await manager.CreateEncryptedDiskAsync(cfg, initPwd);
    Console.WriteLine("加密容器已创建。\n");
}

Console.Write("请输入密码挂载硬盘: ");
var password = ReadPassword();
var result = await manager.MountWithPasswordAsync(cfg, password);

if (!result.Success)
{
    Console.WriteLine(result.Error ?? "挂载失败。");
    return;
}

Console.WriteLine($"已成功挂载到 {cfg.MountPoint}，按任意键卸载...");
Console.ReadKey(intercept: true);
await manager.UnmountAsync(cfg.MountPoint);

static string ReadPassword()
{
    var chars = new Stack<char>();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            return new string(chars.Reverse().ToArray());
        }

        if (key.Key == ConsoleKey.Backspace && chars.Count > 0)
        {
            chars.Pop();
            continue;
        }

        if (!char.IsControl(key.KeyChar))
        {
            chars.Push(key.KeyChar);
        }
    }
}
