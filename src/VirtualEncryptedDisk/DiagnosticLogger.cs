namespace VirtualEncryptedDisk;

internal static class DiagnosticLogger
{
    private static readonly object Sync = new();

    internal static readonly string LogFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VirtualEncryptedDiskLogs",
        "runtime.log");

    public static void Info(string message) => Write("INFO", message, null);

    public static void Error(string message, Exception ex) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            var directory = Path.GetDirectoryName(LogFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var line = $"{DateTimeOffset.Now:O} [{level}] {message}";
            if (ex is not null)
            {
                line += $" | {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
            }

            lock (Sync)
            {
                File.AppendAllText(LogFilePath, line + Environment.NewLine);
            }
        }
        catch
        {
            // 日志写入失败不能影响主流程。
        }
    }
}
