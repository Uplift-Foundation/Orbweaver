namespace Orbweaver.Tests;

// CB-306. Real payload structure, measured 2026-11-03 on openclaw 2026.10.10 from
// the gateway host's own CLI (the Mac probe cannot reach the gateway). The
// commands are a nine-row cut of the 105 commands.list returned, rows verbatim
// bar a skill description shortened; the history is the real chat.history of a
// throwaway session after chat.send "/status", with the session id, auth name
// and ids replaced by placeholders.
internal static class OpenClawSlashFixtures
{
    internal const string CommandsList = """
{
 "commands": [
  {
   "name": "status",
   "nativeName": "status",
   "textAliases": [
    "/status"
   ],
   "description": "Show current status.",
   "category": "status",
   "source": "native",
   "scope": "both",
   "acceptsArgs": true
  },
  {
   "name": "openclaw",
   "textAliases": [
    "/openclaw"
   ],
   "description": "Run the OpenClaw setup and repair helper.",
   "category": "management",
   "source": "native",
   "scope": "text",
   "acceptsArgs": true
  },
  {
   "name": "whoami",
   "nativeName": "whoami",
   "textAliases": [
    "/whoami",
    "/id"
   ],
   "description": "Show your sender id.",
   "category": "status",
   "source": "native",
   "scope": "both",
   "acceptsArgs": false
  },
  {
   "name": "reset",
   "nativeName": "reset",
   "textAliases": [
    "/reset"
   ],
   "description": "Reset the current session.",
   "category": "session",
   "source": "native",
   "scope": "both",
   "acceptsArgs": true
  },
  {
   "name": "new",
   "nativeName": "new",
   "textAliases": [
    "/new"
   ],
   "description": "Start a new session.",
   "category": "session",
   "source": "native",
   "scope": "both",
   "acceptsArgs": true
  },
  {
   "name": "compact",
   "nativeName": "compact",
   "textAliases": [
    "/compact"
   ],
   "description": "Compact the session context.",
   "category": "session",
   "source": "native",
   "scope": "both",
   "acceptsArgs": true,
   "args": [
    {
     "name": "instructions",
     "description": "Extra compaction instructions",
     "type": "string"
    }
   ]
  },
  {
   "name": "think",
   "nativeName": "think",
   "textAliases": [
    "/think",
    "/thinking",
    "/t"
   ],
   "description": "Set thinking level.",
   "category": "options",
   "source": "native",
   "scope": "both",
   "acceptsArgs": true,
   "args": [
    {
     "name": "level",
     "description": "Thinking level",
     "type": "string",
     "dynamic": true
    }
   ]
  },
  {
   "name": "model",
   "nativeName": "model",
   "textAliases": [
    "/model"
   ],
   "description": "Show or set the model; use -s, -a, or -g to choose scope.",
   "category": "options",
   "source": "native",
   "scope": "both",
   "acceptsArgs": true,
   "args": [
    {
     "name": "model",
     "description": "Model id; add -s for session, -a for agent, or -g for global scope",
     "type": "string"
    }
   ]
  },
  {
   "name": "add_model_provider",
   "nativeName": "add_model_provider",
   "textAliases": [
    "/add_model_provider"
   ],
   "description": "Add and live-prove a model provider with non-interactive config one-liners, with",
   "category": "tools",
   "source": "skill",
   "scope": "both",
   "acceptsArgs": true,
   "skillDisplayName": "Add a model provider",
   "skillModelVisible": true
  }
 ]
}
""";

    internal const string StatusCardHistory = """
{
 "messages": [
  {
   "role": "user",
   "content": "/status",
   "timestamp": 1793734136761,
   "idempotencyKey": "cb306-example:user",
   "excludeFromContext": true,
   "__openclaw": {
    "transport": {
     "clients": [
      {
       "id": "cli",
       "mode": "cli"
      }
     ]
    },
    "contextFreeCommand": true,
    "recordTimestampMs": 1793734137716,
    "seq": 1,
    "id": "00000000-0000-0000-0000-000000000000"
   }
  },
  {
   "role": "assistant",
   "content": [
    {
     "type": "text",
     "text": "🦞 OpenClaw 2026.10.10 (7dc3350)\nCurrent time: Tuesday, November 3rd, 2026 - 11:28 AM (America/Los_Angeles)\nReference UTC: 2026-11-03 19:28 UTC\n⏱️ Uptime: gateway 2h 9m · system 34d 11h\n\n🧠 Model: anthropic/claude-opus-5-5\n🌐 Endpoint: unknown\n🔑 Auth: token (anthropic:example)\n🔄 Fallbacks: anthropic/claude-sonnet-5-5, openai/gpt-5.6-terra, xai/grok-4.3\n\n📚 Context: ?/1.0m\n\n🧵 Session: agent:main:dashboard:00000000-example • duration 1s • updated just now\n\n⚙️ Execution: direct\n🤖 Runtime: Claude CLI\n🎛️ Modes: think medium · fast off\n🪢 Queue: steer"
    }
   ],
   "timestamp": 1793734137720,
   "stopReason": "stop",
   "usage": {
    "input": 0,
    "output": 0,
    "totalTokens": 0,
    "cacheRead": 0,
    "cacheWrite": 0,
    "cost": {
     "input": 0,
     "output": 0,
     "cacheRead": 0,
     "cacheWrite": 0,
     "total": 0
    }
   },
   "api": "openai-responses",
   "provider": "openclaw",
   "model": "gateway-injected",
   "idempotencyKey": "cb306-example:user",
   "excludeFromContext": true,
   "__openclaw": {
    "contextFreeCommand": true,
    "recordTimestampMs": 1793734137720,
    "seq": 2,
    "id": "00000000-0000-0000-0000-000000000000"
   }
  }
 ]
}
""";
}
