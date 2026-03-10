using System.Linq.Expressions;
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
        try
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

            var builder = CreateBuilder(builderType, dokanAsm, fs);
            InvokeIfExists(builder, "ConfigureMountPoint", mountPoint);
            InvokeIfExists(builder, "ConfigureOptions", options);

            var instance = InvokeBuild(builderType, builder) as IDisposable;
            return instance ?? throw new InvalidOperationException("Dokan Build 返回值不可释放或为空。");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is DllNotFoundException dllEx)
        {
            throw new InvalidOperationException(
                $"Dokan Runtime 缺失：{dllEx.Message}。请安装 Dokan Runtime（包含 dokan2.dll）。", ex);
        }
    }


    private static object? InvokeBuild(Type builderType, object builder)
    {
        var buildMethods = builderType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == "Build")
            .OrderBy(m => m.GetParameters().Length)
            .ToList();

        if (buildMethods.Count == 0)
        {
            throw new InvalidOperationException("DokanInstanceBuilder.Build 不可用。");
        }

        Exception? lastError = null;
        foreach (var method in buildMethods)
        {
            try
            {
                var args = BuildMethodArgs(method.GetParameters());
                return method.Invoke(builder, args);
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw new InvalidOperationException("未找到可调用的 DokanInstanceBuilder.Build 重载。", lastError);
    }

    private static object?[] BuildMethodArgs(ParameterInfo[] parameters)
    {
        var args = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            args[i] = CreateParameterValue(p);
        }

        return args;
    }

    private static object? CreateParameterValue(ParameterInfo p)
    {
        if (p.HasDefaultValue)
        {
            return p.DefaultValue;
        }

        var t = p.ParameterType;
        if (t == typeof(CancellationToken))
        {
            return CancellationToken.None;
        }

        if (t.IsByRef)
        {
            t = t.GetElementType()!;
        }

        if (t.IsValueType)
        {
            return Activator.CreateInstance(t);
        }

        if (typeof(Delegate).IsAssignableFrom(t))
        {
            return CreateNoOpDelegate(t);
        }

        return null;
    }

    private static object? CreateNoOpDelegate(Type delegateType)
    {
        var invoke = delegateType.GetMethod("Invoke");
        if (invoke is null)
        {
            return null;
        }

        var parameters = invoke.GetParameters()
            .Select(p => Expression.Parameter(p.ParameterType, p.Name))
            .ToArray();

        Expression body = invoke.ReturnType == typeof(void)
            ? Expression.Empty()
            : Expression.Default(invoke.ReturnType);

        var lambda = Expression.Lambda(delegateType, body, parameters);
        return lambda.Compile();
    }

    private static object CreateBuilder(Type builderType, Assembly dokanAsm, DokanPassthroughOperations fs)
    {
        var ctors = builderType.GetConstructors().OrderBy(c => c.GetParameters().Length);
        foreach (var ctor in ctors)
        {
            var parameters = ctor.GetParameters();
            var args = new object?[parameters.Length];
            var ok = true;

            for (var i = 0; i < parameters.Length; i++)
            {
                var pType = parameters[i].ParameterType;

                if (pType.IsInstanceOfType(fs))
                {
                    args[i] = fs;
                    continue;
                }

                if (pType.FullName == "DokanNet.Dokan")
                {
                    args[i] = CreateDokanInstanceObject(dokanAsm, pType);
                    continue;
                }

                if (parameters[i].HasDefaultValue || Nullable.GetUnderlyingType(pType) is not null || !pType.IsValueType)
                {
                    args[i] = parameters[i].DefaultValue;
                    continue;
                }

                ok = false;
                break;
            }

            if (ok)
            {
                return ctor.Invoke(args);
            }
        }

        throw new InvalidOperationException("无法匹配 DokanInstanceBuilder 构造签名，请检查 DokanNet 版本。");
    }

    private static object CreateDokanInstanceObject(Assembly dokanAsm, Type dokanType)
    {
        var logger = TryCreateLogger(dokanAsm);
        if (logger is not null)
        {
            var ctorWithLogger = dokanType.GetConstructors()
                .FirstOrDefault(c => c.GetParameters().Length == 1 && c.GetParameters()[0].ParameterType.IsInstanceOfType(logger));
            if (ctorWithLogger is not null)
            {
                return ctorWithLogger.Invoke(new[] { logger });
            }
        }

        var parameterless = dokanType.GetConstructor(Type.EmptyTypes);
        if (parameterless is not null)
        {
            return parameterless.Invoke(null);
        }

        throw new InvalidOperationException("无法创建 Dokan 实例：未找到兼容构造器。");
    }

    private static object? TryCreateLogger(Assembly dokanAsm)
    {
        var nullLoggerType = dokanAsm.GetType("DokanNet.Logging.NullLogger");
        if (nullLoggerType is not null)
        {
            var nullLogger = Activator.CreateInstance(nullLoggerType);
            if (nullLogger is not null)
            {
                return nullLogger;
            }
        }

        var consoleLoggerType = dokanAsm.GetType("DokanNet.Logging.ConsoleLogger");
        if (consoleLoggerType is null)
        {
            return null;
        }

        var parameterless = consoleLoggerType.GetConstructor(Type.EmptyTypes);
        if (parameterless is not null)
        {
            return parameterless.Invoke(null);
        }

        var stringCtor = consoleLoggerType.GetConstructor(new[] { typeof(string) });
        if (stringCtor is not null)
        {
            return stringCtor.Invoke(new object[] { "[Dokan] " });
        }

        return null;
    }

    private static void InvokeIfExists(object target, string methodName, object argument)
    {
        var argType = argument.GetType();
        var candidates = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == methodName && m.GetParameters().Length == 1)
            .ToList();

        // 精确类型优先，避免选中 ConfigureOptions(delegate) 等重载
        var method = candidates.FirstOrDefault(m => m.GetParameters()[0].ParameterType == argType)
            ?? candidates.FirstOrDefault(m => m.GetParameters()[0].ParameterType.IsAssignableFrom(argType));

        if (method is null)
        {
            return;
        }

        method.Invoke(target, new[] { argument });
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
