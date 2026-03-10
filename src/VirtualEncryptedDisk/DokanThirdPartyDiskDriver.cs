using DokanNet;
using DokanNet.Logging;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VirtualEncryptedDisk;

/// <summary>
/// 基于 Dokan.NET 的直接挂载实现（不使用反射）。
/// </summary>
public sealed class DokanThirdPartyDiskDriver : IThirdPartyDiskDriver, IPersistableDiskDriver
{
    private string? _mountedRoot;
    private DokanInstance? _instance;
    private FileStream? _logStream;
    private TextWriterTraceListener? _traceListener;
    private byte[]? _lastArchiveBytes;
    private readonly object _sync = new();

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

        var runtimeBase = ResolveRuntimeBasePath(config);
        Directory.CreateDirectory(runtimeBase);
        DiagnosticLogger.Info($"Mount start. RuntimeBase='{runtimeBase}', MountPoint='{mountPoint}'.");
        var root = Path.Combine(runtimeBase, $"ved-dokan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        // 将容器明文解释为目录归档，并解压到挂载后端目录。
        VirtualDiskArchiveService.ExtractToDirectory(decryptedDiskBytes, root);

        var fs = new DokanPassthroughOperations(root, config.ReadOnly);
        var logger = CreateLogger();

        try
        {
            var dokan = new Dokan(logger);
            _instance = new DokanInstanceBuilder(dokan)
                .ConfigureOptions(o =>
                {
                    o.MountPoint = mountPoint;
                    o.Options = DokanOptions.MountManager | DokanOptions.CurrentSession;
                    if (config.ReadOnly)
                    {
                        o.Options |= DokanOptions.WriteProtection;
                    }
                })
                .Build(fs);
        }
        catch (DllNotFoundException ex)
        {
            CleanupLogger();
            Directory.Delete(root, recursive: true);
            DiagnosticLogger.Error("Dokan runtime missing while mounting.", ex);
            throw new InvalidOperationException(
                $"Dokan Runtime 缺失：{ex.Message}。请安装 Dokan Runtime（包含 dokan2.dll）。", ex);
        }
        catch (Exception ex)
        {
            CleanupLogger();
            Directory.Delete(root, recursive: true);
            DiagnosticLogger.Error($"Unexpected mount failure. Root='{root}', MountPoint='{mountPoint}'.", ex);
            throw;
        }

        _mountedRoot = root;
        DiagnosticLogger.Info($"Mount completed. Root='{root}', MountPoint='{mountPoint}'.");
        await Task.CompletedTask;
    }

    public Task UnmountAsync(string mountPoint, CancellationToken ct = default)
    {
        DiagnosticLogger.Info($"Unmount start. MountPoint='{mountPoint}', Root='{_mountedRoot}'.");
        _instance?.Dispose();
        _instance = null;

        lock (_sync)
        {
            if (_mountedRoot is not null && Directory.Exists(_mountedRoot))
            {
                _lastArchiveBytes = CaptureArchiveWithRetries(_mountedRoot, maxAttempts: 10, delayMs: 200);
            }
        }

        CleanupLogger();

        if (_mountedRoot is not null && Directory.Exists(_mountedRoot))
        {
            Directory.Delete(_mountedRoot, recursive: true);
            _mountedRoot = null;
        }

        DiagnosticLogger.Info($"Unmount completed. MountPoint='{mountPoint}'.");
        return Task.CompletedTask;
    }

    public byte[]? TakeUpdatedDiskBytes()
    {
        lock (_sync)
        {
            var bytes = _lastArchiveBytes;
            _lastArchiveBytes = null;
            return bytes;
        }
    }

    public byte[]? CaptureSnapshotDiskBytes()
    {
        lock (_sync)
        {
            if (_mountedRoot is null || !Directory.Exists(_mountedRoot))
            {
                return null;
            }

            return CaptureArchiveWithRetries(_mountedRoot, maxAttempts: 3, delayMs: 100);
        }
    }

    private static byte[] CaptureArchiveWithRetries(string root, int maxAttempts, int delayMs)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return VirtualDiskArchiveService.CreateFromDirectory(root);
            }
            catch (IOException ex)
            {
                lastError = ex;
                DiagnosticLogger.Error($"Archive capture attempt {attempt}/{maxAttempts} failed for root '{root}'.", ex);
            }

            if (attempt < maxAttempts)
            {
                Thread.Sleep(delayMs);
            }
        }

        throw new IOException("无法在卸载时完成归档，部分文件可能仍被其他进程占用。", lastError);
    }

    private static string ResolveRuntimeBasePath(VirtualDiskConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.RuntimeRootPath))
        {
            return Path.GetFullPath(config.RuntimeRootPath);
        }

        var containerFullPath = Path.GetFullPath(config.ContainerPath);
        var containerDir = Path.GetDirectoryName(containerFullPath) ?? Directory.GetCurrentDirectory();
        return Path.Combine(containerDir, ".ved-runtime");
    }

    private ILogger CreateLogger()
    {
        try
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VirtualEncryptedDiskLogs");
            Directory.CreateDirectory(logDirectory);

            var logFilePath = Path.Combine(logDirectory, $"dokan_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            _logStream = new FileStream(logFilePath, FileMode.Append, System.IO.FileAccess.Write, FileShare.Read);
            _traceListener = new TextWriterTraceListener(_logStream);
            Trace.Listeners.Add(_traceListener);
            Trace.AutoFlush = true;

            Console.WriteLine($"Dokan logs: {logFilePath}");
            return new TraceLogger();
        }
        catch (Exception ex)
        {
            CleanupLogger();
            DiagnosticLogger.Error("CreateLogger failed, fallback to console logger.", ex);
            return new ConsoleLogger("[Dokan] ");
        }
    }

    private void CleanupLogger()
    {
        if (_traceListener is not null)
        {
            Trace.Listeners.Remove(_traceListener);
            _traceListener.Flush();
            _traceListener.Close();
            _traceListener = null;
        }

        _logStream?.Dispose();
        _logStream = null;
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
