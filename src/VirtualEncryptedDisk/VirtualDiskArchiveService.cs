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
                zip.CreateEntryFromFile(file, rel, CompressionLevel.Fastest);
            }
        }

        return ms.ToArray();
    }
}
