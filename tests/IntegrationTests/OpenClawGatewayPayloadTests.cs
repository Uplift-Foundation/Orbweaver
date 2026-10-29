using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// The gateway's two payloads, end to end, against fixtures derived from what a
// real gateway actually returned.
//
// Here rather than only in tests/UnitTests because this is the seam CLAUDE.md
// says to cover twice: the unit suites check the parsing rules a case at a time,
// and these check the whole exchange — a real sessions.list walked into rooms,
// addresses and orbs, and a real chat.history walked into attributed turns. The
// two fail differently. A unit test catches a field read wrong; only this
// catches a payload whose shape is not what the rules assume.
//
// **Every value in these fixtures is invented.** The structure is real —
// derived field by field from captured payloads, including the fields that were
// observed absent — and the ids, names, channel and sentences are not. This
// repository is public and the gateway is a private Discord server with real
// people in it, so what these are for is the shape, and the shape is all they
// carry.
//
// Serialised on the settings collection because Parse reads the recency window
// and the cluster modes out of the process-wide settings, and ChatFor reads
// whether OpenClaw is on at all.
[Collection("Settings")]
public class OpenClawGatewayPayloadTests
{
    // CB-272: a whole chat.history record for a cron-delivered clip — role,
    // provider, the automation carrier, timestamp and content blocks — walked
    // into one video turn. Invented values on the real structure.
    [Fact]
    public void AHistoryPageWithAClipMediaLineWalksIntoAVideoTurn()
    {
        const string path = "/home/agent/outputs/astra/clip_123.mp4";
        var messages = JsonDocument.Parse("""
        [{"role":"assistant","provider":"claude-cli-example","timestamp":1700000900000,
          "openclawAutomation":{"kind":"cron","jobId":"job-clip0001"},
          "content":[{"type":"text","text":"a short one for you\nMEDIA:/home/agent/outputs/astra/clip_123.mp4"}]}]
        """).RootElement;

        var turn = Assert.Single(OpenClawSessions.TurnsFromHistory(messages, "agent:main:cron:job-clip0001"));

        Assert.Equal(new OpenClawMediaSource(path, "agent:main:cron:job-clip0001").Route, turn.ImageUrl);
        Assert.Equal(path, turn.ImageSourcePath);
        Assert.Equal(MediaConfidence.High, turn.Confidence);
        Assert.Null(turn.ImageBytes);
        Assert.True(OpenClawVideo.IsVideoPath(turn.ImageSourcePath));
        Assert.NotNull(turn.Automation);
    }

    // Both kinds on one page: each carries a route, and only the clip is a
    // video, so the picture still goes to the bitmap path and the clip to the
    // tile.
    [Fact]
    public void AHistoryPageWithAPictureAndAClipKeepsTheKindsApart()
    {
        var messages = JsonDocument.Parse("""
        [{"role":"assistant","content":[{"type":"text","text":"one\nMEDIA:/home/agent/outputs/pic_1.png"}]},
         {"role":"assistant","content":[{"type":"text","text":"two\nMEDIA:/home/agent/outputs/clip_2.mp4"}]}]
        """).RootElement;

        var turns = OpenClawSessions.TurnsFromHistory(messages, "agent:main:main");

        var picture = turns.Single(t => t.Text.StartsWith("one"));
        var clip = turns.Single(t => t.Text.StartsWith("two"));
        Assert.False(OpenClawVideo.IsVideoPath(picture.ImageSourcePath));
        Assert.True(OpenClawVideo.IsVideoPath(clip.ImageSourcePath));
        Assert.NotNull(picture.ImageUrl);
        Assert.NotNull(clip.ImageUrl);
    }

    // CB-99: history is an external payload, so prove the full record carries
    // the two provenance fields into the delivery predicate. A missing field
    // preserves compatibility, while a present mismatch must not initiate a
    // media request.
    [Fact]
    public void HistoryDeliveryProvenanceIsAppliedWithoutBreakingLegacyRows()
    {
        var messages = JsonDocument.Parse("""
        [
          {"model":"delivery-mirror","content":[{"type":"text","text":"legacy.png"}]},
          {"role":"assistant","provider":"another-provider","model":"delivery-mirror",
           "content":[{"type":"text","text":"wrong-provider.png"}]},
          {"role":"user","provider":"openclaw","model":"delivery-mirror",
           "content":[{"type":"text","text":"wrong-role.png"}]}
        ]
        """).RootElement;

        var turns = OpenClawSessions.TurnsFromHistory(messages, "agent:main:main");

        Assert.NotNull(turns.Single(t => t.Text == "legacy.png").ImageUrl);
        Assert.Null(turns.Single(t => t.Text == "wrong-provider.png").ImageUrl);
        Assert.Null(turns.Single(t => t.Text == "wrong-role.png").ImageUrl);
    }

