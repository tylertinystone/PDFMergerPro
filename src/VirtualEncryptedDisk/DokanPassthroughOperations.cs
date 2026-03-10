using DokanNet;
using System.Security.AccessControl;
using System.Text.RegularExpressions;
using FileAccess = DokanNet.FileAccess;

namespace VirtualEncryptedDisk;

/// <summary>
/// 最小可用的本地目录透传实现，避免依赖 DokanNet.Mirror 包。
/// </summary>
public sealed class DokanPassthroughOperations : IDokanOperations
{
    private readonly string _root;
    private readonly bool _readOnly;

    public DokanPassthroughOperations(string root, bool readOnly)
    {
        _root = root;
        _readOnly = readOnly;
    }

    private string MapPath(string fileName)
    {
        var relative = fileName.TrimStart('\\');
        return Path.Combine(_root, relative);
    }

    public NtStatus CreateFile(string fileName, FileAccess access, FileShare share, FileMode mode, FileOptions options,
        FileAttributes attributes, IDokanFileInfo info)
    {
        var path = MapPath(fileName);

        if (Directory.Exists(path))
        {
            info.IsDirectory = true;
            return NtStatus.Success;
        }

        var canWrite = (access & (FileAccess.WriteData | FileAccess.AppendData | FileAccess.GenericWrite)) != 0;
        if (_readOnly && canWrite)
        {
            return NtStatus.AccessDenied;
        }

        if (mode == FileMode.Open && !File.Exists(path))
        {
            return NtStatus.ObjectNameNotFound;
        }

        return NtStatus.Success;
    }

    public void Cleanup(string fileName, IDokanFileInfo info) { }

    public void CloseFile(string fileName, IDokanFileInfo info) { }

    public NtStatus ReadFile(string fileName, byte[] buffer, out int bytesRead, long offset, IDokanFileInfo info)
    {
        bytesRead = 0;
        var path = MapPath(fileName);
        if (!File.Exists(path))
        {
            return NtStatus.ObjectNameNotFound;
        }

        using var fs = new FileStream(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite);
        fs.Position = offset;
        bytesRead = fs.Read(buffer, 0, buffer.Length);
        return NtStatus.Success;
    }

    public NtStatus WriteFile(string fileName, byte[] buffer, out int bytesWritten, long offset, IDokanFileInfo info)
    {
        bytesWritten = 0;
        if (_readOnly)
        {
            return NtStatus.AccessDenied;
        }

        var path = MapPath(fileName);
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        using var fs = new FileStream(path, FileMode.OpenOrCreate, System.IO.FileAccess.Write, FileShare.ReadWrite);
        fs.Position = offset;
        fs.Write(buffer, 0, buffer.Length);
        bytesWritten = buffer.Length;
        return NtStatus.Success;
    }

    public NtStatus FlushFileBuffers(string fileName, IDokanFileInfo info) => NtStatus.Success;

    public NtStatus GetFileInformation(string fileName, out FileInformation fileInfo, IDokanFileInfo info)
    {
        var path = MapPath(fileName);

        if (Directory.Exists(path))
        {
            var di = new DirectoryInfo(path);
            fileInfo = new FileInformation
            {
                FileName = di.Name,
                Attributes = FileAttributes.Directory,
                CreationTime = di.CreationTime,
                LastAccessTime = di.LastAccessTime,
                LastWriteTime = di.LastWriteTime,
                Length = 0
            };
            return NtStatus.Success;
        }

        if (!File.Exists(path))
        {
            fileInfo = new FileInformation();
            return NtStatus.ObjectNameNotFound;
        }

        var fi = new FileInfo(path);
        fileInfo = new FileInformation
        {
            FileName = fi.Name,
            Attributes = fi.Attributes,
            CreationTime = fi.CreationTime,
            LastAccessTime = fi.LastAccessTime,
            LastWriteTime = fi.LastWriteTime,
            Length = fi.Length
        };
        return NtStatus.Success;
    }

    public NtStatus FindFiles(string fileName, out IList<FileInformation> files, IDokanFileInfo info)
    {
        var path = MapPath(fileName);
        if (!Directory.Exists(path))
        {
            files = Array.Empty<FileInformation>();
            return NtStatus.ObjectPathNotFound;
        }

        files = Directory.EnumerateFileSystemEntries(path)
            .Select(entry =>
            {
                var attr = File.GetAttributes(entry);
                var isDir = attr.HasFlag(FileAttributes.Directory);
                var di = new FileInfo(entry);
                return new FileInformation
                {
                    FileName = Path.GetFileName(entry),
                    Attributes = attr,
                    CreationTime = di.CreationTime,
                    LastAccessTime = di.LastAccessTime,
                    LastWriteTime = di.LastWriteTime,
                    Length = isDir ? 0 : di.Length
                };
            })
            .ToList();

        return NtStatus.Success;
    }


    public NtStatus FindFilesWithPattern(string fileName, string searchPattern, out IList<FileInformation> files, IDokanFileInfo info)
    {
        var status = FindFiles(fileName, out var allFiles, info);
        if (status != NtStatus.Success)
        {
            files = allFiles;
            return status;
        }

        files = allFiles
            .Where(f => IsWildcardMatch(f.FileName, searchPattern))
            .ToList();
        return NtStatus.Success;
    }


