using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// CB-278, end to end: a whole hello-ok driven through OpenClawGateway, through
// the 0600 credential file on disk, to the bearer a media fetch would present.
//
// The unit suite takes the same rules a case at a time (OpenClawDeviceTokenTests).
// This one takes the whole exchange, because the seam fails differently from
// the parse: a field read wrong is the unit suite's catch, and a hello-ok
// whose token sits somewhere the parse does not look, or a token that is
// parsed and then never reaches the file the fetch reads, is this one's.
//
// **The fixture is a real hello-ok, captured and then scrubbed.** It was taken
// from openclaw 2026.10.5 on 2026-10-23 over the app's own transport
// (OpenClawSocket.ConnectAsync and OpenClawGateway, with a recording socket
// teed in), as a paired device approved for `operator.read` with reply off,
// against a throwaway copy of that machine's settings. Every field is as the
// gateway sent it except these, and nothing was added:
//
//   auth.deviceToken, auth.recoveryScope   same-length placeholders (43 chars);
//                                          both are credentials
//   auth.issuedAtMs, snapshot.uptimeMs     invented
//   server.bootId, server.connId           placeholder uuids
//   pluginSurfaceUrls.canvas               placeholder url (it carried the
//                                          gateway's address and a key)
//   snapshot.presence, snapshot.health     emptied: other machines' hosts and
//                                          addresses, and the Discord bot's ids
//   snapshot.appliedConfigHash             zeroed, same length
//
// This repository is public and that gateway is private, which is the whole
// reason for the list. The placeholder token is the length the real one is, so
// a length assumption anywhere downstream would still be exercised. It is not,
// and never was, a token any gateway issued.
//
// The capture is what corrected the shape this test first used: the real
// `auth` block also carries `recoveryScope` and `recoveryMigrationAllowed`,
// which the reconstruction from the architect's notes did not have.
[Collection("Settings")]
public class OpenClawHelloOkDeviceTokenTests
{
    private const string Host = "gateway.example.test";

    private const string Placeholder = "PLACEHOLDER-device-token-not-issued-by-any0";

