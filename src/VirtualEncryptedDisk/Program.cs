using System.Diagnostics;
using System.Runtime.InteropServices;
using VirtualEncryptedDisk;

var cfg = new VirtualDiskConfig(
    ContainerPath: "secure-disk.ved",
    MountPoint: "R:",
    SizeMb: 32,
    ReadOnly: false
);

var driver = SelectBestDriver();
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

static IThirdPartyDiskDriver SelectBestDriver()
{
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && IsDokanRuntimeAvailable())
    {
        return new DokanThirdPartyDiskDriver();
    }

    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && IsImDiskAvailable())
    {
        return new ImDiskThirdPartyDiskDriver();
    }

    Console.WriteLine("警告: 未检测到可用真实驱动（Dokan/ImDisk），将使用 Mock 驱动（仅演示，不会产生真实盘符）。");
    return new MockThirdPartyDiskDriver();
}

static bool IsDokanRuntimeAvailable()
{
    try
    {
        if (!NativeLibrary.TryLoad("dokan2.dll", out var handle))
        {
            return false;
        }

        NativeLibrary.Free(handle);
        return true;
    }
    catch
    {
        return false;
    }
}

static bool IsImDiskAvailable()
{
    try
    {
        using var process = Process.Start(new ProcessStartInfo("imdisk", "-h")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        if (process is null)
        {
            return false;
        }

        process.WaitForExit(2000);
        return process.ExitCode == 0;
    }
    catch
    {
        return false;
    }
}

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
