using System;
using System.IO;
using System.Text;

namespace Orbweaver
{
    // The one writer for files that hold a secret. The file is owner-only from
    // its first byte on every platform and is replaced by renaming a file that
    // was itself created that way, so it is never briefly wider and a wider
    // existing file does not pass its permissions on to the replacement.
    // Unix: mode 0600. Windows: an explicit ACL with inheritance off and a single
    // rule, the current user with full control (the profile folder's inherited
    // ACL is not enough: on a real PC it also granted another local group and
    // an unresolved SID).
    internal static class OwnerOnlyFile
    {
        internal static void WriteAllText(string path, string text)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
            Directory.CreateDirectory(dir);
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var fs = Create(tmp))
                using (var w = new StreamWriter(fs, new UTF8Encoding(false))) w.Write(text);
                File.Move(tmp, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }

        internal static FileStream Create(string path)
        {
            if (OperatingSystem.IsWindows()) return CreateWindows(path);
            return new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static FileStream CreateWindows(string path)
        {
            var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
            var security = new System.Security.AccessControl.FileSecurity();
            security.SetOwner(user);
            // Inheritance off and nothing preserved: no rule from the folder applies.
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                user, System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.AccessControlType.Allow));
            return new FileInfo(path).Create(FileMode.CreateNew,
                System.Security.AccessControl.FileSystemRights.Write | System.Security.AccessControl.FileSystemRights.ReadPermissions,
                FileShare.None, 4096, FileOptions.None, security);
        }
    }
}