    // Nine agents standing in one channel, which is what the room this was
    // diagnosed against actually looked like. Their activity times span five
    // months, so the recency window has something real to cut.
    //
    // The *shape* of that spread is the room's — two members inside the hour,
    // three about a day back, then a week, three months and five — and the
    // instants are nobody's. The captured epochs were shifted onto an unrelated
    // base rather than kept: a millisecond timestamp names no person, channel or
    // credential, but it is still a record of when a private gateway was doing
    // something, and this repository is public. Nothing in the test depends on
    // which day they fall on, only on the distances between them.
    //
    // Two details worth keeping rather than tidying: every member's
    // deliveryContext names the same channel and the same recipient and differs
    // only in accountId, which is the fact the whole fix rests on; and
    // lastActivityAt is genuinely absent on four of the nine, with updatedAt
    // left to answer for them.
    private const string SessionsList = """
        {
          "ts": 1763640000000,
          "count": 9,
          "totalCount": 9,
          "hasMore": false,
          "sessions": [
            {
              "key": "agent:quill:discord:channel:900000000000000001",
              "kind": "group",
              "displayName": "discord:900000000000000000#lobby",
              "channel": "discord",
              "groupChannel": "#lobby",
              "space": "900000000000000000",
              "chatType": "channel",
              "origin": {
                "label": "#lobby channel id:900000000000000001",
                "provider": "discord",
                "surface": "webchat",
                "chatType": "direct",
                "from": "discord:channel:900000000000000001",
                "to": "channel:900000000000000001",
                "accountId": "quillbot"
              },
              "updatedAt": 1763639640000,
              "archived": false,
              "lastActivityAt": 1763575200000,
              "sessionId": "00000000-0000-4000-8000-000000000000",
              "status": "done",
              "deliveryContext": {
                "channel": "discord",
                "to": "channel:900000000000000001",
                "accountId": "quillbot"
              }
            },
            {
              "key": "agent:aster:discord:channel:900000000000000001",
              "kind": "group",
              "displayName": "discord:900000000000000000#lobby",
              "channel": "discord",
              "groupChannel": "#lobby",
              "space": "900000000000000000",
              "chatType": "channel",
              "origin": {
                "label": "#lobby channel id:900000000000000001",
                "provider": "discord",
                "surface": "discord",
                "chatType": "channel",
                "from": "discord:channel:900000000000000001",
                "to": "channel:900000000000000001",
                "accountId": "default"
              },
              "updatedAt": 1763639580000,
              "archived": false,
              "lastActivityAt": 1762615200000,
              "sessionId": "00000000-0000-4000-8000-000000000001",
              "status": "done",
              "deliveryContext": {
                "channel": "discord",
                "to": "channel:900000000000000001",
                "accountId": "default"
              }
            },
            {
              "key": "agent:thorn:discord:channel:900000000000000001",
              "kind": "group",
              "displayName": "discord:900000000000000000#lobby",
              "channel": "discord",
              "groupChannel": "#lobby",
              "space": "900000000000000000",
              "chatType": "channel",
              "origin": {
                "label": "#lobby channel id:900000000000000001",
                "provider": "discord",
                "surface": "discord",
                "chatType": "channel",
                "from": "discord:channel:900000000000000001",
                "to": "channel:900000000000000001",
                "accountId": "thornbot"
              },
              "updatedAt": 1763517600000,
              "archived": false,
              "lastActivityAt": null,
              "sessionId": "00000000-0000-4000-8000-000000000002",
              "status": "done",
              "deliveryContext": {
                "channel": "discord",
                "to": "channel:900000000000000001",
                "accountId": "thornbot"
              }
            },
            {
              "key": "agent:bram:discord:channel:900000000000000001",
              "kind": "group",
              "displayName": "discord:900000000000000000#lobby",
              "channel": "discord",
              "groupChannel": "#lobby",
              "space": "900000000000000000",
              "chatType": "channel",
              "origin": {
                "label": "#lobby channel id:900000000000000001",
                "provider": "discord",
                "surface": "discord",
                "chatType": "channel",
                "from": "discord:channel:900000000000000001",
                "to": "channel:900000000000000001",
                "accountId": "brambot"
              },
              "updatedAt": 1763517540000,
              "archived": false,
              "lastActivityAt": 1763517300000,
              "sessionId": "00000000-0000-4000-8000-000000000003",
              "status": "done",
              "deliveryContext": {
                "channel": "discord",
                "to": "channel:900000000000000001",
                "accountId": "brambot"
              }
            },
            {
              "key": "agent:nib:discord:channel:900000000000000001",
              "kind": "group",
              "displayName": "discord:900000000000000000#lobby",
              "channel": "discord",
              "groupChannel": "#lobby",
              "space": "900000000000000000",
              "chatType": "channel",
              "origin": {
                "label": "#lobby channel id:900000000000000001",
                "provider": "discord",
                "surface": "discord",
                "chatType": "channel",
                "from": "discord:channel:900000000000000001",
                "to": "channel:900000000000000001",
                "accountId": "nibbot"
              },
              "updatedAt": 1763517480000,
              "archived": false,
              "lastActivityAt": 1763517240000,
              "sessionId": "00000000-0000-4000-8000-000000000004",
              "status": "done",
              "deliveryContext": {
                "channel": "discord",
                "to": "channel:900000000000000001",
                "accountId": "nibbot"
              }
            },
            {
              "key": "agent:vale:discord:channel:900000000000000001",
              "kind": "group",
              "displayName": "discord:900000000000000000#lobby",
              "channel": "discord",
              "groupChannel": "#lobby",
              "space": "900000000000000000",
              "chatType": "channel",
              "origin": {
                "label": "#lobby channel id:900000000000000001",
                "provider": "discord",
                "surface": "discord",
                "chatType": "channel",
                "from": "discord:channel:900000000000000001",
                "to": "channel:900000000000000001",
                "accountId": "valebot"
              },
              "updatedAt": 1763517420000,
              "archived": false,
              "lastActivityAt": 1763517180000,
              "sessionId": "00000000-0000-4000-8000-000000000005",
              "status": "done",
              "deliveryContext": {
                "channel": "discord",
                "to": "channel:900000000000000001",
                "accountId": "valebot"
              }
            },
            {
              "key": "agent:wren:discord:channel:900000000000000001",
              "kind": "group",
              "displayName": "discord:900000000000000000#lobby",
              "channel": "discord",
              "groupChannel": "#lobby",
              "space": "900000000000000000",
              "chatType": "channel",
              "origin": {
                "label": "#lobby channel id:900000000000000001",
                "provider": "discord",
                "surface": "discord",
                "chatType": "channel",
                "from": "discord:channel:900000000000000001",
                "to": "channel:900000000000000001",
                "accountId": "wrenbot"
              },
              "updatedAt": 1763121600000,
              "archived": false,
              "lastActivityAt": null,
              "sessionId": "00000000-0000-4000-8000-000000000006",
              "status": "done",
              "deliveryContext": {
                "channel": "discord",
                "to": "channel:900000000000000001",
                "accountId": "wrenbot"
              }
            },
            {
              "key": "agent:fen:discord:channel:900000000000000001",
              "kind": "group",
              "displayName": "discord:900000000000000000#lobby",
              "channel": "discord",
              "groupChannel": "#lobby",
              "space": "900000000000000000",
              "chatType": "channel",
              "origin": {
                "label": "#lobby channel id:900000000000000001",
                "provider": "discord",
                "surface": "discord",
                "chatType": "channel",
                "from": "discord:channel:900000000000000001",
                "to": "channel:900000000000000001",
                "accountId": "fenbot"
              },
              "updatedAt": 1754989200000,
              "archived": false,
              "lastActivityAt": null,
              "sessionId": "00000000-0000-4000-8000-000000000007",
              "status": "done",
              "deliveryContext": {
                "channel": "discord",
                "to": "channel:900000000000000001",
                "accountId": "fenbot"
              }
            },
            {
              "key": "agent:moss:discord:channel:900000000000000001",
              "kind": "group",
              "displayName": "discord:900000000000000000#lobby",
              "channel": "discord",
              "groupChannel": "#lobby",
              "space": "900000000000000000",
              "chatType": "channel",
              "origin": {
                "label": "#lobby channel id:900000000000000001",
                "provider": "discord",
                "surface": "discord",
                "chatType": "channel",
                "from": "discord:channel:900000000000000001",
                "to": "channel:900000000000000001",
                "accountId": "valebot"
              },
              "updatedAt": 1750420800000,
              "archived": false,
              "lastActivityAt": null,
              "sessionId": "00000000-0000-4000-8000-000000000008",
              "status": null,
              "deliveryContext": {
                "channel": "discord",
                "to": "channel:900000000000000001",
                "accountId": "valebot"
              }
            }
          ]
        }
        """;

