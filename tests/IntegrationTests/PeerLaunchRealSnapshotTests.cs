using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// CB-357: a peer launch run through the server's *real* session source.
//
// Review found that the server's snapshot was never fed outside the tests:
// the CLI would start on the receiving desk, the check-in would never see it,
// and the sender would be told no_session after 30 s. Every unit test passed,
// because every unit test handed the server a fake snapshot. So this one keeps
// PeerLaunchServer.RealSeams().Snapshot — RemoteControlSessions.LocalSessions,
// a HeadlessSnapshot of the status directory — and fakes only the process
// start, which writes the status file the hook would write for a live process.
// If the real seam stops being wired, or stops seeing a new session, this
// fails with no_session.
//
// The status directory is the assembly's scratch ORBWEAVER_STATUS_ROOT
// (TestBootstrap). The "session" is a child process of its own, not this test
// host, so Superseded — which collapses files sharing a pid — cannot be
// decided by some hook test that recorded the host's pid. "Settings" because
// HeadlessSnapshot reads the per-CLI switches.
[Collection("Settings")]
public class PeerLaunchRealSnapshotTests
{
    [Fact]
    public async Task A_launch_checks_in_through_the_real_session_source()
    {
        OrbweaverSettings.ClaudeCodeEnabled = true;
        RemoteControlSessions.ForgetLocalSessionsForTests();

        using var key = RSA.Create(2048);
        using var sender = new CertificateRequest("CN=sender", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var ownPin = new string('b', 64);

        var cwd = Path.Combine(Path.GetTempPath(), "cb357-real-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        var sessionId = "cb357-" + Guid.NewGuid().ToString("N");
        using var alive = Process.Start(OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping", "-n 120 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false }
            : new ProcessStartInfo("sleep", "120") { UseShellExecute = false })!;
        var statusFile = Path.Combine(StatusDirectory.Path(), sessionId + ".txt");

        // What the hook does once the CLI is up: a status file for a live pid.
        void Hook()
        {
            Directory.CreateDirectory(StatusDirectory.Path());
            File.WriteAllText(statusFile, JsonSerializer.Serialize(new SessionStatus
            {
                State = "idle",
                Cwd = cwd,
                SessionPid = alive.Id,
            }));
        }

        var started = 0;
        var real = PeerLaunchServer.RealSeams();
        var server = new PeerLaunchServer(real with
        {
            Enabled = () => true,
            KnownPeer = m => m == "mbp" ? new PeerIdentity.Peer(PeerIdentity.PinOf(sender), "mbp") : null,
            OwnPin = () => ownPin,
            Launch = new RelayRemote.LaunchSeams(
                OperatingSystem.IsWindows(),
                _ => OperatingSystem.IsWindows() ? "C:\\c\\claude.exe" : "/bin/claude",
                Directory.Exists,
                () => "/bin/zsh",
                (_, _) => { started++; Hook(); return ("%1", null); },
                _ => { started++; Hook(); return 42; }),
            Accounts = new RelayRemote.AccountSeams(_ => [], _ => null),
            Log = _ => { },
        });

        try
        {
            var f = new LaunchFields(cwd, "claude", "", "", "", "",
                RelayCrypto.Base64Url(RandomNumberGenerator.GetBytes(16)),
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 120, "default");
            var signed = PeerIdentity.Sign(sender, LaunchGate.PeerMessage(ownPin, LaunchGate.Body(f)));
            var body = PeerLaunchWire.EncodeLaunch(new PeerLaunchWire.LaunchRequest(
                f.Cwd, f.Cli, "", "", f.Account, "", "", f.Nonce, f.ExpiresAt,
                RelayCrypto.Base64Url(signed.Sig), Convert.ToBase64String(signed.CertificateDer)));

            var result = await server.HandleAsync("mbp", body);

            Assert.Equal(1, started);
            Assert.Equal(new PeerLaunchResult(true, PeerLaunchCodes.Launched, sessionId, null), result);
        }
        finally
        {
            try { alive.Kill(); } catch (InvalidOperationException) { }
            File.Delete(statusFile);
            Directory.Delete(cwd);
            RemoteControlSessions.ForgetLocalSessionsForTests();
        }
    }
}
