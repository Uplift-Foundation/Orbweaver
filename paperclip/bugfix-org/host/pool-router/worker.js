// The plugin worker Paperclip runs (entrypoints.worker). It reads the host's
// published usage on every request rather than caching it: a request is rare
// (one per new task) and the file is small, and a stale cache is exactly the
// failure a usage-aware router exists to avoid.
import { readFile } from "node:fs/promises";
import { definePlugin, runWorker } from "@paperclipai/plugin-sdk";
import { choose, usagePath } from "./choose.js";

let log = null;

async function published() {
  try {
    return JSON.parse(await readFile(usagePath(), "utf8"));
  } catch {
    return null; // no file yet, or mid-write: every member is "unknown", which still routes
  }
}

const plugin = definePlugin({
  async setup(ctx) {
    log = ctx.logger;
  },
  async onRouteAiConnection(req) {
    const result = choose(req, await published());
    log?.info("pool route", { pool: req.pool?.name, agentId: req.agentId, result: result.kind,
      memberId: result.memberId, skipped: result.skipped });
    return result;
  },
});

export default plugin;
runWorker(plugin, import.meta.url);