    private static bool IsWildcardMatch(string input, string pattern)
    {
        var regexPattern = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        return Regex.IsMatch(input, regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public NtStatus SetFileAttributes(string fileName, FileAttributes attributes, IDokanFileInfo info)
    {
        if (_readOnly) return NtStatus.AccessDenied;
        File.SetAttributes(MapPath(fileName), attributes);
        return NtStatus.Success;
    }

    public NtStatus SetFileTime(string fileName, DateTime? creationTime, DateTime? lastAccessTime, DateTime? lastWriteTime,
        IDokanFileInfo info)
    {
        if (_readOnly) return NtStatus.AccessDenied;
        var path = MapPath(fileName);
        if (creationTime.HasValue) File.SetCreationTime(path, creationTime.Value);
        if (lastAccessTime.HasValue) File.SetLastAccessTime(path, lastAccessTime.Value);
        if (lastWriteTime.HasValue) File.SetLastWriteTime(path, lastWriteTime.Value);
        return NtStatus.Success;
    }

    public NtStatus DeleteFile(string fileName, IDokanFileInfo info)
    {
        if (_readOnly) return NtStatus.AccessDenied;
        var path = MapPath(fileName);
        if (File.Exists(path)) File.Delete(path);
        return NtStatus.Success;
    }

    public NtStatus DeleteDirectory(string fileName, IDokanFileInfo info)
    {
        if (_readOnly) return NtStatus.AccessDenied;
        var path = MapPath(fileName);
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        return NtStatus.Success;
    }

    public NtStatus MoveFile(string oldName, string newName, bool replace, IDokanFileInfo info)
    {
        if (_readOnly) return NtStatus.AccessDenied;

        var oldPath = MapPath(oldName);
        var newPath = MapPath(newName);
        if (Directory.Exists(oldPath))
        {
            if (replace && Directory.Exists(newPath)) Directory.Delete(newPath, recursive: true);
            Directory.Move(oldPath, newPath);
            return NtStatus.Success;
        }

        if (replace && File.Exists(newPath)) File.Delete(newPath);
        File.Move(oldPath, newPath);
        return NtStatus.Success;
    }

    public NtStatus SetEndOfFile(string fileName, long length, IDokanFileInfo info)
    {
        if (_readOnly) return NtStatus.AccessDenied;
        using var fs = new FileStream(MapPath(fileName), FileMode.OpenOrCreate, System.IO.FileAccess.Write, FileShare.ReadWrite);
        fs.SetLength(length);
        return NtStatus.Success;
    }

    public NtStatus SetAllocationSize(string fileName, long length, IDokanFileInfo info)
        => SetEndOfFile(fileName, length, info);

    public NtStatus LockFile(string fileName, long offset, long length, IDokanFileInfo info) => NtStatus.Success;

    public NtStatus UnlockFile(string fileName, long offset, long length, IDokanFileInfo info) => NtStatus.Success;

    public NtStatus GetDiskFreeSpace(out long freeBytesAvailable, out long totalNumberOfBytes,
        out long totalNumberOfFreeBytes, IDokanFileInfo info)
    {
        var drive = new DriveInfo(Path.GetPathRoot(_root)!);
        freeBytesAvailable = drive.AvailableFreeSpace;
        totalNumberOfBytes = drive.TotalSize;
        totalNumberOfFreeBytes = drive.TotalFreeSpace;
        return NtStatus.Success;
    }

    public NtStatus GetVolumeInformation(out string volumeLabel, out FileSystemFeatures features,
        out string fileSystemName, out uint maximumComponentLength, IDokanFileInfo info)
    {
        volumeLabel = "VED";
        fileSystemName = "Dokan";
        maximumComponentLength = 256;
        features = FileSystemFeatures.CasePreservedNames |
                   FileSystemFeatures.CaseSensitiveSearch |
                   FileSystemFeatures.UnicodeOnDisk |
                   FileSystemFeatures.PersistentAcls;
        if (_readOnly) features |= FileSystemFeatures.ReadOnlyVolume;
        return NtStatus.Success;
    }

    public NtStatus Mounted(string mountPoint, IDokanFileInfo info) => NtStatus.Success;

    public NtStatus Unmounted(IDokanFileInfo info) => NtStatus.Success;

    public NtStatus GetFileSecurity(string fileName, out FileSystemSecurity? security,
        AccessControlSections sections, IDokanFileInfo info)
    {
        security = null;
        return NtStatus.NotImplemented;
    }

    public NtStatus SetFileSecurity(string fileName, FileSystemSecurity security,
        AccessControlSections sections, IDokanFileInfo info)
    {
        if (_readOnly) return NtStatus.AccessDenied;
        return NtStatus.NotImplemented;
    }

    public NtStatus FindStreams(string fileName, out IList<FileInformation> streams, IDokanFileInfo info)
    {
        streams = Array.Empty<FileInformation>();
        return NtStatus.NotImplemented;
    }
}
