using DokanNet;
using System.Security.AccessControl;
using FileAccess = DokanNet.FileAccess;

namespace VirtualEncryptedDisk;

/// <summary>
/// 最小可用的本地目录透传实现，避免依赖 DokanNet.Mirror 包。
/// </summary>
public sealed class DokanPassthroughOperations : IDokanOperations
{
    private readonly string _root;
    private readonly string _rootFullPath;
    private readonly bool _readOnly;
    private readonly IFileContentStore _contentStore;

    private sealed record PendingDelete(bool IsDirectory);

    public DokanPassthroughOperations(string root, bool readOnly, IFileContentStore? contentStore = null)
    {
        _root = root;
        _rootFullPath = EnsureTrailingSeparator(Path.GetFullPath(root));
        _readOnly = readOnly;
        _contentStore = contentStore ?? new PlainFileContentStore();
    }

    private string MapPath(string fileName)
    {
        var relative = fileName.TrimStart('\\');
        return Path.Combine(_root, relative);
    }

    private bool TryMapPathInRoot(string fileName, out string fullPath)
    {
        var mappedPath = MapPath(fileName);
        fullPath = Path.GetFullPath(mappedPath);
        return fullPath.StartsWith(_rootFullPath, StringComparison.OrdinalIgnoreCase)
               || string.Equals(fullPath, _rootFullPath.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    public NtStatus CreateFile(string fileName, FileAccess access, FileShare share, FileMode mode, FileOptions options,
        FileAttributes attributes, IDokanFileInfo info)
    {
        var path = MapPath(fileName);

        NtStatus Fail(NtStatus status, string reason)
        {
            DiagnosticLogger.Info($"CreateFile status={status}. File='{fileName}', Path='{path}', Mode={mode}, Access={access}, Reason={reason}.");
            return status;
        }

        try
        {
            // 根目录
            if (fileName == "\\" || string.IsNullOrEmpty(fileName))
            {
                info.IsDirectory = true;
                return NtStatus.Success;
            }

            var canWrite = (access & (FileAccess.WriteData | FileAccess.AppendData | FileAccess.GenericWrite)) != 0;
            if (_readOnly && canWrite)
            {
                return Fail(NtStatus.AccessDenied, "read-only write attempt");
            }

            var isDirectoryRequest = info.IsDirectory || attributes.HasFlag(FileAttributes.Directory);
            if (Directory.Exists(path))
            {
                info.IsDirectory = true;
                return mode == FileMode.CreateNew ? Fail(NtStatus.ObjectNameCollision, "directory already exists") : NtStatus.Success;
            }

            if (isDirectoryRequest)
            {
                info.IsDirectory = true;

                if (mode == FileMode.Open)
                {
                    return Fail(NtStatus.ObjectNameNotFound, "directory open target not found");
                }

                if (_readOnly)
                {
                    return Fail(NtStatus.AccessDenied, "read-only directory create");
                }

                Directory.CreateDirectory(path);
                return NtStatus.Success;
            }

            var exists = File.Exists(path);
            switch (mode)
            {
                case FileMode.Open:
                    if (!exists)
                    {
                        return Fail(NtStatus.ObjectNameNotFound, "file open target not found");
                    }
                    break;

                case FileMode.CreateNew:
                    if (exists)
                    {
                        return Fail(NtStatus.ObjectNameCollision, "file already exists");
                    }
                    if (_readOnly) return Fail(NtStatus.AccessDenied, "read-only create new");
                    _contentStore.EnsureParentDirectory(path);
                    break;

                case FileMode.Create:
                    if (_readOnly) return Fail(NtStatus.AccessDenied, "read-only create");
                    _contentStore.EnsureParentDirectory(path);
                    if (!exists)
                    {
                        using (File.Create(path)) { }
                    }
                    break;

                case FileMode.OpenOrCreate:
                    if (!exists)
                    {
                        if (_readOnly) return Fail(NtStatus.AccessDenied, "read-only open-or-create");
                        _contentStore.EnsureParentDirectory(path);
                        using (File.Create(path)) { }
                    }
                    break;

                case FileMode.Truncate:
                    if (!exists)
                    {
                        return Fail(NtStatus.ObjectNameNotFound, "truncate target not found");
                    }
                    if (_readOnly) return Fail(NtStatus.AccessDenied, "read-only truncate");
                    break;
            }

            return NtStatus.Success;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Error($"CreateFile exception. File='{fileName}', Path='{path}', Mode={mode}, Access={access}.", ex);
            return NtStatus.Unsuccessful;
        }
    }

    public void Cleanup(string fileName, IDokanFileInfo info)
    {
        if (_readOnly || info.Context is not PendingDelete pendingDelete)
        {
            return;
        }

        var path = MapPath(fileName);
        try
        {
            if (pendingDelete.IsDirectory)
            {
                if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                {
                    Directory.Delete(path, recursive: false);
                }
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Error($"Cleanup pending-delete failed. File='{fileName}', Path='{path}', IsDirectory={pendingDelete.IsDirectory}.", ex);
        }
        finally
        {
            info.Context = null;
        }
    }

    public void CloseFile(string fileName, IDokanFileInfo info)
    {
        info.Context = null;
    }

    public NtStatus ReadFile(string fileName, byte[] buffer, out int bytesRead, long offset, IDokanFileInfo info)
    {
        bytesRead = 0;
        var path = MapPath(fileName);

        try
        {
            if (!_contentStore.Exists(path))
            {
                DiagnosticLogger.Info($"ReadFile target not found. File='{fileName}', Path='{path}', Offset={offset}, Buffer={buffer.Length}.");
                return NtStatus.ObjectNameNotFound;
            }

            bytesRead = _contentStore.Read(path, buffer, offset);
            return NtStatus.Success;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Error($"ReadFile exception. File='{fileName}', Path='{path}', Offset={offset}, Buffer={buffer.Length}.", ex);
            return NtStatus.Unsuccessful;
        }
    }

    public NtStatus WriteFile(string fileName, byte[] buffer, out int bytesWritten, long offset, IDokanFileInfo info)
    {
        bytesWritten = 0;
        if (_readOnly)
        {
            DiagnosticLogger.Info($"WriteFile denied due to read-only. File='{fileName}', Offset={offset}, Buffer={buffer.Length}.");
            return NtStatus.AccessDenied;
        }

        var path = MapPath(fileName);

        try
        {
            _contentStore.EnsureParentDirectory(path);
            _contentStore.Write(path, buffer, offset, info.WriteToEndOfFile);
            bytesWritten = buffer.Length;
            return NtStatus.Success;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Error($"WriteFile exception. File='{fileName}', Path='{path}', Offset={offset}, Buffer={buffer.Length}, WriteToEnd={info.WriteToEndOfFile}.", ex);
            return NtStatus.Unsuccessful;
        }
    }

    public NtStatus FlushFileBuffers(string fileName, IDokanFileInfo info) => NtStatus.Success;

    public NtStatus GetFileInformation(string fileName, out FileInformation fileInfo, IDokanFileInfo info)
    {
        var path = MapPath(fileName);

        try
        {
            if (!_contentStore.Exists(path))
            {
                fileInfo = new FileInformation();
                return NtStatus.ObjectNameNotFound;
            }

            var isDirectory = _contentStore.IsDirectory(path);
            fileInfo = new FileInformation
            {
                FileName = Path.GetFileName(path),
                Attributes = isDirectory ? FileAttributes.Directory : _contentStore.GetAttributes(path),
                CreationTime = _contentStore.GetCreationTime(path),
                LastAccessTime = _contentStore.GetLastAccessTime(path),
                LastWriteTime = _contentStore.GetLastWriteTime(path),
                Length = isDirectory ? 0 : _contentStore.GetLength(path)
            };
            return NtStatus.Success;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Error($"GetFileInformation exception. File='{fileName}', Path='{path}'.", ex);
            fileInfo = new FileInformation();
            return NtStatus.Unsuccessful;
        }
    }

    public NtStatus FindFiles(string fileName, out IList<FileInformation> files, IDokanFileInfo info)
    {
        var path = MapPath(fileName);

        try
        {
            if (!_contentStore.Exists(path) || !_contentStore.IsDirectory(path))
            {
                files = Array.Empty<FileInformation>();
                return NtStatus.ObjectPathNotFound;
            }

            files = _contentStore.EnumerateFileSystemEntries(path)
                .Select(entry =>
                {
                    var isDir = _contentStore.IsDirectory(entry);
                    var attr = isDir ? FileAttributes.Directory : _contentStore.GetAttributes(entry);
                    return new FileInformation
                    {
                        FileName = Path.GetFileName(entry),
                        Attributes = attr,
                        CreationTime = _contentStore.GetCreationTime(entry),
                        LastAccessTime = _contentStore.GetLastAccessTime(entry),
                        LastWriteTime = _contentStore.GetLastWriteTime(entry),
                        Length = isDir ? 0 : _contentStore.GetLength(entry)
                    };
                })
                .ToList();

            return NtStatus.Success;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Error($"FindFiles exception. File='{fileName}', Path='{path}'.", ex);
            files = Array.Empty<FileInformation>();
            return NtStatus.Unsuccessful;
        }
    }


    public NtStatus FindFilesWithPattern(string fileName, string searchPattern, out IList<FileInformation> files, IDokanFileInfo info)
    {
        // 为兼容复杂通配符语义（某些大程序运行期依赖），直接返回完整列表，交由调用方过滤。
        return FindFiles(fileName, out files, info);
    }

    public NtStatus SetFileAttributes(string fileName, FileAttributes attributes, IDokanFileInfo info)
    {
        if (_readOnly)
        {
            DiagnosticLogger.Info($"SetFileAttributes denied due to read-only. File='{fileName}', Attr={attributes}.");
            return NtStatus.AccessDenied;
        }

        var path = MapPath(fileName);
        try
        {
            _contentStore.SetAttributes(path, attributes);
            return NtStatus.Success;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Error($"SetFileAttributes exception. File='{fileName}', Path='{path}', Attr={attributes}.", ex);
            return NtStatus.Unsuccessful;
        }
    }

    public NtStatus SetFileTime(string fileName, DateTime? creationTime, DateTime? lastAccessTime, DateTime? lastWriteTime,
        IDokanFileInfo info)
    {
        if (_readOnly) return NtStatus.AccessDenied;
        if (!TryMapPathInRoot(fileName, out var path))
        {
            return NtStatus.AccessDenied;
        }

        try
        {
            if (creationTime.HasValue)
            {
                _contentStore.SetCreationTime(path, creationTime.Value);
            }

            if (lastAccessTime.HasValue)
            {
                _contentStore.SetLastAccessTime(path, lastAccessTime.Value);
            }

            if (lastWriteTime.HasValue)
            {
                _contentStore.SetLastWriteTime(path, lastWriteTime.Value);
            }

            return NtStatus.Success;
        }
        catch (UnauthorizedAccessException ex)
        {
            DiagnosticLogger.Error($"SetFileTime access denied. FileName='{fileName}', Path='{path}'.", ex);
            return NtStatus.AccessDenied;
        }
        catch (DirectoryNotFoundException ex)
        {
            DiagnosticLogger.Error($"SetFileTime directory not found. FileName='{fileName}', Path='{path}'.", ex);
            return NtStatus.ObjectPathNotFound;
        }
        catch (FileNotFoundException ex)
        {
            DiagnosticLogger.Error($"SetFileTime file not found. FileName='{fileName}', Path='{path}'.", ex);
            return NtStatus.ObjectNameNotFound;
        }
        catch (IOException ex)
        {
            DiagnosticLogger.Error($"SetFileTime sharing violation. FileName='{fileName}', Path='{path}'.", ex);
            return NtStatus.SharingViolation;
        }
    }

    public NtStatus DeleteFile(string fileName, IDokanFileInfo info)
    {
        if (_readOnly)
        {
            DiagnosticLogger.Info($"DeleteFile denied due to read-only. File='{fileName}'.");
            return NtStatus.AccessDenied;
        }

        var path = MapPath(fileName);
        try
        {
            if (!File.Exists(path))
            {
                return NtStatus.ObjectNameNotFound;
            }

            if (Directory.Exists(path))
            {
                return NtStatus.AccessDenied;
            }

            // 当前 DokanNet 版本无 DeleteOnClose 暴露：改为用 info.Context 标记待删除，Cleanup 阶段执行实际删除。
            info.Context = new PendingDelete(IsDirectory: false);
            return NtStatus.Success;
        }
        catch (UnauthorizedAccessException ex)
        {
            DiagnosticLogger.Error($"DeleteFile access denied. File='{fileName}', Path='{path}'.", ex);
            return NtStatus.AccessDenied;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Error($"DeleteFile exception. File='{fileName}', Path='{path}'.", ex);
            return NtStatus.Unsuccessful;
        }
    }

    public NtStatus DeleteDirectory(string fileName, IDokanFileInfo info)
    {
        if (_readOnly)
        {
            DiagnosticLogger.Info($"DeleteDirectory denied due to read-only. File='{fileName}'.");
            return NtStatus.AccessDenied;
        }

        var path = MapPath(fileName);
        try
        {
            if (!Directory.Exists(path))
            {
                return NtStatus.ObjectPathNotFound;
            }

            if (Directory.EnumerateFileSystemEntries(path).Any())
            {
                return NtStatus.DirectoryNotEmpty;
            }

            // 当前 DokanNet 版本无 DeleteOnClose 暴露：改为用 info.Context 标记待删除，Cleanup 阶段执行实际删除。
            info.Context = new PendingDelete(IsDirectory: true);
            return NtStatus.Success;
        }
        catch (UnauthorizedAccessException ex)
        {
            DiagnosticLogger.Error($"DeleteDirectory access denied. File='{fileName}', Path='{path}'.", ex);
            return NtStatus.AccessDenied;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Error($"DeleteDirectory exception. File='{fileName}', Path='{path}'.", ex);
            return NtStatus.Unsuccessful;
        }
    }

    public NtStatus MoveFile(string oldName, string newName, bool replace, IDokanFileInfo info)
    {
        if (_readOnly) return NtStatus.AccessDenied;

        var oldPath = MapPath(oldName);
        var newPath = MapPath(newName);

        try
        {
            var parent = Path.GetDirectoryName(newPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            if (Directory.Exists(oldPath))
            {
                if (replace && Directory.Exists(newPath)) Directory.Delete(newPath, recursive: true);
                Directory.Move(oldPath, newPath);
                return NtStatus.Success;
            }

            if (replace && File.Exists(newPath))
            {
                File.Replace(oldPath, newPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(oldPath, newPath);
            }

            return NtStatus.Success;
        }
        catch (IOException ex)
        {
            DiagnosticLogger.Error($"MoveFile sharing violation. Old='{oldName}', New='{newName}'.", ex);
            return NtStatus.SharingViolation;
        }
        catch (UnauthorizedAccessException ex)
        {
            DiagnosticLogger.Error($"MoveFile access denied. Old='{oldName}', New='{newName}'.", ex);
            return NtStatus.AccessDenied;
        }
    }

    public NtStatus SetEndOfFile(string fileName, long length, IDokanFileInfo info)
    {
        if (_readOnly)
        {
            DiagnosticLogger.Info($"SetEndOfFile denied due to read-only. File='{fileName}', Length={length}.");
            return NtStatus.AccessDenied;
        }

        var path = MapPath(fileName);
        try
        {
            _contentStore.SetLength(path, length);
            return NtStatus.Success;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Error($"SetEndOfFile exception. File='{fileName}', Path='{path}', Length={length}.", ex);
            return NtStatus.Unsuccessful;
        }
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
        fileSystemName = "NTFS";
        maximumComponentLength = 255;
        features = FileSystemFeatures.CasePreservedNames |
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
        var path = MapPath(fileName);

        try
        {
            if (Directory.Exists(path))
            {
                security = new DirectorySecurity();
                return NtStatus.Success;
            }

            if (File.Exists(path))
            {
                security = new FileSecurity();
                return NtStatus.Success;
            }

            security = null;
            DiagnosticLogger.Info($"GetFileSecurity target not found. File='{fileName}', Path='{path}'.");
            return NtStatus.ObjectNameNotFound;
        }
        catch (Exception ex)
        {
            security = null;
            DiagnosticLogger.Error($"GetFileSecurity exception. File='{fileName}', Path='{path}', Sections={sections}.", ex);
            return NtStatus.Unsuccessful;
        }
    }

    public NtStatus SetFileSecurity(string fileName, FileSystemSecurity security,
        AccessControlSections sections, IDokanFileInfo info)
    {
        if (_readOnly)
        {
            DiagnosticLogger.Info($"SetFileSecurity denied due to read-only. File='{fileName}', Sections={sections}.");
            return NtStatus.AccessDenied;
        }

        var path = MapPath(fileName);
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            DiagnosticLogger.Info($"SetFileSecurity target not found. File='{fileName}', Path='{path}', Sections={sections}.");
            return NtStatus.ObjectNameNotFound;
        }

        DiagnosticLogger.Info($"SetFileSecurity accepted (no-op). File='{fileName}', Path='{path}', Sections={sections}, SecurityType={security.GetType().Name}.");
        return NtStatus.Success;
    }

    public NtStatus FindStreams(string fileName, out IList<FileInformation> streams, IDokanFileInfo info)
    {
        streams = Array.Empty<FileInformation>();
        return NtStatus.Success;
    }
}
