using System;
using System.IO;
using System.Text.Json;

namespace Orbweaver
{
    // The roster on disk: paperclip-roster.json beside the settings, created private
    // exactly as the credentials file is (0600 on Unix, an owner-only ACL on Windows,
    // via a private temp file renamed over the real one) even though it holds no
    // secret, because it names a company's agents. A corrupt or unreadable file is
    // ignored silently: the cost is one launch without the remembered picture.
    internal sealed class FileRosterStore : IRosterStore
    {
        internal const string FileName = "paperclip-roster.json";

        private readonly object _gate = new();
        private readonly string _path;

        internal FileRosterStore(string directory) => _path = Path.Combine(directory, FileName);

        internal string Path_ => _path;

        public RosterFile? Load()
        {
            lock (_gate)
            {
                try
                {
                    if (!File.Exists(_path)) return null;
                    return JsonSerializer.Deserialize<RosterFile>(File.ReadAllText(_path));
                }
                catch
                {
                    return null;
                }
            }
        }

        public void Save(RosterFile roster)
        {
            lock (_gate)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    var tmp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        using (var fs = PaperclipCredentials.CreatePrivate(tmp))
                        using (var w = new StreamWriter(fs)) w.Write(JsonSerializer.Serialize(roster));
                        File.Move(tmp, _path, overwrite: true);
                    }
                    finally
                    {
                        if (File.Exists(tmp)) File.Delete(tmp);
                    }
                }
                catch
                {
                    // Nothing remembered is the worst outcome allowed.
                }
            }
        }

        public void Delete()
        {
            lock (_gate)
            {
                try { if (File.Exists(_path)) File.Delete(_path); } catch { }
            }
        }
    }
}
