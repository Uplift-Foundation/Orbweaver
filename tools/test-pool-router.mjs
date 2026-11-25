// Tests for the bug-fix org's connection-pool router (CB-468):
// paperclip/bugfix-org/host/pool-router/choose.js.
//
//     node --test tools/test-pool-router.mjs
//
// tools/test-paperclip-bugfix-org.py runs this too, so CI covers it.
import test from "node:test";
import assert from "node:assert/strict";
import { choose, bindingLimits, usageFor, usagePath, STALE_MS, UNKNOWN_RETRY_MS }
  from "../paperclip/bugfix-org/host/pool-router/choose.js";

const NOW = "2026-11-25T04:30:00.000Z";
const T = Date.parse(NOW);
const iso = (ms) => new Date(T + ms).toISOString();
const H = 3600_000;

function member(id, model = "claude-sonnet-5-5") {
  return { id, binding: { provider: "anthropic", method: "subscription", mode: "shared",
    connectionId: `conn-${id}`, grantId: `grant-${id}` }, profile: { provider: "acpx", acpxAgent: "claude", model } };
}
const cand = (id, model) => ({ member: member(id, model), runtimeConfig: {}, notes: [] });
function limits(five, week, extra = []) {
  return [
    { id: "five_hour", label: "5-hour", scope: null, usedPercent: five, resetsAt: iso(2 * H) },
    { id: "seven_day", label: "Weekly", scope: null, usedPercent: week, resetsAt: iso(30 * H) },
    ...extra,
  ];
}
function pub(map, checkedAt = NOW) {
  const accounts = {};
  for (const [id, l] of Object.entries(map)) accounts[`conn-${id}`] = { status: "ok", checkedAt, limits: l };
  return { version: 1, writtenAt: checkedAt, accounts };
}
function req(ids, over = {}) {
  return { companyId: "c", agentId: "a", taskKey: "t", lastMemberId: null, cursorVersion: 0,
    pool: { name: "p", mode: "usage_aware", thresholdPercent: 90, enabled: true, members: [] },
    memberOrder: ids, candidates: ids.map((id) => cand(id)), now: NOW, ...over };
}

test("the least loaded account wins", () => {
  const r = choose(req(["a", "b", "c"]), pub({ a: limits(30, 80), b: limits(10, 20), c: limits(50, 60) }));
  assert.deepEqual(r, { kind: "selected", memberId: "b", skipped: {} });
});

test("an account at or over the threshold on any window is skipped, and says why", () => {
  const r = choose(req(["a", "b"]), pub({ a: limits(13, 99), b: limits(70, 85) }));
  assert.equal(r.kind, "selected");
  assert.equal(r.memberId, "b");
  assert.match(r.skipped.a, /Weekly 99% \(threshold 90%\)/);
  const at = choose(req(["a", "b"]), pub({ a: limits(90, 10), b: limits(89, 89) }));
  assert.equal(at.memberId, "b", "exactly the threshold counts as over");
});

test("negative control: the same account is chosen once its reading is under the threshold", () => {
  const over = choose(req(["a", "b"]), pub({ a: limits(13, 99), b: limits(60, 60) }));
  const under = choose(req(["a", "b"]), pub({ a: limits(13, 40), b: limits(60, 60) }));
  assert.equal(over.memberId, "b");
  assert.equal(under.memberId, "a");
});

test("accounts in the same band take turns after the last pick", () => {
  const p = pub({ a: limits(21, 22), b: limits(23, 25), c: limits(20, 28) });
  assert.equal(choose(req(["a", "b", "c"]), p).memberId, "a");
  assert.equal(choose(req(["a", "b", "c"], { lastMemberId: "a" }), p).memberId, "b");
  assert.equal(choose(req(["a", "b", "c"], { lastMemberId: "c" }), p).memberId, "a");
});

test("when every account is over, the pool waits for the earliest one to free up", () => {
  const a = [{ id: "five_hour", scope: null, usedPercent: 95, resetsAt: iso(2 * H) },
             { id: "seven_day", scope: null, usedPercent: 92, resetsAt: iso(40 * H) }];
  const b = [{ id: "five_hour", scope: null, usedPercent: 99, resetsAt: iso(3 * H) },
             { id: "seven_day", scope: null, usedPercent: 10, resetsAt: iso(40 * H) }];
  const r = choose(req(["a", "b"]), pub({ a, b }));
  assert.equal(r.kind, "exhausted");
  assert.equal(r.retryAt, iso(3 * H), "a needs both windows back (40h); b only its 5-hour (3h)");
  assert.deepEqual(Object.keys(r.skipped).sort(), ["a", "b"]);
});

test("an over account with an unknown reset falls back to a fixed wait", () => {
  const a = [{ id: "seven_day", scope: null, usedPercent: 99, resetsAt: null }];
  const r = choose(req(["a"]), pub({ a }));
  assert.equal(r.kind, "exhausted");
  assert.equal(Date.parse(r.retryAt), T + UNKNOWN_RETRY_MS);
});

