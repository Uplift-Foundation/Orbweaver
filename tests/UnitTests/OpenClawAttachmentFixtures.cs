namespace Orbweaver.UnitTests;

// CB-274 / CB-275 fixtures, with where each shape came from.
//
// STRUCTURE FROM REAL ROWS, VALUES INVENTED. This repository is public and
// the gateway is a private server with real people on it, so every id, name,
// path, caption and timestamp below is invented; the *shape* is what was read
// off a real gateway's stored transcript (five attachment_error rows and a
// delivered-picture row). A stored row carries `content` as caption + "\n" +
// filename and, beside it, `openclawDisplayContent`; chat.history serves the
// second in place of the first, so SeqNineSevenSeven is the stored row's
// shape with `content` replaced by its display content — the projection's
// documented effect. No captured chat.history response was available (the
// probe is behind the Local Network gate on the MacBook).
//
// FROM SOURCE, NOT FROM THE WILD (openclaw 2026.10.5 buildManagedMediaBlock;
// zero real rows exist): the video, audio and attachment success shapes and
// the non-video attachment_error kinds. If the real emission names a field
// differently, AttachmentOf's fallbacks absorb it, and these are the
// fixtures to correct.
internal static class OpenClawAttachmentFixtures
{
    internal const string JobId = "0a1b2c3d-1111-2222-3333-444455556666";
    internal const string ClipName = "sample_pink_afternoon_video_2pm_1234567890.mp4";
    internal const string Caption = "Soft light and a little message just for you 🌸";

    internal const string SeqNineSevenSeven = """
    {"role":"assistant","content":[
      {"type":"text","text":"Soft light and a little message just for you 🌸"},
      {"type":"attachment_error","attachment":{"code":"delivery-failed","kind":"video","label":"sample_pink_afternoon_video_2pm_1234567890.mp4","mimeType":"video/mp4"}}],
     "api":"openclaw-transcript","provider":"openclaw","model":"automation-result","stopReason":"stop",
     "timestamp":1700000000001,
     "idempotencyKey":"cron-current-completion:cron:0a1b2c3d-1111-2222-3333-444455556666:1700000000000",
     "openclawAutomation":{"kind":"cron","jobId":"0a1b2c3d-1111-2222-3333-444455556666","runId":"cron:0a1b2c3d-1111-2222-3333-444455556666:1700000000000"}}
    """;

    // The same message as an older gateway serves it: the raw text, with the
    // filename on the last line and no blocks.
    internal const string SeqNineSevenSevenRaw = """
    {"role":"assistant","content":[
      {"type":"text","text":"Soft light and a little message just for you 🌸\nsample_pink_afternoon_video_2pm_1234567890.mp4"}],
     "provider":"openclaw","model":"automation-result","timestamp":1700000000001,
     "openclawAutomation":{"kind":"cron","jobId":"0a1b2c3d-1111-2222-3333-444455556666","runId":"r"}}
    """;

    // The real delivered-picture row (seq 1018), display form.
    internal const string SeqTenEighteen = """
    {"role":"assistant","content":[
      {"type":"text","text":"Slow morning and lazy light 😘"},
      {"type":"image","artifactId":"artifact_managed_image_aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","url":"/api/chat/media/outgoing/agent%3Amain%3Adiscord%3Adirect%3A1/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee/full","openUrl":"/api/chat/media/outgoing/agent%3Amain%3Adiscord%3Adirect%3A1/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee/full","alt":"sample_morning_still_10am_9876543210.png","mimeType":"image/png","width":832,"height":1216,"sizeBytes":1021973}],
     "provider":"openclaw","model":"automation-result","timestamp":1700000100001,
     "openclawAutomation":{"kind":"cron","jobId":"0a1b2c3d-1111-2222-3333-444455556666","runId":"r2"}}
    """;

    internal const string OutgoingUrl = "/api/chat/media/outgoing/agent%3Amain%3Adiscord%3Adirect%3A1/abc-123/full";

    // FROM SOURCE.
    internal const string VideoSuccess = """
    {"role":"assistant","content":[
      {"type":"text","text":"a clip for you"},
      {"type":"video","artifactId":"artifact_managed_media_1","url":"/api/chat/media/outgoing/agent%3Amain%3Adiscord%3Adirect%3A1/abc-123/full","openUrl":"/api/chat/media/outgoing/agent%3Amain%3Adiscord%3Adirect%3A1/abc-123/full","fileName":"sample_clip.mp4","mimeType":"video/mp4","playback":"native","sizeBytes":1042813,"durationMs":12750,"width":544,"height":800}]}
    """;

    // FROM SOURCE.
    internal const string AudioSuccess = """
    {"role":"assistant","content":[
      {"type":"audio","artifactId":"artifact_managed_media_2","url":"/api/chat/media/outgoing/agent%3Amain%3Adiscord%3Adirect%3A1/def-456/full","fileName":"voice_note.m4a","mimeType":"audio/mp4","sizeBytes":240000,"durationMs":42000}]}
    """;

    // FROM SOURCE.
    internal const string DocumentSuccess = """
    {"role":"assistant","content":[
      {"type":"text","text":"the report"},
      {"type":"attachment","attachment":{"artifactId":"artifact_managed_media_3","url":"/api/chat/media/outgoing/agent%3Amain%3Adiscord%3Adirect%3A1/ghi-789/full","kind":"document","label":"quarterly-report.pdf","mimeType":"application/pdf","sizeBytes":1258291}}]}
    """;

    // FROM SOURCE.
    internal const string DocumentError = """
    {"role":"assistant","content":[
      {"type":"text","text":"the report"},
      {"type":"attachment_error","attachment":{"code":"unsupported-format","kind":"document","label":"quarterly-report.pdf","mimeType":"application/pdf"}}]}
    """;
}
