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
            throw new InvalidOperationException(
                $"Dokan Runtime 缺失：{ex.Message}。请安装 Dokan Runtime（包含 dokan2.dll）。", ex);
        }
        catch
        {
            CleanupLogger();
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
            _lastArchiveBytes = VirtualDiskArchiveService.CreateFromDirectory(_mountedRoot);
        }

        CleanupLogger();

        if (_mountedRoot is not null && Directory.Exists(_mountedRoot))
        {
            Directory.Delete(_mountedRoot, recursive: true);
            _mountedRoot = null;
        }

        return Task.CompletedTask;
    }

    public byte[]? TakeUpdatedDiskBytes()
    {
        var bytes = _lastArchiveBytes;
        _lastArchiveBytes = null;
        return bytes;
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
        catch
        {
            CleanupLogger();
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