    private const string HistoryPage = """
        {
          "sessionKey": "agent:quill:discord:channel:900000000000000001",
          "sessionId": "00000000-0000-4000-8000-000000000000",
          "messages": [
            {
              "role": "user",
              "content": "what did the overnight run say?",
              "timestamp": 1787880000000,
              "__openclaw": {
                "senderIsOwner": true,
                "senderId": "900000000000000101",
                "senderName": "quillfeather",
                "senderUsername": "quillfeather",
                "id": "00000000-0000-4000-8000-0000000000a1",
                "recordTimestampMs": 1787880000200,
                "seq": 1
              }
            },
            {
              "role": "assistant",
              "content": [
                {
                  "type": "text",
                  "text": "Green on both legs."
                }
              ],
              "stopReason": "stop",
              "api": "cli",
              "provider": "claude-cli",
              "model": "claude-sonnet-4-6",
              "timestamp": 1787880001000,
              "idempotencyKey": "cli-assistant:00000000-0000-4000-8000-0000000000a2",
              "__openclaw": {
                "id": "00000000-0000-4000-8000-0000000000a3",
                "idempotencyKey": "cli-assistant:00000000-0000-4000-8000-0000000000a2",
                "recordTimestampMs": 1787880001100,
                "seq": 2
              }
            },
            {
              "role": "user",
              "content": "Nodes are loaded.",
              "timestamp": 1787880002000,
              "__openclaw": {
                "senderIsOwner": false,
                "senderId": "900000000000000102",
                "senderName": "Thistle",
                "senderUsername": "Thistle",
                "id": "00000000-0000-4000-8000-0000000000a4",
                "recordTimestampMs": 1787880002100,
                "seq": 3
              }
            },
            {
              "role": "user",
              "content": "anyone free to look at the build?",
              "timestamp": 1787880003000,
              "idempotencyKey": "00000000-0000-4000-8000-0000000000a5:user",
              "__openclaw": {
                "id": "00000000-0000-4000-8000-0000000000a6",
                "idempotencyKey": "00000000-0000-4000-8000-0000000000a5:user",
                "recordTimestampMs": 1787880003100,
                "seq": 4
              }
            },
            {
              "role": "user",
              "content": "**(via Claude Buddy)** anyone free to look at the build?",
              "timestamp": 1787880003050,
              "__openclaw": {
                "senderIsOwner": false,
                "senderId": "900000000000000103",
                "senderName": "Quillbot",
                "senderUsername": "Quillbot",
                "id": "00000000-0000-4000-8000-0000000000a7",
                "recordTimestampMs": 1787880003150,
                "seq": 5
              }
            },
            {
              "role": "user",
              "content": "[Inter-session message] sourceSession=agent:thorn:discord:direct:900000000000000104 sourceChannel=discord sourceTool=sessions_send isUser=false can you take the release notes?",
              "timestamp": 1787880004000,
              "__openclaw": {
                "id": "00000000-0000-4000-8000-0000000000a8",
                "recordTimestampMs": 1787880004100,
                "seq": 6
              }
            }
          ],
          "offset": 0,
          "hasMore": false,
          "totalMessages": 6
        }
        """;

    // Two members' views of one stretch of the same channel, for CB-32 and
    // CB-33. Same provenance as the page above — the structure is real and every
    // value in it is invented — and the same five minutes seen twice, which is
    // the only way the merge's questions can be asked of a payload at all.
    //
    // What is in it: Quill answers a question; the operator says "ok" and then
    // says "ok" again a minute later; and another person in the channel quotes
    // Quill's answer and adds a question of their own. Quill's page carries its
    // own reply as an assistant turn, Aster's carries the same words as the
    // relayed user turn every other member in the channel receives — which is
    // the asymmetry the whole merge exists for.
    private const string QuillPage = """
        {
          "sessionKey": "agent:quill:discord:channel:900000000000000001",
          "sessionId": "00000000-0000-4000-8000-000000000000",
          "messages": [
            {
              "role": "assistant",
              "content": [{ "type": "text", "text": "Build is green on both legs." }],
              "stopReason": "stop",
              "api": "cli",
              "provider": "claude-cli",
              "model": "claude-sonnet-4-6",
              "timestamp": 1787880000000,
              "idempotencyKey": "cli-assistant:00000000-0000-4000-8000-0000000000b1",
              "__openclaw": {
                "id": "00000000-0000-4000-8000-0000000000b2",
                "recordTimestampMs": 1787880000100,
                "seq": 1
              }
            },
            {
              "role": "user",
              "content": "ok",
              "timestamp": 1787880060000,
              "__openclaw": {
                "senderIsOwner": true,
                "senderId": "900000000000000101",
                "senderName": "quillfeather",
                "id": "00000000-0000-4000-8000-0000000000b3",
                "recordTimestampMs": 1787880060100,
                "seq": 2
              }
            },
            {
              "role": "user",
              "content": "ok",
              "timestamp": 1787880120000,
              "__openclaw": {
                "senderIsOwner": true,
                "senderId": "900000000000000101",
                "senderName": "quillfeather",
                "id": "00000000-0000-4000-8000-0000000000b4",
                "recordTimestampMs": 1787880120100,
                "seq": 3
              }
            },
            {
              "role": "user",
              "content": "Build is green on both legs. Are we sure about the arm64 one?",
              "timestamp": 1787880180000,
              "__openclaw": {
                "senderIsOwner": false,
                "senderId": "900000000000000102",
                "senderName": "Thistle",
                "senderUsername": "Thistle",
                "id": "00000000-0000-4000-8000-0000000000b5",
                "recordTimestampMs": 1787880180100,
                "seq": 4
              }
            }
          ],
          "offset": 0,
          "hasMore": false,
          "totalMessages": 4
        }
        """;

    private const string AsterPage = """
        {
          "sessionKey": "agent:aster:discord:channel:900000000000000001",
          "sessionId": "00000000-0000-4000-8000-000000000001",
          "messages": [
            {
              "role": "user",
              "content": "Build is green on both legs.",
              "timestamp": 1787880000500,
              "__openclaw": {
                "senderIsOwner": false,
                "senderId": "900000000000000103",
                "senderName": "Quillbot",
                "senderUsername": "Quillbot",
                "id": "00000000-0000-4000-8000-0000000000c1",
                "recordTimestampMs": 1787880000600,
                "seq": 1
              }
            },
            {
              "role": "user",
              "content": "ok",
              "timestamp": 1787880060500,
              "__openclaw": {
                "senderIsOwner": true,
                "senderId": "900000000000000101",
                "senderName": "quillfeather",
                "id": "00000000-0000-4000-8000-0000000000c2",
                "recordTimestampMs": 1787880060600,
                "seq": 2
              }
            },
            {
              "role": "user",
              "content": "ok",
              "timestamp": 1787880120500,
              "__openclaw": {
                "senderIsOwner": true,
                "senderId": "900000000000000101",
                "senderName": "quillfeather",
                "id": "00000000-0000-4000-8000-0000000000c3",
                "recordTimestampMs": 1787880120600,
                "seq": 3
              }
            },
            {
              "role": "user",
              "content": "Build is green on both legs. Are we sure about the arm64 one?",
              "timestamp": 1787880180500,
              "__openclaw": {
                "senderIsOwner": false,
                "senderId": "900000000000000102",
                "senderName": "Thistle",
                "senderUsername": "Thistle",
                "id": "00000000-0000-4000-8000-0000000000c4",
                "recordTimestampMs": 1787880180600,
                "seq": 4
              }
            }
          ],
          "offset": 0,
          "hasMore": false,
          "totalMessages": 4
        }
        """;

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    // A moment just after the newest session in the fixture, so "the last hour"
    // means something fixed rather than something that depends on when the suite
    // is run.
    private static readonly DateTime Now = new(2025, 11, 20, 12, 0, 0, DateTimeKind.Utc);

