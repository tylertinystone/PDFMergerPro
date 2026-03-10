using System.Reflection;
using System.Runtime.InteropServices;

namespace VirtualEncryptedDisk;

/// <summary>
/// 基于 Dokan.NET 的挂载实现（不依赖 DokanNet.Mirror）。
/// 通过反射适配不同 DokanNet 2.2.x API 形态，避免版本差异导致编译失败。
/// </summary>
public sealed class DokanThirdPartyDiskDriver : IThirdPartyDiskDriver
{
    private string? _mountedRoot;
    private IDisposable? _instance;

    public async Task MountAsync(VirtualDiskConfig config, byte[] decryptedDiskBytes, CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException("Dokan.NET 仅支持 Windows。");
        }

        var mountPoint = NormalizeMountPoint(config.MountPoint);
        if (Directory.Exists($"{mountPoint}\\"))
        {
            throw new InvalidOperationException($"盘符 {mountPoint} 已被占用。");
        }

        var root = Path.Combine(Path.GetTempPath(), $"ved-dokan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        var diskImagePath = Path.Combine(root, "disk.bin");
        await File.WriteAllBytesAsync(diskImagePath, decryptedDiskBytes, ct);

        var fs = new DokanPassthroughOperations(root, config.ReadOnly);

        try
        {
            _instance = await Task.Run(() => CreateDokanInstance(fs, mountPoint, config.ReadOnly), ct);
        }
        catch
        {
            Directory.Delete(root, recursive: true);
            throw;
        }

        _mountedRoot = root;
    }

    public Task UnmountAsync(string mountPoint, CancellationToken ct = default)
    {
        _instance?.Dispose();
        _instance = null;

        if (_mountedRoot is not null && Directory.Exists(_mountedRoot))
        {
            Directory.Delete(_mountedRoot, recursive: true);
            _mountedRoot = null;
        }

        return Task.CompletedTask;
    }

    private static IDisposable CreateDokanInstance(DokanPassthroughOperations fs, string mountPoint, bool readOnly)
    {
        var dokanAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "DokanNet")
            ?? throw new InvalidOperationException("未加载 DokanNet 程序集。");

        var builderType = dokanAsm.GetType("DokanNet.DokanInstanceBuilder")
            ?? throw new InvalidOperationException("当前 DokanNet 版本未找到 DokanInstanceBuilder。");

        var optionsType = dokanAsm.GetType("DokanNet.DokanOptions")
            ?? throw new InvalidOperationException("当前 DokanNet 版本未找到 DokanOptions。");

        var fixedDrive = Enum.Parse(optionsType, "FixedDrive");
        var options = fixedDrive;
        if (readOnly)
        {
            var writeProtection = Enum.Parse(optionsType, "WriteProtection");
            options = Enum.ToObject(optionsType, Convert.ToInt32(options) | Convert.ToInt32(writeProtection));
        }

        var ctor = builderType.GetConstructors()
            .OrderBy(c => c.GetParameters().Length)
            .FirstOrDefault(c => c.GetParameters().Length >= 1)
            ?? throw new InvalidOperationException("DokanInstanceBuilder 构造器不可用。");

        var ctorArgs = ctor.GetParameters().Length switch
        {
            1 => new object?[] { fs },
            2 => new object?[] { null, fs },
            _ => throw new InvalidOperationException("不支持的 DokanInstanceBuilder 构造签名。")
        };

        var builder = ctor.Invoke(ctorArgs);

        InvokeIfExists(builder, "ConfigureMountPoint", mountPoint);
        InvokeIfExists(builder, "ConfigureOptions", options);

        var build = builderType.GetMethod("Build", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException("DokanInstanceBuilder.Build 不可用。");

        var instance = build.Invoke(builder, null) as IDisposable;
        return instance ?? throw new InvalidOperationException("Dokan Build 返回值不可释放或为空。");
    }

    private static void InvokeIfExists(object target, string methodName, object argument)
    {
        var method = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == 1);
        method?.Invoke(target, new[] { argument });
    }

    private static string NormalizeMountPoint(string mountPoint)
    {
        if (string.IsNullOrWhiteSpace(mountPoint))
        {
            throw new ArgumentException("挂载盘符不能为空。", nameof(mountPoint));
        }

        var letter = char.ToUpperInvariant(mountPoint.Trim()[0]);
        if (letter is < 'A' or > 'Z')
        {
            throw new ArgumentException("挂载点必须是盘符，例如 R:", nameof(mountPoint));
        }

        return $"{letter}:";
    }
}
