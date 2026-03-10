namespace VirtualEncryptedDisk;

public sealed record VirtualDiskConfig(
    string ContainerPath,
    string MountPoint,
    int SizeMb,
    bool ReadOnly = false,
    string? RuntimeRootPath = null
);
