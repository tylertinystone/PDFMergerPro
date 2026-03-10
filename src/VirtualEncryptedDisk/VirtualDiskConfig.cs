namespace VirtualEncryptedDisk;

public sealed record VirtualDiskConfig(
    string ContainerPath,
    string MountPoint,
    int SizeMb,
    bool ReadOnly = false,
    string? RuntimeRootPath = null,
    bool AutosaveEnabled = true,
    int AutosaveIntervalSeconds = 5,
    bool PersistOnlyWhenChanged = true,
    bool UseChunkedContainerExperimental = false,
    int ChunkSizeBytes = 1 * 1024 * 1024
);
