using System.Security.AccessControl;
using System.Security.Principal;

namespace Harness.Infrastructure.Telemetry;

public static class PrivateExportFiles
{
    public static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) is not 0)
        { throw new IOException("Refusing a linked export file."); }
        using (var stream = new FileStream(path, options))
        { /* Create empty before restricting the ACL; no secret bytes are written yet. */ }
        OwnerOnly(path);
        File.WriteAllBytes(path, bytes);
    }
    public static void OwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(identity.User!);
            security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
        else
        { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    }
}
