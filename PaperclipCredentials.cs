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

        // Created owner-only from the first byte: a temp file opened with mode
        // 0600, then renamed over the real one. Writing first and chmod-ing after
        // leaves the key readable under the default umask for the window between.
        // Windows has no mode to set; the file lives under the user profile, whose
        // ACL is the protection, and that is not verified by any test here.
        private void Save(Dictionary<string, Entry> map)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            try
            {
                using (var fs = new FileStream(tmp, options))
                using (var w = new StreamWriter(fs)) w.Write(JsonSerializer.Serialize(map));
                File.Move(tmp, _path, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }
    }
}
