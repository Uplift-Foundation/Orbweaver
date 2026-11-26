// The routing decision, kept pure so it is tested without a Paperclip host.
//
// Paperclip calls the router once per new task with the pool's members that
// this agent may use, and pins the task to whatever it picks, so a session
// never changes account mid-task. Paperclip would also attach each member's
// usage, but only for credentials that can read the usage API, and its Claude
// sign-in (`claude setup-token`) grants user:inference only: the usage API
// answers 403 for every subscription it connects (Paperclip's own source says
// so, and all four accounts here measured permission_denied). So usage comes
// from the host instead: host/pool_usage.py asks each account's own Claude Code
// login and writes the answer to a file this worker reads.

import os from "node:os";
import path from "node:path";

/** Older than this, a published reading is treated as unknown. */
export const STALE_MS = 30 * 60 * 1000;
/** With nothing known about when any account frees up, look again after this. */
export const UNKNOWN_RETRY_MS = 15 * 60 * 1000;
/** Accounts within one band count as equally loaded and take turns. */
export const BAND = 10;

const FAMILIES = ["opus", "sonnet", "haiku", "fable"];

export function usagePath(env = process.env) {
  if (env.ORBWEAVER_POOL_USAGE) return env.ORBWEAVER_POOL_USAGE;
  const home = env.PAPERCLIP_HOME || path.join(os.homedir(), ".paperclip");
  return path.join(home, "instances", env.PAPERCLIP_INSTANCE_ID || "default", "pool-usage.json");
}

/** The limits that bind a member: account-wide windows, plus model-scoped ones
 * for its own model family. Overage is a payment setting, not a window. */
export function bindingLimits(limits, model) {
  const m = String(model || "").toLowerCase();
  return (limits || []).filter((l) => {
    if (!l || l.scope === "overage") return false;
    if (l.scope == null) return true;
    const s = String(l.scope).toLowerCase();
    return FAMILIES.some((f) => s.includes(f) && m.includes(f));
  });
}

/** The usage to judge a candidate by: Paperclip's own when it has some, else
 * the host's published reading for the member's connection if it is fresh. */
export function usageFor(candidate, published, now) {
  const own = candidate.usage;
  if (own && own.status === "ok" && Array.isArray(own.limits) && own.limits.length) return own;
  const id = candidate.member?.binding?.connectionId;
  const entry = published?.accounts?.[id];
  if (!entry || entry.status !== "ok" || !Array.isArray(entry.limits) || !entry.limits.length) return null;
  const checked = Date.parse(entry.checkedAt);
  if (!Number.isFinite(checked) || now - checked > STALE_MS || checked - now > STALE_MS) return null;
  return entry;
}

function blockingLimits(limits, threshold, hardOnly) {
  return limits.filter((l) => l.limitReached === true || l.allowed === false
    || (!hardOnly && typeof l.usedPercent === "number" && l.usedPercent >= threshold));
}

function describe(blocking, threshold) {
  return blocking.map((l) => `${l.label || l.id} ${typeof l.usedPercent === "number" ? l.usedPercent : "?"}%`)
    .join(", ") + ` (threshold ${threshold}%)`;
}

/** Position of each member in turn order, starting just after the last pick. */
function turnOrder(memberOrder, lastMemberId) {
  const order = memberOrder || [];
  const start = order.indexOf(lastMemberId) + 1;
  const rank = new Map();
  order.forEach((id, i) => rank.set(id, (i - start + order.length) % order.length));
  return (id) => (rank.has(id) ? rank.get(id) : order.length);
}

/**
 * The router's answer for one AiConnectionRouterRequest.
 *
 * - round_robin: the next permitted member after the last pick.
 * - usage_aware: members over the threshold on any binding window are skipped;
 *   of the rest, the least loaded (in BAND-point bands, taking turns within a
 *   band) wins; members with no usable reading come last but stay eligible.
 *   When every member is over, "exhausted" carries the earliest moment one of
 *   them frees up, so Paperclip waits rather than failing the task.
 * - A pinned task (one candidate, the pin) keeps its account until a window is
 *   actually reached; the threshold only steers new tasks.
 */
export function choose(req, published) {
  const now = Number.isFinite(Date.parse(req.now)) ? Date.parse(req.now) : Date.now();
  const candidates = req.candidates || [];
  if (!candidates.length) return { kind: "unavailable", skipped: {} };
  const rank = turnOrder(req.memberOrder, req.lastMemberId);
  const ids = candidates.map((c) => c.member.id);
  if (req.pool?.mode !== "usage_aware") {
    return { kind: "selected", memberId: ids.slice().sort((a, b) => rank(a) - rank(b))[0] };
  }
  const threshold = req.pool.thresholdPercent ?? 90;
  const pinned = Boolean(req.pinnedMemberId) && candidates.length === 1;
  const skipped = {};
  const open = [];
  let retry = null;
  for (const c of candidates) {
    const id = c.member.id;
    const usage = usageFor(c, published, now);
    if (!usage) {
      open.push({ id, load: null });
      continue;
    }
    const limits = bindingLimits(usage.limits, c.member.profile?.model);
    const blocking = blockingLimits(limits, threshold, pinned);
    if (blocking.length) {
      skipped[id] = describe(blocking, threshold);
      const resets = blocking.map((l) => Date.parse(l.resetsAt));
      if (resets.every(Number.isFinite)) {
        const free = Math.max(...resets);
        retry = retry === null ? free : Math.min(retry, free);
      }
      continue;
    }
    const used = limits.map((l) => l.usedPercent).filter((n) => typeof n === "number");
    open.push({ id, load: used.length ? Math.max(...used) : null });
  }
  if (!open.length) {
    const at = retry !== null && retry > now ? retry : now + UNKNOWN_RETRY_MS;
    return { kind: "exhausted", retryAt: new Date(at).toISOString(), skipped };
  }
  open.sort((a, b) => (a.load === null) - (b.load === null)
    || Math.floor((a.load ?? 0) / BAND) - Math.floor((b.load ?? 0) / BAND)
    || rank(a.id) - rank(b.id));
  return { kind: "selected", memberId: open[0].id, skipped };
}
