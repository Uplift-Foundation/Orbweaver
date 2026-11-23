using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Orbweaver
{
    // The board key Paperclip issues, and when it stops working, per base URL.
    // A file of its own in the settings directory, 0600 off Windows, never
    // settings.json: that file is ordinary preferences and is read by half the
    // app. The key is full board power (Paperclip has no read-only board key),
    // so it is never shown, typed, logged or put in the title of anything.
    internal sealed class PaperclipCredentials
    {
        internal sealed record Entry(string Key, DateTime Expires);

        private readonly object _gate = new();
        private readonly string _path;

        internal PaperclipCredentials(string directory)
        {
            _path = Path.Combine(directory, "paperclip-credentials.json");
        }

        internal static PaperclipCredentials Default() => new(OrbweaverSettings.Directory);

        private static string Norm(string baseUrl) => baseUrl.TrimEnd('/').ToLowerInvariant();

        internal Entry? Get(string baseUrl)
        {
            lock (_gate) return Load().GetValueOrDefault(Norm(baseUrl));
        }

        internal void Set(string baseUrl, string key, DateTime expires)
        {
            lock (_gate)
            {
                var map = Load();
                map[Norm(baseUrl)] = new Entry(key, expires);
                Save(map);
            }
        }

        internal void Delete(string baseUrl)
        {
            lock (_gate)
            {
                var map = Load();
                if (map.Remove(Norm(baseUrl))) Save(map);
            }
        }

        private Dictionary<string, Entry> Load()
        {
            try
            {
                if (!File.Exists(_path)) return new();
                return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path)) ?? new();
            }
            catch
            {
                // An unreadable store costs a reconnect, not a failure to start.
                return new();
            }
        }

        // Created owner-only from the first byte, on both platforms, and replaced
        // by renaming a file that was itself created that way, so it is never
        // briefly wider. Unix: mode 0600 on a temp file. Windows: an explicit ACL
        // with inheritance off and one rule, the current user with full control.
        // The profile folder's inherited ACL is not enough (measured on a real PC:
        // it also grants another principal read access), and a rename keeps the
        // source file's descriptor on the same volume, so a wider existing file
        // is replaced rather than edited and its ACL does not carry over.
        private void Save(Dictionary<string, Entry> map)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var fs = CreatePrivate(tmp))
                using (var w = new StreamWriter(fs)) w.Write(JsonSerializer.Serialize(map));
                File.Move(tmp, _path, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }

        internal static FileStream CreatePrivate(string path)
        {
            if (OperatingSystem.IsWindows()) return CreateOwnerOnlyWindows(path);
            return new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static FileStream CreateOwnerOnlyWindows(string path)
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
