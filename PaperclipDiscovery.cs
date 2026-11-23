using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Orbweaver
{
    // Finding a Paperclip on this machine (plan 2.2). Pure over two seams: a
    // file reader and a function that asks one address for /api/health.
    //
    // Reads exactly two files per instance folder, runtime-info.json and
    // config.json, and nothing else in it (the folder also holds a secrets
    // directory and a database). Writes nothing. Runs when Settings opens and
    // once at launch, never on a timer.
    internal static class PaperclipDiscovery
    {
        internal const string DefaultAddress = "http://127.0.0.1:3100";

        internal sealed record Candidate(string BaseUrl, string InstanceName);
        internal sealed record Found(string BaseUrl, string InstanceName, string DeploymentMode);

        // The address is loopback whatever the files say: runtime-info.json
        // names 0.0.0.0 and a LAN dashboardUrl, but this machine reaches it
        // on 127.0.0.1.
        internal static IReadOnlyList<Candidate> Candidates(
            Func<string, IEnumerable<string>> listInstanceFolders,
            Func<string, string?> readFile,
            string instancesRoot)
        {
            var list = new List<Candidate>();
            IEnumerable<string> folders;
            try { folders = listInstanceFolders(instancesRoot).ToList(); }
            catch { folders = Array.Empty<string>(); }

            foreach (var folder in folders)
            {
                var name = Path.GetFileName(folder.TrimEnd('/', '\\'));
                int? port = null;
                try { port = PaperclipApi.ParseRuntimeInfo(readFile(Path.Combine(folder, "runtime-info.json")))?.Port; }
                catch { }
                if (port == null)
                {
                    try { port = PaperclipApi.ParseConfigPort(readFile(Path.Combine(folder, "config.json")))?.Port; }
                    catch { }
                }
                if (port != null) list.Add(new Candidate($"http://127.0.0.1:{port}", name));
            }

            if (list.Count == 0) list.Add(new Candidate(DefaultAddress, "default"));
            return list.DistinctBy(c => c.BaseUrl).ToList();
        }

        // A candidate counts only if /api/health says status "ok".
        internal static async Task<IReadOnlyList<Found>> FindAsync(
            IReadOnlyList<Candidate> candidates, Func<string, IPaperclipApi> apiFor, CancellationToken ct)
        {
            var found = new List<Found>();
            foreach (var c in candidates)
            {
                var res = await apiFor(c.BaseUrl).GetAsync("/api/health", ct).ConfigureAwait(false);
                if (!res.IsOk) continue;
                var health = PaperclipApi.ParseHealth(res.Body);
                if (health is { Status: "ok" })
                    found.Add(new Found(c.BaseUrl, c.InstanceName, health.DeploymentMode));
            }
            return found;
        }

        internal static string InstancesRoot(string home) => Path.Combine(home, ".paperclip", "instances");
    }
}