    private const string HelloOk = """
        {
          "type": "hello-ok",
          "protocol": 4,
          "server": {
            "version": "2026.10.5",
            "buildId": "2026.10.5-release-1b58cb9a1b4f-2026-10-18T17-23-40.943Z",
            "bootId": "00000000-0000-4000-8000-000000000001",
            "controlUiBuildSource": "bundled",
            "connId": "00000000-0000-4000-8000-000000000002"
          },
          "features": {
            "methods": [
              "health", "diagnostics.stability", "doctor.memory.status", "doctor.memory.dreamDiary",
              "doctor.memory.backfillDreamDiary", "doctor.memory.resetDreamDiary",
              "doctor.memory.resetGroundedShortTerm", "doctor.memory.repairDreamingArtifacts",
              "doctor.memory.dedupeDreamDiary", "logs.tail", "channels.status", "channels.start",
              "channels.stop", "channels.logout", "status", "usage.status", "usage.cost", "tts.status",
              "tts.providers", "tts.personas", "tts.enable", "tts.disable", "tts.convert",
              "tts.setProvider", "tts.setPersona", "config.get", "config.set", "config.apply",
              "config.patch", "config.schema", "config.schema.lookup", "exec.approvals.get",
              "exec.approvals.set", "exec.approvals.node.get", "exec.approvals.node.set",
              "exec.approval.get", "exec.approval.list", "exec.approval.request",
              "exec.approval.waitDecision", "exec.approval.resolve", "exec.approval.grants.list",
              "exec.approval.grants.revoke", "question.request", "question.waitAnswer", "question.resolve",
              "question.get", "question.list", "plugin.approval.list", "plugin.approval.request",
              "plugin.approval.waitDecision", "plugin.approval.resolve", "plugins.uiDescriptors",
              "plugins.sessionAction", "openclaw.chat", "openclaw.chat.history", "openclaw.changes.list",
              "openclaw.approval.list", "openclaw.setup.detect", "openclaw.setup.activate",
              "openclaw.setup.activate.start", "openclaw.setup.auth.start", "openclaw.setup.prepare.start",
              "wizard.start", "wizard.next", "wizard.cancel", "wizard.status", "talk.catalog",
              "talk.config", "talk.client.create", "talk.client.transcript", "talk.client.close",
              "talk.client.toolCall", "talk.client.steer", "talk.session.create",
              "talk.session.appendAudio", "talk.session.cancelOutput", "talk.session.acknowledgeMark",
              "talk.session.submitToolResult", "talk.session.steer", "talk.session.close", "talk.speak",
              "talk.mode", "commands.list", "models.list", "models.authStatus", "models.authLogout",
              "tools.catalog", "tools.effective", "tools.invoke", "mcp.app.view", "mcp.app.listTools",
              "mcp.app.listResources", "mcp.app.listResourceTemplates", "mcp.app.readResource",
              "mcp.app.callTool", "mcp.app.updateModelContext", "board.get", "board.update",
              "board.widget.put", "board.widget.grant", "board.widget.appView", "board.event", "audit.list",
              "audit.activity.list", "users.list", "users.self", "users.linkEmail", "users.setDisplayName",
              "users.setAvatar", "users.setRole", "users.listAuthLinks", "users.listModelAccounts",
              "users.selectModelAccount", "users.linkAuthProfile", "users.unlinkAuthProfile",
              "users.authConnect.start", "users.authConnect.answer", "users.authConnect.status",
              "users.authConnect.cancel", "users.authConnect.catalog", "taskSuggestions.list",
              "taskSuggestions.create", "taskSuggestions.accept", "taskSuggestions.dismiss",
              "environments.list", "environments.status", "worktrees.list", "worktrees.branches",
              "fs.listDir", "worktrees.create", "worktrees.remove", "worktrees.restore", "worktrees.gc",
              "agents.list", "agents.create", "agents.update", "agents.delete", "agents.files.list",
              "agents.files.get", "agents.files.set", "sessions.files.list", "sessions.files.get",
              "sessions.files.set", "sessions.files.reveal", "artifacts.list", "artifacts.get",
              "artifacts.download", "skills.status", "skills.library.list", "skills.library.read",
              "skills.library.save", "skills.library.mutate", "skills.library.activate",
              "skills.library.import", "skills.library.upload", "skills.search", "skills.detail",
              "skills.securityVerdicts", "skills.skillCard", "skills.bins", "skills.upload.begin",
              "skills.upload.chunk", "skills.upload.commit", "skills.install", "skills.update",
              "skills.curator.status", "skills.curator.pin", "skills.curator.unpin",
              "skills.curator.restore", "skills.proposals.list", "skills.proposals.inspect",
              "skills.proposals.historyStatus", "skills.proposals.historyScan", "skills.proposals.create",
              "skills.proposals.update", "skills.proposals.revise", "skills.proposals.requestRevision",
              "skills.proposals.apply", "skills.proposals.reject", "skills.proposals.quarantine",
              "update.status", "update.run", "voicewake.get", "voicewake.set", "secrets.reload",
              "secrets.resolve", "voicewake.routing.get", "sessions.list", "sessions.subscribe",
              "sessions.messages.subscribe", "sessions.messages.unsubscribe", "sessions.viewers.set",
              "sessions.preview", "sessions.describe", "sessions.branches.list", "sessions.branches.switch",
              "sessions.rewind", "sessions.fork", "sessions.create", "sessions.recover", "sessions.send",
              "sessions.abort", "sessions.patch", "sessions.goal.update", "sessions.goal.clear",
              "sessions.pluginPatch", "sessions.cleanup", "sessions.reset", "sessions.delete",
              "sessions.compact", "sessions.groups.list", "sessions.groups.defaults", "sessions.groups.put",
              "sessions.groups.rename", "sessions.groups.update", "sessions.groups.delete",
              "last-heartbeat", "set-heartbeats", "wake", "node.pair.list", "node.pair.approve",
              "node.pair.reject", "node.pair.remove", "device.pair.list", "device.pair.approve",
              "device.pair.reject", "device.pair.remove", "device.pair.rename", "device.token.rotate",
              "device.token.revoke", "node.rename", "node.list", "node.describe",
              "node.pluginSurface.refresh", "node.pluginTools.update", "node.skills.update",
              "node.pending.drain", "node.pending.enqueue", "node.invoke", "node.pending.pull",
              "node.pending.ack", "node.invoke.progress", "node.invoke.result", "node.event", "cron.get",
              "cron.list", "cron.status", "cron.scratch.get", "cron.scratch.set", "cron.add", "cron.update",
              "cron.remove", "cron.run", "cron.runs", "gateway.identity.get", "gateway.restart.preflight",
              "gateway.restart.request", "system-presence", "system-event", "message.action",
              "conversations.send", "conversations.turn", "conversations.turn.cancel", "send", "agent",
              "agent.identity.get", "agent.wait", "chat.history", "chat.startup", "chat.metadata",
              "chat.message.get", "chat.abort", "chat.send", "terminal.open", "terminal.input",
              "terminal.resize", "terminal.close", "channels.pairing.list", "channels.pairing.approve",
              "channels.pairing.dismiss", "attach.grant", "attach.revoke", "push.web.preferences.get",
              "push.web.preferences.set", "terminal.attach", "terminal.list", "controlUi.githubPreview",
              "system.info", "agents.workspace.list", "agents.workspace.get", "tts.speak", "plugins.list",
              "plugins.search", "plugins.install", "plugins.setEnabled", "plugins.uninstall",
              "plugins.refresh", "controlUi.sessionPullRequests.subscribe", "controlUi.sessionPreview",
              "gateway.suspend.prepare", "gateway.suspend.status", "gateway.suspend.resume",
              "chat.toolTitles", "sessions.diff", "openclaw.setup.verify", "environments.create",
              "environments.destroy", "sessions.catalog.list", "sessions.catalog.read", "terminal.upload",
              "sessions.catalog.continue", "sessions.catalog.archive", "approval.get", "approval.resolve",
              "sessions.search", "sessions.dispatch", "sessions.reclaim", "models.probe",
              "migrations.memory.plan", "migrations.memory.apply", "ui.command", "approval.history",
              "plugin.surface.refresh", "conversations.list", "session.discussion.info",
              "session.discussion.open", "board.prompt.authorize", "board.data.read", "board.action",
              "sessions.observer.visibility", "session.visibility.set", "session.members.list",
              "session.members.add", "session.members.remove", "session.suggestions.add",
              "session.suggestions.list", "session.suggestions.resolve", "session.reactions.set",
              "session.reactions.list", "session.typing", "sessions.companion.ask",
              "sessions.companion.state", "sessions.companion.reset", "memory.search",
              "skills.proposals.events.list", "skills.proposals.evaluate", "hooks.status",
              "audit.run.inspect", "sessions.patchMany", "update.hold", "sessions.catalog.startTerminal",
              "worker.desktop.observe", "projects.list", "projects.register", "projects.remove",
              "worker.desktop.launch", "secrets.store.list", "secrets.store.set", "secrets.store.delete",
              "users.prefs.get", "users.prefs.set", "projects.add", "projects.searchRemote",
              "desktop.observe", "desktop.launch", "device.scopes.requestUpgrade",
              "device.scopes.waitUpgrade", "portal.list", "portal.open", "portal.close", "sessions.move",
              "sessions.assignOwner", "progressCard.get", "progressCard.put", "tools.github.status",
              "tools.github.configure", "tools.github.authorize.start", "tools.github.authorize.poll",
              "tools.github.authorize.cancel", "sessions.github.publish", "diagnostics.lanes",
              "session.members.listEvidence", "plugins.inspect", "users.github.status",
              "users.github.authorize.start", "users.github.authorize.poll",
              "users.github.authorize.cancel", "users.github.disconnect", "sessions.github.options",
              "sessions.github.status", "sessions.github.confirm", "sessions.title.prepare",
              "users.mentionable", "mentions.list", "mentions.dismiss", "transcripts.list",
              "transcripts.get", "models.authOrderSet", "canvas.document.view", "plugins.controlUi.list",
              "plugins.controlUi.reload", "plugins.controlUi.report", "plugins.controlUi.status",
              "update.runs.get", "update.runs.list", "gateway.suspend.handoff", "transcripts.export",
              "transcripts.status", "update.report", "skills.workshop.read", "session.publicShare.set",
              "claws.monitors", "plugins.catalog.browse", "plugins.catalog.categories",
              "plugins.catalog.get", "environments.prepare", "models.authRefresh", "models.authLogin",
              "models.authSetApiKey", "sessions.storage.status", "sessions.storage.run", "plugins.reload",
              "claws.packages.remove", "claws.removalJournal", "canvas.document.preview", "computer.status",
              "computer.invoke", "sessions.activitySummary.ensure", "controlUi.sessionPullRequests.checks",
              "diagnostics.cpuProfile", "talk.voice.get", "talk.voice.set", "talk.voice.complete",
              "plugins.credentials.inspect", "plugins.skills.read", "diagnostics.heapProfile",
              "desktop.release", "environments.session.status", "environments.session.create",
              "environments.session.destroy", "environments.session.exec", "sessions.setInvolvement",
              "transcripts.summarize", "controlUi.linkPreview", "themes.list", "themes.get", "themes.set",
              "themes.import", "controlUi.githubDetail", "progressCard.refresh", "webSearch.status",
              "webSearch.test", "sessions.providerReview.continue", "users.linkChannelIdentity",
              "users.unlinkChannelIdentity", "users.listChannelIdentities", "users.personalFile.get",
              "users.personalFile.set", "portal.session.list", "portal.session.open",
              "portal.session.close", "cron.history", "presence.activity", "presence.query", "users.merge",
              "gateway.stop.request", "diagnostics.heapSnapshot", "sessions.catalog.import",
              "backup.status", "storage.locations.list", "storage.locations.probe", "mcp.app.onboard",
              "mcp.app.discover", "mcp.app.launch", "mcp.app.settings", "mcp.app.mention",
              "mcp.app.formResource", "mcp.app.modelContext", "mcp.app.removeModelContext",
              "mcp.app.writeResource", "mcp.app.subscribeResource", "mcp.app.unsubscribeResource",
              "mcp.app.openFile", "memory.get", "memory.status", "sessions.files.assets",
              "worktrees.recoverRemoval", "worktrees.retireSnapshot", "sessions.processes.list",
              "sessions.processes.stop", "catalog.browse", "catalog.searchKeywords", "skills.workshop.list",
              "skills.workshop.changes", "skills.workshop.archive", "skills.workshop.restore",
              "browser.request", "browser.dashboard.request", "geolocation.lookup", "github.preview",
              "github.detail", "github.image", "memory.sessionBackfill.preview",
              "memory.sessionBackfill.apply", "memory.sessionBackfill.rollback", "codex.accountUsage",
              "web.login.start", "web.login.wait"
            ],
            "events": [
              "connect.challenge", "agent", "chat", "chat.metadata.changed", "models.snapshot",
              "ui.command", "session.approval", "session.message", "session.narration", "session.observer",
              "session.operation", "session.sharing", "session.sharing.evidence", "session.suggestion",
              "session.reaction", "session.typing", "session.tool", "sessions.changed",
              "controlUi.sessionPullRequests.changed", "plugins.controlUi.changed", "presence", "tick",
              "talk.mode", "talk.event", "talk.voice.change", "shutdown", "gateway.suspension", "health",
              "heartbeat", "cron", "task.suggestion", "node.pair.requested", "node.pair.resolved",
              "node.presence", "node.hostStats", "node.runnerInventory.changed", "node.invoke.cancel",
              "node.invoke.input", "node.invoke.request", "device.pair.changed", "device.pair.requested",
              "device.pair.resolved", "device.pair.setup.completed", "device.pair.setup.deliveryUncertain",
              "users.prefs.changed", "skills.changed", "plugins.changed", "plugins.install.progress",
              "voicewake.changed", "voicewake.routing.changed", "exec.approval.requested",
              "exec.approval.resolved", "question.requested", "question.resolved",
              "plugin.approval.requested", "plugin.approval.resolved", "openclaw.approval.requested",
              "openclaw.approval.resolved", "terminal.data", "terminal.exit", "update.available",
              "update.run.changed", "portal.changed", "progressCard.changed", "mentions.changed"
            ],
            "capabilities": [
              "system.run.execution-context.v1", "board-widget-put-canvas-doc",
              "chat-send-routing-contract", "control-ui-browser-focus", "gateway-restart-target-safe-v1",
              "local-state-owner-routing-v1", "channels-pairing-list-owner-v1",
              "channels-pairing-approve-owner-v1", "exec-approvals-get-owner-v1",
              "exec-approvals-set-owner-v1", "worktrees-remove-owner-v1", "worktrees-restore-owner-v1",
              "worktrees-gc-owner-v1", "worktrees-recover-removal-owner-v1",
              "worktrees-retire-snapshot-owner-v1", "model-catalog-snapshot",
              "node-worker-bundle-retention-v1", "node-worker-bundle-status-v1",
              "node-worker-captured-exec-policy", "node-worker-workspace-quiescence-v1",
              "node-worker-environment-session-v1", "node-worker-host-diagnostics-v1",
              "node-worker-idle-retention-v1", "node-worker-launch-tool-names-v1",
              "node-worker-native-inference-v1", "node-worker-portal-stream-v1",
              "node-worker-prompt-context-v1", "node-worker-status-wait-v1", "profile-binding-v1",
              "published-model-catalog", "progress-card-agent-scope-v1", "session-scoped-chat-metadata",
              "session-scoped-model-catalog", "session-unread-ack-contract", "session-goal-start-v1",
              "session-settings-contract", "session-settings-cas-v1", "sender-restricted-hidden-helpers-v1",
              "openclaw-chat-wizard-cancel", "openclaw-setup-model-ref", "taskSuggestions.acceptModes"
            ]
          },
          "snapshot": {
            "suspension": {
              "phase": "accepting"
            },
            "presence": [],
            "health": {},
            "stateVersion": {
              "presence": 55,
              "health": 1522
            },
            "uptimeMs": 1000,
            "appliedConfigHash": "0000000000000000000000000000000000000000000000000000000000",
            "sessionDefaults": {
              "defaultAgentId": "main",
              "modelConfigured": true,
              "ownership": "explicit",
              "selectionRequired": false,
              "mainKey": "main",
              "mainSessionKey": "agent:main:main",
              "scope": "per-sender"
            },
            "updateAvailable": {
              "currentVersion": "2026.10.5",
              "latestVersion": "2026.10.6",
              "channel": "latest"
            },
            "updateSchedule": {
              "channel": "stable",
              "autoEnabled": false,
              "install": {
                "kind": "package"
              },
              "target": {
                "kind": "package",
                "version": "2026.10.6"
              }
            }
          },
          "controlUiWidgetKinds": [
            {
              "pluginId": "session",
              "kind": "session:report",
              "label": "Report"
            },
            {
              "pluginId": "session",
              "kind": "session:progress",
              "label": "Session progress"
            },
            {
              "pluginId": "session",
              "kind": "session:website",
              "label": "Website"
            }
          ],
          "controlUiLinkReaders": [
            {
              "pluginId": "github",
              "id": "github",
              "label": "GitHub",
              "icon": "github",
              "linkReader": {
                "hosts": ["github.com"],
                "pathPattern": "^/[^/]+/[^/]+/(?:issues/[1-9][0-9]{0,9}|pull/[1-9][0-9]{0,9}(?:/files)?)/?$",
                "detailMethod": "github.detail",
                "previewMethod": "github.preview",
                "imageMethod": "github.image"
              }
            },
            {
              "pluginId": "github",
              "id": "github-commit",
              "label": "GitHub",
              "icon": "github",
              "linkReader": {
                "hosts": ["github.com"],
                "pathPattern": "^/[^/]+/[^/]+/commit/[a-fA-F0-9]{7,40}/?$",
                "detailMethod": "github.detail",
                "imageMethod": "github.image"
              }
            }
          ],
          "pluginSurfaceUrls": {
            "canvas": "https://gateway.example.test:18789/__openclaw__/canvas/"
          },
          "auth": {
            "method": "token",
            "role": "operator",
            "scopes": ["operator.read"],
            "recoveryScope": "PLACEHOLDER-recovery-scope-not-issued-by-a0",
            "recoveryMigrationAllowed": true,
            "deviceToken": "PLACEHOLDER-device-token-not-issued-by-any0",
            "issuedAtMs": 1700000000000
          },
          "policy": {
            "maxPayload": 26214400,
            "maxBufferedBytes": 52428800,
            "tickIntervalMs": 30000,
            "attachments": {
              "maxBytes": 19464192,
              "maxImageBytes": 6291456
            },
            "allowedSessionVisibilities": ["shared", "read-only", "suggest", "draft"],
            "hasMultipleSessionSharingIdentities": false
          }
        }
        """;