test("an account with no usable reading ranks last but is still used", () => {
  const p = pub({ a: limits(80, 85) });
  assert.equal(choose(req(["a", "b"]), p).memberId, "a", "known headroom beats unknown");
  assert.equal(choose(req(["a", "b"]), pub({ a: limits(95, 10) })).memberId, "b", "unknown beats over");
  assert.equal(choose(req(["a", "b"]), null).memberId, "a", "no file at all still routes");
});

test("a stale, failed or future-dated reading is unknown", () => {
  for (const p of [pub({ a: limits(95, 95) }, iso(-STALE_MS - 1)), pub({ a: limits(95, 95) }, iso(STALE_MS + 1)),
                   { accounts: { "conn-a": { status: "error", checkedAt: NOW, limits: limits(95, 95) } } }]) {
    assert.equal(choose(req(["a"]), p).kind, "selected");
    assert.equal(usageFor(cand("a"), p, T), null);
  }
});

test("Paperclip's own usage is used when it has some", () => {
  const c = cand("a");
  c.usage = { status: "ok", limits: limits(95, 10) };
  const r = choose({ ...req(["a", "b"]), candidates: [c, cand("b")] }, pub({ a: limits(1, 1), b: limits(50, 50) }));
  assert.equal(r.memberId, "b");
});

test("model-scoped windows bind only their own model family; overage never binds", () => {
  const l = limits(10, 10, [
    { id: "seven_day_opus", label: "Opus weekly", scope: "seven_day_opus", usedPercent: 97, resetsAt: iso(H) },
    { id: "extra_usage", scope: "overage", usedPercent: 100, resetsAt: null }]);
  assert.deepEqual(bindingLimits(l, "claude-sonnet-5-5").map((x) => x.id), ["five_hour", "seven_day"]);
  assert.deepEqual(bindingLimits(l, "claude-opus-5-5").map((x) => x.id), ["five_hour", "seven_day", "seven_day_opus"]);
  const opus = req(["a", "b"], { candidates: [cand("a", "claude-opus-5-5"), cand("b", "claude-opus-5-5")] });
  assert.equal(choose(opus, pub({ a: l, b: limits(40, 40) })).memberId, "b");
  assert.equal(choose(req(["a", "b"]), pub({ a: l, b: limits(40, 40) })).memberId, "a");
});

test("a pinned task keeps its account past the threshold until a window is reached", () => {
  const pinned = req(["a"], { pinnedMemberId: "a" });
  assert.equal(choose(pinned, pub({ a: limits(10, 95) })).kind, "selected");
  const reached = [{ id: "seven_day", scope: null, usedPercent: 100, limitReached: true, resetsAt: iso(5 * H) }];
  const r = choose(pinned, pub({ a: reached }));
  assert.equal(r.kind, "exhausted");
  assert.equal(r.retryAt, iso(5 * H));
});

test("round_robin ignores usage and rotates", () => {
  const p = pub({ a: limits(99, 99), b: limits(1, 1) });
  const rr = (last) => choose(req(["a", "b", "c"], { lastMemberId: last, pool: { mode: "round_robin" } }), p);
  assert.equal(rr(null).memberId, "a");
  assert.equal(rr("a").memberId, "b");
  assert.equal(rr("c").memberId, "a");
});

test("no candidates is unavailable", () => {
  assert.deepEqual(choose(req([]), null), { kind: "unavailable", skipped: {} });
});

test("the usage file defaults into Paperclip's instance folder", () => {
  assert.equal(usagePath({ PAPERCLIP_HOME: "/p", PAPERCLIP_INSTANCE_ID: "x" }), "/p/instances/x/pool-usage.json");
  assert.equal(usagePath({ ORBWEAVER_POOL_USAGE: "/o.json" }), "/o.json");
  assert.match(usagePath({}), /\.paperclip\/instances\/default\/pool-usage\.json$/);
});

test("the package is a Paperclip router plugin the host will accept", async () => {
  const { readFile, access } = await import("node:fs/promises");
  const root = new URL("../paperclip/bugfix-org/host/pool-router/", import.meta.url);
  const pkg = JSON.parse(await readFile(new URL("package.json", root), "utf8"));
  assert.equal(pkg.type, "module");
  const manifest = (await import(new URL(pkg.paperclipPlugin.manifest, root))).default;
  // The rules Paperclip's pluginManifestV1Schema enforces on these fields.
  assert.match(manifest.id, /^[a-z0-9][a-z0-9._-]*$/);
  assert.equal(manifest.apiVersion, 1);
  assert.match(manifest.version, /^\d+\.\d+\.\d+$/);
  assert.ok(manifest.description.length <= 500 && manifest.displayName.length <= 100);
  assert.ok(["connector", "workspace", "automation", "ui"].includes(manifest.categories[0]));
  assert.deepEqual(manifest.capabilities, ["ai.connections.route"]);
  assert.ok(manifest.aiConnectionRouter.name.length <= 100 && manifest.aiConnectionRouter.description.length <= 500);
  await access(new URL(manifest.entrypoints.worker, root));
});
