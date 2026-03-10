using System.IO.Compression;

namespace VirtualEncryptedDisk;

public static class VirtualDiskArchiveService
{
    public static byte[] CreateEmptyArchive()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
        }

        return ms.ToArray();
    }

    public static void ExtractToDirectory(byte[] archiveBytes, string rootDirectory)
    {
        if (archiveBytes.Length == 0)
        {
            return;
        }

        using var ms = new MemoryStream(archiveBytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);
        foreach (var entry in zip.Entries)
        {
            var fullPath = Path.Combine(rootDirectory, entry.FullName);

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(fullPath);
                continue;
            }

            var parent = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            entry.ExtractToFile(fullPath, overwrite: true);
        }
    }

    public static byte[] CreateFromDirectory(string rootDirectory)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var dir in Directory.EnumerateDirectories(rootDirectory, "*", SearchOption.AllDirectories))
            {
                var relDir = Path.GetRelativePath(rootDirectory, dir).Replace('\\', '/').TrimEnd('/') + "/";
                zip.CreateEntry(relDir);
            }

            foreach (var file in Directory.EnumerateFiles(rootDirectory, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(rootDirectory, file).Replace('\\', '/');
                var entry = zip.CreateEntry(rel, CompressionLevel.Fastest);

                using var entryStream = entry.Open();
                using var input = OpenReadWithRetries(file, maxAttempts: 5, delayMs: 200);
                input.CopyTo(entryStream);
            }
        }

        return ms.ToArray();
    }

    private static FileStream OpenReadWithRetries(string filePath, int maxAttempts, int delayMs)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return new FileStream(
                    filePath,
                    FileMode.Open,
                    System.IO.FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
            }
            catch (IOException ex)
            {
                lastError = ex;
            }
            catch (UnauthorizedAccessException ex)
            {
                lastError = ex;
            }

            if (attempt < maxAttempts)
            {
                Thread.Sleep(delayMs);
            }
        }

        throw new IOException($"无法读取文件 '{filePath}'，该文件可能仍被其他进程占用。", lastError);
    }
}
