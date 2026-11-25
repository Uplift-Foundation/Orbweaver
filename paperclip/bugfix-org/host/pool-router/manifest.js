// The Paperclip plugin manifest (PLUGIN_SPEC §10.1). A connection-pool router
// needs exactly one capability; it reads nothing through the host but the
// candidates each routing request hands it.
export default {
  id: "orbweaver.pool-router",
  apiVersion: 1,
  version: "1.0.0",
  displayName: "Orbweaver pool router",
  description: "Routes each new task to the Claude subscription with the most headroom under the pool's threshold, from usage the host publishes (CB-468).",
  author: "Uplift Foundation",
  categories: ["automation"],
  capabilities: ["ai.connections.route"],
  aiConnectionRouter: {
    name: "Usage-aware (Orbweaver)",
    description: "Picks the account with the most headroom; skips any account at or over the threshold; waits for the earliest reset when all are over.",
  },
  entrypoints: { worker: "./worker.js" },
};
