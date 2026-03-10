using VirtualEncryptedDisk;

var cfg = new VirtualDiskConfig(
    ContainerPath: "secure-disk.ved",
    MountPoint: "R:",
    SizeMb: 32,
    ReadOnly: false
);

var manager = new SecureVirtualDiskManager(
    new EncryptionService(),
    new ContainerFileService(),
    new MockThirdPartyDiskDriver()
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
var mounted = await manager.MountWithPasswordAsync(cfg, password);

if (!mounted)
{
    Console.WriteLine("密码错误，拒绝挂载。");
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