    private const string Room = "discord:900000000000000001";

    private static (IReadOnlyList<OpenClawSessions.Session> Sessions, int Total) Parse(
        int withinMinutes)
    {
        OrbweaverSettings.OpenClawHeartbeatMode = ClusterMode.WithChats;
        OrbweaverSettings.OpenClawCronMode = ClusterMode.WithChats;
        OrbweaverSettings.OpenClawNativeMode = ClusterMode.WithChats;
        OrbweaverSettings.OpenClawActiveWithinMinutes = withinMinutes;

        return OpenClawSessions.Parse(Json(SessionsList), Now);
    }

    // CB-270: one page in the gateway's envelope, the field structure of the rows
    // measured on a live 2026.10.5 gateway and every value invented (phone numbers
    // are +1555000000N, Discord ids are 190000000000000000N, names are made up).
    // By category, what each row carried:
    //   native main      classification main; no origin, deliveryContext or
    //                    lastChannel (3 of the 5 main rows measured)
    //   whatsapp main    classification main; origin.provider, deliveryContext
    //                    and lastChannel all whatsapp (the other 2)
    //   whatsapp direct  classification direct; all three present
    //   whatsapp group   classification group; all three present
    //   discord dm       classification direct; all three present
    //   discord channel  classification channel; all three present
    //   cron             classification cron; none of the three
    //   dashboard        a New Chat; the live gateway had none, so its shape is
    //                    the sessions.create result the findings record
    //   pinned dm        a Discord DM with pinned:true
    // Every row is two days old so the recency window has something to decide.
    private static string NativePage()
    {
        var old = new DateTimeOffset(Now.AddDays(-2)).ToUnixTimeMilliseconds();

        static string Surface(string provider, string to, string classification, string chatType) =>
            $$"""
              "classification":"{{classification}}","chatType":"{{chatType}}",
              "origin":{"provider":"{{provider}}","to":"{{to}}"},
              "deliveryContext":{"channel":"{{provider}}","to":"{{to}}"},
              "lastChannel":"{{provider}}","lastTo":"{{to}}"
              """;

        return $$"""
            {"ts":1700000000000,"path":"/x/sessions.json","count":9,"totalCount":9,"hasMore":false,
             "sessions":[
              {"key":"agent:alpha:main","kind":"direct","isMain":true,"classification":"main","pinned":false,
               "sessionId":"sid-n1","lastActivityAt":{{old}}},
              {"key":"agent:beta:main","kind":"direct","isMain":true,"pinned":false,"sessionId":"sid-n2",
               {{Surface("whatsapp", "+15550000001", "main", "direct")}},"lastActivityAt":{{old}}},
              {"key":"agent:alpha:whatsapp:direct:+15550000002","pinned":false,"sessionId":"sid-n3",
               {{Surface("whatsapp", "+15550000002", "direct", "direct")}},"lastActivityAt":{{old}}},
              {"key":"agent:alpha:whatsapp:group:1555000000300000003@g.us","pinned":false,"sessionId":"sid-n4",
               {{Surface("whatsapp", "1555000000300000003@g.us", "group", "group")}},"lastActivityAt":{{old}}},
              {"key":"agent:alpha:discord:direct:1900000000000000001","pinned":false,"sessionId":"sid-n5",
               {{Surface("discord", "user:1900000000000000001", "direct", "direct")}},"lastActivityAt":{{old}}},
              {"key":"agent:alpha:discord:channel:1900000000000000002","pinned":false,"sessionId":"sid-n6",
               {{Surface("discord", "channel:1900000000000000002", "channel", "channel")}},"lastActivityAt":{{old}}},
              {"key":"agent:alpha:cron:job-0001","classification":"cron","pinned":false,"sessionId":"sid-n7",
               "label":"Cron: sweep","lastActivityAt":{{old}}},
              {"key":"agent:alpha:dashboard:11111111-2222-3333-4444-555555555555","classification":"dashboard",
               "pinned":false,"sessionId":"sid-n8","label":"Scratch pad","lastActivityAt":{{old}}},
              {"key":"agent:alpha:discord:direct:1900000000000000003","pinned":true,"sessionId":"sid-n9",
               {{Surface("discord", "user:1900000000000000003", "direct", "direct")}},"lastActivityAt":{{old}}}
             ]}
            """;
    }

    private static (IReadOnlyList<OpenClawSessions.Session> Sessions, int Total) ParseNative(
        ClusterMode native = ClusterMode.WithChats)
    {
        OrbweaverSettings.OpenClawHeartbeatMode = ClusterMode.WithChats;
        OrbweaverSettings.OpenClawCronMode = ClusterMode.WithChats;
        OrbweaverSettings.OpenClawNativeMode = native;
        OrbweaverSettings.OpenClawActiveWithinMinutes = 60;

        return OpenClawSessions.Parse(Json(NativePage()), Now);
    }

