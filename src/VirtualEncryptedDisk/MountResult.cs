namespace VirtualEncryptedDisk;

public sealed record MountResult(bool Success, bool PasswordValid, string? Error = null)
{
    public static MountResult Mounted() => new(true, true);
    public static MountResult InvalidPassword() => new(false, false, "密码错误，拒绝挂载。");
    public static MountResult DriverError(string error) => new(false, true, error);
}
