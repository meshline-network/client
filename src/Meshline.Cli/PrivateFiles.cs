using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Meshline.Cli;

internal static class PrivateFiles
{
    public static void EnsureDirectory(string path)
    {
        if (Directory.Exists(path)) return;
        if (OperatingSystem.IsWindows())
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).Create(security);
        }
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public static void RestrictFile(string path)
    {
        if (OperatingSystem.IsWindows()) SetWindowsFilePermissions(path);
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public static void RestrictDataDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        if (directory.LinkTarget is not null || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new CliException("data_directory_link", "Profile data must be in a regular private directory.", Exit.Configuration);
        if (OperatingSystem.IsWindows())
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(WindowsIdentity.GetCurrent().User!);
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            directory.SetAccessControl(security);
        }
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [SupportedOSPlatform("windows")]
    static void SetWindowsFilePermissions(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(WindowsIdentity.GetCurrent().User!);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    public static void VerifySecretFile(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new CliException("credential_missing", "The configured credential file does not exist.", Exit.Credentials);
        if (file.LinkTarget is not null || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new CliException("credential_link", "Credential files must be regular files, not symbolic links.", Exit.Credentials);
        if (OperatingSystem.IsWindows()) VerifyWindowsFilePermissions(file);
        else if ((File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new CliException("credential_permissions", "Credential file must be accessible only to its owner (for example mode 0600).", Exit.Credentials);
    }

    [SupportedOSPlatform("windows")]
    static void VerifyWindowsFilePermissions(FileInfo file)
    {
        var security = file.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        var current = WindowsIdentity.GetCurrent().User!;
        var trusted = new[] { current.Value, "S-1-5-18", "S-1-5-32-544" };
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !trusted.Contains(owner.Value))
            throw new CliException("credential_owner", "Credential file is not owned by the current user, SYSTEM or Administrators.", Exit.Credentials);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && !trusted.Contains(rule.IdentityReference.Value)
                && (rule.FileSystemRights & (FileSystemRights.ReadData | FileSystemRights.WriteData | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)) != 0)
                throw new CliException("credential_permissions", "Credential file grants another identity access. Restrict its ACL before use.", Exit.Credentials);
    }

    public static async Task WriteAtomicAsync(string path, ReadOnlyMemory<byte> bytes, bool overwrite, CancellationToken token)
    {
        path = Path.GetFullPath(path);
        EnsureDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                RestrictFile(temporary);
                await file.WriteAsync(bytes, token);
                await file.FlushAsync(token);
                file.Flush(true);
            }
            File.Move(temporary, path, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal sealed class FileLock : IDisposable
{
    readonly FileStream stream;
    FileLock(FileStream stream) => this.stream = stream;
    public static FileLock Acquire(string path)
    {
        PrivateFiles.EnsureDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try { PrivateFiles.RestrictFile(path); return new(stream); }
            catch { stream.Dispose(); throw; }
        }
        catch (IOException e) when ((e.HResult & 0xffff) is 11 or 32 or 33) { throw new CliException("profile_busy", "This profile or configuration is already in use by another process.", Exit.Busy); }
    }
    public void Dispose() => stream.Dispose();
}
