using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
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

    public static void VerifySecretFile(string path, bool allowCurrentUserAcl = false)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new CliException("credential_missing", "The configured credential file does not exist.", Exit.Credentials);
        if (file.LinkTarget is not null || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new CliException("credential_link", "Credential files must be regular files, not symbolic links.", Exit.Credentials);
        if (OperatingSystem.IsWindows()) VerifyWindowsFilePermissions(file);
        else if ((File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0
            && !(allowCurrentUserAcl && OperatingSystem.IsLinux() && HasPrivateLinuxAcl(path)))
            throw new CliException("credential_permissions", "Credential file must be accessible only to its owner (for example mode 0600).", Exit.Credentials);
    }

    [SupportedOSPlatform("linux")]
    static bool HasPrivateLinuxAcl(string path)
    {
        // systemd grants the service UID access with a POSIX ACL. In st_mode,
        // the group bits then describe the ACL mask, not the owning group's access.
        var bytes = new byte[65536]; // Linux's maximum extended attribute value size.
        var length = GetXattr(path, "system.posix_acl_access", bytes, (nuint)bytes.Length);
        if (length < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is 61 or 95) return false; // ENODATA or EOPNOTSUPP: no usable ACL.
            throw new IOException("Could not inspect the credential's POSIX ACL.", new Win32Exception(error));
        }
        return IsPrivateLinuxAcl(bytes.AsSpan(0, checked((int)length)), GetEffectiveUserId());
    }

    internal static bool IsPrivateLinuxAcl(ReadOnlySpan<byte> acl, uint currentUser)
    {
        if (acl.Length < 28 || (acl.Length - 4) % 8 != 0 || BinaryPrimitives.ReadUInt32LittleEndian(acl) != 2) return false;
        var mask = 7;
        for (var offset = 4; offset < acl.Length; offset += 8)
            if (BinaryPrimitives.ReadUInt16LittleEndian(acl[offset..]) == 16)
                mask = BinaryPrimitives.ReadUInt16LittleEndian(acl[(offset + 2)..]);
        for (var offset = 4; offset < acl.Length; offset += 8)
        {
            var tag = BinaryPrimitives.ReadUInt16LittleEndian(acl[offset..]);
            var permissions = BinaryPrimitives.ReadUInt16LittleEndian(acl[(offset + 2)..]);
            var id = BinaryPrimitives.ReadUInt32LittleEndian(acl[(offset + 4)..]);
            if ((permissions & ~7) != 0) return false;
            switch (tag)
            {
                case 1:
                case 16: break; // Owner and mask; the kernel validates ACL structure.
                case 2:
                    if (id != currentUser && id != 0 && (permissions & mask) != 0) return false;
                    break;
                case 4:
                case 8:
                    if ((permissions & mask) != 0) return false;
                    break;
                case 32:
                    if (permissions != 0) return false;
                    break;
                default: return false;
            }
        }
        return true;
    }

    [DllImport("libc", EntryPoint = "getxattr", SetLastError = true)]
    static extern nint GetXattr([MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [Out] byte[] value, nuint size);

    [DllImport("libc", EntryPoint = "geteuid")]
    static extern uint GetEffectiveUserId();

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