    // The whole exchange. Nine rows two days old against a 60 minute window:
    // exactly the dashboard row (retention) and the pinned DM (the gateway's own
    // signal) survive, and each row's native kind, heartbeat flag and pin are what
    // the category above says. A format someone else defines, so this is the
    // seam test the unit cases cannot replace.
    [Fact]
    public void ANativePageSurvivesTheWindowOnlyWhereRetentionSaysSo()
    {
        var (sessions, total) = ParseNative();

        Assert.Equal(9, total);
        Assert.Equal(
            new[]
            {
                "agent:alpha:dashboard:11111111-2222-3333-4444-555555555555",
                "agent:alpha:discord:direct:1900000000000000003",
            },
            sessions.Select(s => s.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray());

        var dashboard = sessions.Single(s => s.Key.Contains(":dashboard:"));
        Assert.Equal(SessionKind.Main, dashboard.Kind);
        Assert.Equal(OpenClawNativeKind.Dashboard, dashboard.Native);
        Assert.False(dashboard.Heartbeat);
        Assert.False(dashboard.GatewayPinned);
        Assert.Equal("Scratch pad", dashboard.Label);

        var pinned = sessions.Single(s => s.Key.EndsWith("0003"));
        Assert.Equal(SessionKind.Direct, pinned.Kind);
        Assert.Equal(OpenClawNativeKind.None, pinned.Native);
        Assert.True(pinned.GatewayPinned);
    }

    // Every row's native verdict, read through NativeOf because the recency
    // filter dropped most of them from the result. Only the dashboard row and the
    // native main are native; every Discord and WhatsApp row, the WhatsApp main
    // included, is None, which is the negative control this classifier lives on.
    [Fact]
    public void OnlyTheDashboardAndTheNativeMainAreNative()
    {
        ParseNative();

        var verdicts = new Dictionary<string, OpenClawNativeKind?>
        {
            ["agent:alpha:main"] = OpenClawNativeKind.Main,
            ["agent:beta:main"] = OpenClawNativeKind.None,
            ["agent:alpha:whatsapp:direct:+15550000002"] = OpenClawNativeKind.None,
            ["agent:alpha:whatsapp:group:1555000000300000003@g.us"] = OpenClawNativeKind.None,
            ["agent:alpha:discord:direct:1900000000000000001"] = OpenClawNativeKind.None,
            ["agent:alpha:discord:channel:1900000000000000002"] = OpenClawNativeKind.None,
            ["agent:alpha:cron:job-0001"] = OpenClawNativeKind.None,
            ["agent:alpha:dashboard:11111111-2222-3333-4444-555555555555"] = OpenClawNativeKind.Dashboard,
            ["agent:alpha:discord:direct:1900000000000000003"] = OpenClawNativeKind.None,
        };

        foreach (var (key, expected) in verdicts)
            Assert.Equal(expected, OpenClawSessions.NativeOf(key));
    }

    // Hiding native sessions on the same page takes the dashboard row away and
    // leaves the pinned Discord DM, which no native setting governs.
    [Fact]
    public void HidingNativeSessionsLeavesTheSurfaceRowsAlone()
    {
        var (sessions, _) = ParseNative(ClusterMode.Hidden);

        Assert.Equal("agent:alpha:discord:direct:1900000000000000003", Assert.Single(sessions).Key);
    }

    // --- sessions.list, all the way to an address --------------------------

    // Everyone in the channel is a member of the room, however long ago they
    // last said anything. Membership is recorded before the recency filter and
    // deliberately ignores it — an agent that spoke last week is still one of
    // the people standing in the channel.
    [Fact]
    public void EveryAgentInTheChannelIsAMemberOfTheRoom()
    {
        Parse(withinMinutes: 60);

        var members = OpenClawSessions.MembersOfRoom(Room);

        Assert.Equal(9, members.Count);
        Assert.Contains("agent:quill:discord:channel:900000000000000001", members);
        Assert.Contains("agent:moss:discord:channel:900000000000000001", members);
    }

    // The window still cuts the orbs, which is what it is for: seven of the nine
    // have been quiet for longer than an hour and none of them gets one.
    [Fact]
    public void TheRecencyWindowStillDecidesWhichOrbsAreDrawn()
    {
        var (sessions, total) = Parse(withinMinutes: 60);

        Assert.Equal(9, total);
        Assert.Equal(2, sessions.Count);
        Assert.All(sessions, s => Assert.Contains("channel:900000000000000001", s.Key));
    }

    // ...and every one of the nine still has somewhere to deliver, orb or no
    // orb. This is CB-27 end to end: before the fix, a member outside the window
    // had no address, so a room whose members had all gone quiet could not post
    // to its channel at all.
    [Fact]
    public void AMemberOutsideTheWindowStillHasAnAddress()
    {
        Parse(withinMinutes: 60);
        OrbweaverSettings.OpenClawEnabled = true;

        foreach (var key in OpenClawSessions.MembersOfRoom(Room))
        {
            var chat = (OpenClawChatSession)OpenClawSessions.ChatFor("openclaw:" + key, "#lobby")!;

            Assert.NotNull(chat.Delivery);
            Assert.Equal("discord", chat.Delivery!.Channel);
            Assert.Equal("channel:900000000000000001", chat.Delivery.To);
            Assert.False(string.IsNullOrWhiteSpace(chat.Delivery.AccountId));
        }
    }

    // The accountId is the one part of the address that differs per member, and
    // the one part the room key cannot be made to yield. It is what makes the
    // gateway suppress a bot's own channel post from that bot's own sessions,
    // which is what stops a room send reaching its carrier twice.
    [Fact]
    public void EachMemberCarriesItsOwnAccount()
    {
        Parse(withinMinutes: 60);
        OrbweaverSettings.OpenClawEnabled = true;

        var accounts = OpenClawSessions.MembersOfRoom(Room)
            .Select(k => ((OpenClawChatSession)OpenClawSessions.ChatFor("openclaw:" + k, "#lobby")!)
                .Delivery!.AccountId)
            .ToList();

        // Eight distinct accounts across nine members: two of these agents share
        // one, which is real and is why this asserts "several" rather than "all
        // different".
        Assert.True(accounts.Distinct().Count() >= 8, string.Join(",", accounts));
    }

    // --- chat.history, all the way to who said it --------------------------

    // One page carrying all five message shapes the gateway was observed to
    // produce, read in one pass.
    [Fact]
    public void APageOfHistoryIsClassifiedShapeByShape()
    {
        var payload = Json(HistoryPage);
        var page = payload.GetProperty("messages");

        // The key off the payload itself, which is where production gets it
        // too — FetchHistoryPageAsync passes the key it asked with, and every
        // picture route on the page carries it so the gateway resolves its
        // media policy against the right agent (CB-109).
        var turns = OpenClawSessions.TurnsFromHistory(
            page, payload.GetProperty("sessionKey").GetString());

        Assert.Equal(6, turns.Count);

        // The operator, typing in Discord.
        Assert.True(turns[0].Mine);
        Assert.Null(turns[0].Speaker);

        // The agent whose transcript this is. Never classified, never yours,
        // whatever its idempotency key looks like.
        Assert.Equal(ChatRole.Assistant, turns[1].Role);
        Assert.False(turns[1].Mine);

        // Another agent, relayed through the channel — named, and uncoloured
        // because a Discord display name is not an agent id.
        Assert.Equal("Thistle", turns[2].Speaker);
        Assert.False(turns[2].Mine);
        Assert.Null(turns[2].SpeakerColor);

        // The operator, typing here: no sender fields, and an idempotency key
        // ending ":user".
        Assert.True(turns[3].Mine);

        // The same message as the one before it, as the other agents in the room
        // received it — prefixed, attributed to the bot that carried it, and
        // still yours. Shown without the prefix, which is what lets the two
        // dedupe into one bubble.
        Assert.True(turns[4].Mine);
        Assert.Equal(turns[3].Text, turns[4].Text);

        // An inter-session message, whose machine header Readable strips and
        // whose speaker it identifies — so the classification leaves it alone.
        Assert.False(turns[5].Mine);
        Assert.Equal("can you take the release notes?", turns[5].Text);
        Assert.NotNull(turns[5].Speaker);
    }

    // --- two pages, merged into one room -----------------------------------

    // CB-32 and CB-33 across the whole exchange: two members' pages of real
    // gateway shape, parsed by the same code production parses them with, loaded
    // into real sessions and merged by the real room.
    //
    // Here as well as in tests/UnitTests because the two fail differently. The
    // unit suite hands the merge strings and asks whether the rule is right; this
    // asks whether the rule is being asked the right question — the merge keys on
    // what `TurnsFromHistory` produced, so a `senderIsOwner` read wrong, or a
    // mirror prefix left on, changes which turns look identical to each other
    // without changing a line of the merge.
    [Fact]
    public void ARoomBuiltFromTwoRealPagesKeepsRepeatsAndQuotes()
    {
        var quillPayload = Json(QuillPage);
        var asterPayload = Json(AsterPage);

        var quill = new OpenClawChatSession(
            "openclaw:agent:quill:discord:channel:900000000000000001",
            "agent:quill:discord:channel:900000000000000001", "quill");
        var aster = new OpenClawChatSession(
            "openclaw:agent:aster:discord:channel:900000000000000001",
            "agent:aster:discord:channel:900000000000000001", "aster");

        quill.SetHistory(OpenClawSessions.TurnsFromHistory(
            quillPayload.GetProperty("messages"),
            quillPayload.GetProperty("sessionKey").GetString()));
        aster.SetHistory(OpenClawSessions.TurnsFromHistory(
            asterPayload.GetProperty("messages"),
            asterPayload.GetProperty("sessionKey").GetString()));

        // Nothing held back, so the trust window does not also cut this.
        quill.HasMore = false;
        aster.HasMore = false;

        var room = new OpenClawRoomChatSession("openclaw:room:" + Room, "#lobby");
        room.SetMembers(new[] { (quill, "Quill", "#7f7"), (aster, "Aster", "#77f") });
        room.Rebuild();

        // Quill's answer once, attributed to Quill from its own transcript
        // rather than to the bot account the relay wore.
        var mine = room.History.Where(t => t.Mine).ToList();
        var quillsAnswer = room.History
            .Where(t => t.Text == "Build is green on both legs.")
            .ToList();

        Assert.Single(quillsAnswer);
        Assert.Equal("Quill", quillsAnswer[0].Speaker);

        // "ok" twice — two messages, four deliveries, and this is the count that
        // used to come back as one.
        Assert.Equal(2, mine.Count);
        Assert.All(mine, t => Assert.Equal("ok", t.Text));

        // Thistle's quote, whole, once. This is the message that used to be
        // drawn nowhere at all.
        var quote = Assert.Single(room.History, t => t.Speaker == "Thistle");
        Assert.Equal(
            "Build is green on both legs. Are we sure about the arm64 one?", quote.Text);

        Assert.Equal(4, room.History.Count);
    }

    // --- the assistant-media request, as a shape ---------------------------
    //
    // CB-109's seam is an HTTP request the gateway defines, so it gets covered
    // here as well as in tests/UnitTests, per CLAUDE.md's rule for a format
    // this process does not own. What is asserted is the request *shape* and
    // the parsing of captured answers — **no socket is opened and none should
    // ever be.** CI runners have no OpenClaw gateway at all, and a gateway
    // call with no timeout does not fail a suite, it stalls it: one such call
    // on this ticket hung for over ten minutes and had to be killed as an
    // orphan. A test that silently needs a healthy gateway is a flake with a
    // schedule.
    //
    // The same pattern as everything else in this file: fixtures derived from
    // what a real gateway actually returned, asserted against the real code.

    private const string MediaPath =
        "/Users/w/.openclaw/workspace-sample-agent/outputs/demo/sample_40.png";

    private const string MediaSessionKey = "agent:comfyui:discord:direct:100000000000000001";

    // The exact string that goes on the wire, spelled out rather than
    // computed, so a change to how it is built has to be a change to this line
    // too. Order included: `source` first, because the app recognises one of
    // these urls by prefix and CB-93's refusal note is what silently stops
    // working if anything is put in front of it.
    [Fact]
    public void TheAssistantMediaRequestIsTheFileAndTheAskingSession()
    {
        var route = new OpenClawMediaSource(MediaPath, MediaSessionKey).Route;

        Assert.Equal(
            "/__openclaw__/assistant-media?source="
            + "%2FUsers%2Fw%2F.openclaw%2Fworkspace-sample-agent%2Foutputs%2Fdemo%2Fsample_40.png"
            + "&sessionKey=agent%3Acomfyui%3Adiscord%3Adirect%3A100000000000000001",
            route);

        // No agentId, in any arrangement. Measured on a healthy gateway
        // (OpenClaw 2026.9.2, 3928bad): the key alone serves the file — 200,
        // 1,116,874 bytes for one of the corpus files — while `agentId=main`
        // paired with this same key answers 404 Not Found and `agentId=comfyui`
        // paired with it answers available:true. So a client-sent agent id can
        // only ever disagree, and the key already carries the provenance.
        Assert.DoesNotContain("agentId", route, StringComparison.Ordinal);
    }

    // The explanation is asked with the identity the fetch used, because it is
    // asked with the *same string* plus a flag. An explanation asked with a
    // different identity than the fetch does not fail — it lies, which is
    // worse than the silence CB-93 set out to remove.
    [Fact]
    public void TheMetaRequestIsTheFetchPlusAFlag()
    {
        var source = new OpenClawMediaSource(MediaPath, MediaSessionKey);

        Assert.Equal(source.Route + "&meta=1", source.MetaRoute);
        Assert.Equal(source.MetaRoute, OpenClawMediaSource.MetaOf(source.Route));
        Assert.Contains("&sessionKey=", source.MetaRoute, StringComparison.Ordinal);
        Assert.EndsWith("&meta=1", source.MetaRoute, StringComparison.Ordinal);
    }

    // With no session the request is byte-identical to what this app sent
    // before CB-109 — the no-regression pin, at the seam rather than only at
    // the constructor. Nothing in production builds one of these; a fixture
    // can.
    [Fact]
    public void WithNoSessionTheRequestIsUnchangedFromBeforeTheFix()
    {
        Assert.Equal(
            OpenClawSessions.AssistantMediaRoute + Uri.EscapeDataString(MediaPath),
            new OpenClawMediaSource(MediaPath, null).Route);
    }

    // Both prefixes match a real request, which is what keeps the refusal note
    // visible: ShouldAskWhy gates on the parameterless prefix so recognition
    // survives a reordering, and the older constant still matches because
    // `source` is still first.
    [Fact]
    public void ARealRequestIsRecognisedAsOneOfTheseRoutes()
    {
        var route = new OpenClawMediaSource(MediaPath, MediaSessionKey).Route;

        Assert.StartsWith(OpenClawSessions.AssistantMediaPathPrefix, route, StringComparison.Ordinal);
        Assert.StartsWith(OpenClawSessions.AssistantMediaRoute, route, StringComparison.Ordinal);
        Assert.True(OpenClawMediaRefusal.ShouldAskWhy(Array.Empty<byte>(), route));
        Assert.True(OpenClawMediaRefusal.ShouldAskWhy(null, OpenClawMediaSource.MetaOf(route)));
    }

    // The four meta bodies a real gateway returned for this route, verbatim
    // apart from the ticket value, read back through the real code.
    //
    // Detail is what is asserted rather than Explain's sentence: the tooltip is
    // where the path and the gateway's own code have to survive intact, and the
    // sentences themselves are CB-108's to word.
    [Theory]
    [InlineData("""{"available":false,"code":"outside-allowed-folders","reason":"Outside allowed folders","retryable":false,"canAllow":true}""",
                "outside-allowed-folders")]
    [InlineData("""{"available":false,"code":"unsupported-media-type","reason":"Not an image"}""",
                "unsupported-media-type")]
    [InlineData("""{"available":false,"code":"file-not-found"}""", "file-not-found")]
    public void ACapturedRefusalBodyKeepsItsCodeAndPathInTheTooltip(string json, string code)
    {
        Assert.Equal(MediaPath + " — " + code, OpenClawMediaRefusal.Detail(json, MediaPath));

        // And the line above it says something, whatever CB-108 has it say.
        Assert.False(string.IsNullOrWhiteSpace(OpenClawMediaRefusal.Explain(json)));
    }

    // The success body, which is what the same file answers *with* a session.
    // mediaTicket and mediaTicketExpiresAt are present in it and are
    // deliberately unread: the bytes come back 200 on the sessionKey alone, so
    // presenting a ticket would mean a mandatory meta round trip per picture to
    // obtain a credential the server does not ask for.
    [Fact]
    public void TheSuccessBodyIsNotTreatedAsARefusalAndItsTicketIsIgnored()
    {
        const string json = """
        {"available":true,"mimeType":"image/png","sizeBytes":1116874,
         "mediaTicket":"v1.aaaaaaaa","mediaTicketExpiresAt":"2026-09-06T00:05:00.000Z"}
        """;

        Assert.DoesNotContain("refused", OpenClawMediaRefusal.Explain(json), StringComparison.Ordinal);
        Assert.Equal(MediaPath, OpenClawMediaRefusal.Detail(json, MediaPath));
    }

    // The traversal the gateway normalises and then cannot find. This app
    // refuses it before ever asking — LooksLikeAnImagePath, reached through
    // LocalMediaPathFrom — which since CB-109 is the only structural guard
    // left on this path, because supplying a session disables the gateway's
    // folder allowlist rather than scoping it.
    [Theory]
    [InlineData("/Users/w/.openclaw/media/../../../../etc/passwd.png")]
    [InlineData("//evil.example/a.png")]
    public void ATraversalNeverBecomesARequestAtAll(string path)
    {
        Assert.Null(OpenClawSessions.LocalMediaPathFrom("MEDIA:" + path));
    }

    // --- the room-send failure path ----------------------------------------

    // With no gateway there is nothing to post through, and the room says so in
    // its own transcript rather than going quiet. Silence is the failure this
    // ticket is about: the message looked sent and nobody in the channel had it.
    //
    // Through the real SendToRoomAsync rather than a stand-in, because the thing
    // being checked is that the refusal survives the whole path — carrier pick,
    // send attempt, sentence, note — and lands somewhere a person will read it.
    [Fact]
    public async Task ARoomSendThatCannotHappenSaysSoInTheRoom()
    {
        OrbweaverSettings.ReloadForTests();
        OrbweaverSettings.OpenClawReplyEnabled = true;

        Parse(withinMinutes: 60);
        OrbweaverSettings.OpenClawEnabled = true;

        var members = OpenClawSessions.MembersOfRoom(Room)
            .Select(k => ((OpenClawChatSession)OpenClawSessions.ChatFor("openclaw:" + k, "#lobby")!,
                          k.Split(':')[1], "#7f7"))
            .ToList();

        foreach (var (chat, _, _) in members) chat.HasMore = false;

        var room = new OpenClawRoomChatSession("openclaw:room:" + Room, "#lobby");
        room.SetMembers(members);

        await room.SendAsync("anyone free to look at the build?");

        // Your message, and under it the reason it went nowhere.
        Assert.Contains(room.History, t => t.Mine && t.Text == "anyone free to look at the build?");

        var note = Assert.Single(room.History, t => t.Role == ChatRole.System);
        Assert.StartsWith("Couldn't post to #lobby:", note.Text);
        Assert.Contains("Nothing was sent", note.Text);
    }

    // CB-274 / CB-275: a whole chat.history page in the gateway's envelope —
    // display-projected content beside the automation carrier and the other
    // keys the gateway really sends — walked into turns. Real structure,
    // invented values; the success shapes are from the gateway's source.
    [Fact]
    public void AHistoryPageOfEveryAttachmentKindWalksIntoOneTurnEach()
    {
        var page = JsonDocument.Parse("""
        {"messages":[
         {"role":"assistant","provider":"openclaw","model":"automation-result","api":"openclaw-transcript",
          "timestamp":1700000000001,"idempotencyKey":"cron-current-completion:cron:job-att0001:1",
          "openclawAutomation":{"kind":"cron","jobId":"job-att0001","runId":"cron:job-att0001:1"},
          "content":[{"type":"text","text":"soft light"},
                     {"type":"attachment_error","attachment":{"code":"delivery-failed","kind":"video","label":"sample_clip.mp4","mimeType":"video/mp4"}}]},
         {"role":"assistant","provider":"openclaw","model":"automation-result","timestamp":1700000000002,
          "content":[{"type":"text","text":"a picture"},
                     {"type":"image","url":"/api/chat/media/outgoing/agent%3Amain%3Adiscord%3Adirect%3A1/p1/full","alt":"pic.png","mimeType":"image/png","width":832,"height":1216,"sizeBytes":1021973}]},
         {"role":"assistant","timestamp":1700000000003,
          "content":[{"type":"video","url":"/api/chat/media/outgoing/agent%3Amain%3Adiscord%3Adirect%3A1/v1/full","fileName":"v.mp4","mimeType":"video/mp4","sizeBytes":1042813,"durationMs":12750,"width":544,"height":800}]},
         {"role":"assistant","timestamp":1700000000004,
          "content":[{"type":"audio","url":"/api/chat/media/outgoing/agent%3Amain%3Adiscord%3Adirect%3A1/a1/full","fileName":"a.m4a","mimeType":"audio/mp4","sizeBytes":240000,"durationMs":42000}]},
         {"role":"assistant","timestamp":1700000000005,
          "content":[{"type":"attachment","attachment":{"url":"/api/chat/media/outgoing/agent%3Amain%3Adiscord%3Adirect%3A1/d1/full","kind":"document","label":"r.pdf","mimeType":"application/pdf","sizeBytes":1258291}}]},
         {"role":"assistant","timestamp":1700000000006,
          "content":[{"type":"attachment_error","attachment":{"code":"unsupported-format","kind":"document","label":"x.pdf","mimeType":"application/pdf"}}]}]}
        """).RootElement.GetProperty("messages");

        var turns = OpenClawSessions.TurnsFromHistory(page, "agent:main:discord:direct:1");

        // The picture message is two turns (the picture, then its caption —
        // image blocks are read first), as it is today; every other message
        // is exactly one.
        Assert.Equal(7, turns.Count);
        Assert.Equal(
            new AttachmentKind?[]
            {
                AttachmentKind.Video, null, null, AttachmentKind.Video,
                AttachmentKind.Audio, AttachmentKind.Document, AttachmentKind.Document
            },
            turns.Select(t => t.Attachment?.Kind).ToArray());

        Assert.Equal("soft light", turns[0].Text);
        Assert.True(turns[0].Attachment!.Value.Failed);
        Assert.Equal("job-att0001", turns[0].Automation!.Value.JobId);

        var picture = turns[1];
        Assert.Equal("a picture", turns[2].Text);
        Assert.Equal("/api/chat/media/outgoing/agent%3Amain%3Adiscord%3Adirect%3A1/p1/full", picture.ImageUrl);
        Assert.Null(picture.Attachment);

        Assert.All(turns.Skip(3).Take(3), t => Assert.Null(t.ImageSourcePath));
        Assert.StartsWith("/api/chat/media/outgoing/", turns[3].ImageUrl);
        Assert.StartsWith("/api/chat/media/outgoing/", turns[4].ImageUrl);
        Assert.StartsWith("/api/chat/media/outgoing/", turns[5].ImageUrl);
        Assert.True(turns[6].Attachment!.Value.Failed);
        Assert.Equal("unsupported-format", turns[6].Attachment!.Value.FailureCode);
    }

    // CB-277: agents.list as the gateway lays it out — FROM SOURCE for the
    // `workspace` field (openclaw 2026.10.5 session-utils: workspace is set on
    // every row), because the probe is behind the Local Network gate on the
    // machine this was written on. Invented values on that structure; if a
    // real response names the field differently this is the fixture to fix.
    [Fact]
    public void AnAgentsListWalksIntoWorkspacesAndOwnersAChosenSessionForAClip()
    {
        var agentsList = JsonDocument.Parse("""
        {"defaultId":"main","mainKey":"main","scope":"per-sender","agents":[
          {"id":"main","name":"Sample One","workspace":"/Users/sample/.openclaw/workspace","identity":{"name":"Sample One","emoji":"x"}},
          {"id":"comfyui","name":"Sample Two","workspace":"/Users/sample/.openclaw/workspace-sample-zara","workspaceGit":{"enabled":false}}]}
        """).RootElement;

        var workspaces = OpenClawSessions.WorkspacesFrom(agentsList);
        var index = new OwnerIndex(workspaces, new[]
        {
            "agent:main:discord:direct:1", "agent:comfyui:discord:direct:1",
        });

        const string path = "/Users/sample/.openclaw/workspace-sample-zara/outputs/sample/clip_9.mp4";
        var history = JsonDocument.Parse("""
        [{"role":"assistant","provider":"claude-cli-example","timestamp":1700000900000,
          "content":[{"type":"text","text":"here you go\nMEDIA:/Users/sample/.openclaw/workspace-sample-zara/outputs/sample/clip_9.mp4"}]}]
        """).RootElement;

        var turn = Assert.Single(OpenClawSessions.TurnsFromHistory(history, "agent:main:discord:direct:1", index));

        Assert.Equal(
            "/__openclaw__/assistant-media?source="
            + "%2FUsers%2Fsample%2F.openclaw%2Fworkspace-sample-zara%2Foutputs%2Fsample%2Fclip_9.mp4"
            + "&sessionKey=agent%3Acomfyui%3Adiscord%3Adirect%3A1",
            turn.ImageUrl);
        Assert.Equal(path, turn.ImageSourcePath);
    }

    // The control, same page: with no index the route is the one the app sent
    // before CB-277, byte for byte.
    [Fact]
    public void TheSameHistoryPageWithoutAnIndexKeepsTheAskingSessionOnTheRoute()
    {
        var history = JsonDocument.Parse("""
        [{"role":"assistant","content":[{"type":"text","text":"here you go\nMEDIA:/Users/sample/.openclaw/workspace-sample-zara/outputs/sample/clip_9.mp4"}]}]
        """).RootElement;

        var turn = Assert.Single(OpenClawSessions.TurnsFromHistory(history, "agent:main:discord:direct:1"));

        Assert.EndsWith("&sessionKey=agent%3Amain%3Adiscord%3Adirect%3A1", turn.ImageUrl);
    }

    // The `&meta=1` answer for a video, field names and numbers as measured
    // against a real gateway for a real clip (values: duration, size and
    // dimensions only; the ticket is invented).
    [Fact]
    public void AVideoMetaAnswerWalksIntoATileSizedClip()
    {
        const string meta =
            "{\"available\":true,\"mimeType\":\"video/mp4\",\"sizeBytes\":1042813,\"playback\":\"native\","
            + "\"durationMs\":12750,\"width\":544,\"height\":800,\"mediaTicket\":\"v1.sample\"}";

        var parsed = OpenClawVideo.ParseMeta(meta);

        Assert.NotNull(parsed);
        var clip = OpenClawVideo.FromMeta(parsed!.Value, "clip_9.mp4");
        Assert.Equal(12750, clip.DurationMs);
        Assert.Equal(544, clip.Width);
        Assert.Equal(800, clip.Height);
        Assert.Equal(1042813, clip.SizeBytes);
        Assert.Equal("0:13", clip.Duration);
    }

    // CB-270: the send policy reads NativeOf, so every conversation on a chat
    // surface in a real-shaped sessions.list must come out as not native —
    // otherwise its reply would stop being delivered to its channel. The
    // fixture's channel rows carry no classification at all, which is the
    // fail-safe case: absent means today's behaviour.
    [Fact]
    public void EveryChannelRowInTheListIsClassifiedAsNotNative()
    {
        Parse(withinMinutes: 60);

        var members = OpenClawSessions.MembersOfRoom(Room);
        Assert.Equal(9, members.Count);
        Assert.All(members, key =>
            Assert.Equal(OpenClawNativeKind.None, OpenClawSessions.NativeOf(key)));
    }

    // The control for the test above: the same envelope with a native main, a
    // dashboard session and the two surface shapes that look closest to them
    // (a WhatsApp main, a Discord DM) does classify the first two as native and
    // the others not, so the previous result is not just "nothing is ever
    // native". Invented values on the structure measured in CB-270.
    [Fact]
    public void OnlyTheNativeRowsOfAMixedPageAreClassifiedAsNative()
    {
        OrbweaverSettings.OpenClawHeartbeatMode = ClusterMode.WithChats;
        OrbweaverSettings.OpenClawActiveWithinMinutes = OrbweaverSettings.OpenClawActiveWithinAll;

        var at = new DateTimeOffset(Now.AddSeconds(-5)).ToUnixTimeMilliseconds();
        OpenClawSessions.Parse(Json($$"""
        {"sessions":[
          {"key":"agent:alpha:main","classification":"main","isMain":true,"lastActivityAt":{{at}}},
          {"key":"agent:alpha:dashboard:11111111-2222-3333-4444-555555555555","classification":"dashboard","lastActivityAt":{{at}}},
          {"key":"agent:beta:main","classification":"main","isMain":true,"chatType":"direct",
           "origin":{"provider":"whatsapp","to":"+15550000001"},
           "deliveryContext":{"channel":"whatsapp","to":"+15550000001"},"lastChannel":"whatsapp","lastActivityAt":{{at}}},
          {"key":"agent:alpha:discord:direct:1900000000000000001","classification":"direct","chatType":"direct",
           "origin":{"provider":"discord"},"deliveryContext":{"channel":"discord","to":"user:1900000000000000001"},
           "lastChannel":"discord","lastActivityAt":{{at}}}
        ]}
        """), Now);

        Assert.Equal(OpenClawNativeKind.Main, OpenClawSessions.NativeOf("agent:alpha:main"));
        Assert.Equal(OpenClawNativeKind.Dashboard,
            OpenClawSessions.NativeOf("agent:alpha:dashboard:11111111-2222-3333-4444-555555555555"));
        Assert.Equal(OpenClawNativeKind.None, OpenClawSessions.NativeOf("agent:beta:main"));
        Assert.Equal(OpenClawNativeKind.None,
            OpenClawSessions.NativeOf("agent:alpha:discord:direct:1900000000000000001"));
    }
}