    [Fact]
    public async Task AHelloOkDeviceTokenReachesTheFileAndIsWhatAMediaFetchPresents()
    {
        Assert.Equal(43, Placeholder.Length);

        var previous = Environment.GetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR");
        var scratch = Path.Combine(Path.GetTempPath(), "cb278-hello-" + Guid.NewGuid());
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", scratch);
        OpenClawIdentity.ResetForTests();

        try
        {
            OpenClawIdentity.SetGatewayTokenFor(Host, "shared-placeholder");

            // Before any connect: no device token, so the fetch falls back to
            // the shared secret. That is today's behaviour and the control for
            // the assertion after the connect.
            Assert.Equal(OpenClawMediaCredential.Kind.Shared, OpenClawMediaCredential.Pick(
                OpenClawIdentity.TokenFor(Host), OpenClawIdentity.GatewayTokenFor(Host)).Kind);

            var hello = JsonDocument.Parse(HelloOk).RootElement;

            // The parse rule on its own first, against the real shape, so a
            // failure below says "the seam" rather than "the field".
            Assert.Equal(Placeholder, OpenClawGateway.DeviceTokenIn(hello));
            var socket = new FakeGatewaySocket();
            socket.PushEvent("connect.challenge", new { nonce = "nonce-placeholder" });
            socket.OnRequest = r => FakeGatewaySocket.Ok(r.Id, hello);

            using (var gateway = new OpenClawGateway(Host, 18789, "shared-placeholder",
                (_, _, _, _) => Task.FromResult(new OpenClawSocket.Connection(socket, Stream.Null, "fp")),
                Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan))
            {
                var result = await gateway.ConnectAsync(null, CancellationToken.None);
                Assert.Equal(OpenClawGateway.Outcome.Connected, result.Outcome);
                Assert.Equal(new[] { "operator.read" }, gateway.GrantedScopes);
                Assert.Equal("2026.10.5", gateway.ServerVersion);
            }

            // On disk, read back as JSON rather than through the cache: the
            // file is what a restarted app reads, so it has to hold both keys.
            var stored = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(Path.Combine(scratch, "openclaw-devices.json")))!;
            Assert.Equal(Placeholder, stored[Host]);
            Assert.Equal("shared-placeholder", stored["gateway:" + Host]);

            // And through a fresh read of that file, the pick a media fetch
            // makes is now the device token.
            OpenClawIdentity.ResetForTests();
            var (token, kind) = OpenClawMediaCredential.Pick(
                OpenClawIdentity.TokenFor(Host), OpenClawIdentity.GatewayTokenFor(Host));
            Assert.Equal(OpenClawMediaCredential.Kind.Device, kind);
            Assert.Equal(Placeholder, token);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", previous);
            OpenClawIdentity.ResetForTests();
            try { Directory.Delete(scratch, recursive: true); } catch { }
        }
    }
}
