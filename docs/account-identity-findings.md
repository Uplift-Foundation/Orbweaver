# Account identity (CB-340): what was measured

One usage card per account across every PC needs a value that is the same for
one account on every machine and reveals nothing about it. This records where
that value comes from and, as `docs/codex-findings.md` does, keeps what was
**read off a real machine** apart from what is **assumed**. Every value in the
fixtures under `tests/UnitTests` is fabricated; only key names and value shapes
come from real files.

## Confirmed

Read 16 Nov 2026 on the machine this was built on (macOS, one user), by a
script that printed **key names, JSON types and shape tests only** — never a
value. Nothing was written, no token was refreshed, no CLI was run.

**Claude Code** — `<config dir or home>/.claude.json`, the file `UsagePoller`
already reads for the label.

- Three profiles (`~/.claude.json`, `~/.claude-loginid`, `~/.claude-board`)
  carry an `oauthAccount` object whose `accountUuid` and `organizationUuid`
  both match the UUID shape.
- `~/.claude-work` has **no** `oauthAccount` at all: a profile that was never
  logged in. That is an ordinary state, so it has no account id and is never
  merged.
- Beside those two ids the object holds `emailAddress`, `displayName`,
  `fullName`, `organizationName`, role and tier fields. Only the two ids are
  read for identity.

**Codex** — `~/.codex/auth.json`.

- Top level: `auth_mode`, `OPENAI_API_KEY` (null in the ChatGPT login),
  `tokens`, `last_refresh`. **No top-level email**, which is why
  `CodexUsageAccounts.LabelFrom` never found one here.
- `tokens`: `id_token`, `access_token`, `refresh_token`, `account_id`.
  `account_id` matches the UUID shape.
- The `id_token` payload (middle base64url segment, JSON) holds `email`, `sub`,
  and a nested object under the key `https://api.openai.com/auth` with
  `chatgpt_account_id` (a UUID), `chatgpt_user_id`, `user_id`, plan and group
  fields. **`chatgpt_user_id` is nested there, not at the top level of the
  payload** — which settles the question frames-v12 left open. Both places are
  still read, nested second, so a different nesting on another version degrades
  to "no second id" rather than to nothing.
- `chatgpt_user_id` is a string of the form `user-` plus 24 characters; it is
  not a UUID, so the identity code does not assume one.

**Grok** — `~/.grok/auth.json`.

- One entry under a long key. It holds `user_id`, `principal_id`, `team_id`
  (UUID shapes), `email`, `refresh_token`, `key` and OIDC fields.
- `GrokUsageAccounts.LabelFrom` takes the first object-valued entry that has an
  email; the identity reader takes the ids from the same entry.

## The derivation

`account_id` = lowercase hex of the first 16 bytes of
`HMAC-SHA256(key = "orbweaver/account-id/v1", message = cli + "\n" + id1 + "\n" + id2)`,
ids trimmed of ASCII whitespace and lowercased.

| CLI | id1 | id2 |
| --- | --- | --- |
| claude | `oauthAccount.accountUuid` | `oauthAccount.organizationUuid` |
| codex | `tokens.account_id` | `chatgpt_user_id` claim of `tokens.id_token` |
| grok | `user_id` | `team_id` |

- **Never an email, a display name or a token.** An id1 that is missing, blank
  or contains `@` yields no id and the account is never merged.
- **The key is a domain separator, not a secret.** PCs share no secret and the
  id must be equal on all of them. Non-reversibility rests on the provider ids
  being random, which is why nothing here claims the key protects them.
- **Every available id goes into the message**, so when a CLI offers less on one
  machine than on another the failure is two cards for one account, never one
  card for two. A Claude account with no `organizationUuid`, or a Codex account
  whose `id_token` does not decode, gets a *different* id from the same account
  with both — covered by `AccountIdentityTests`.
- The reference vectors (`tests/UnitTests/Fixtures/relay/frames-v12-vectors.json`)
  derive the same ids this code does, so a second implementation can be checked
  against them.

## Codex `tokens`, and why reading two strings is allowed now

`docs/codex-findings.md` and `CodexUsageAccounts.LabelFrom` said the usage orb
must not read `auth.json`'s `tokens`. The reason is real: those tokens rotate,
and a second reader that loses a compare-and-swap logs the user out. That is an
argument against *refreshing or writing* a rotating token. Reading two
identifier strings out of the file touches no token's lifecycle.

So `AccountIdentity.Codex` reads exactly `tokens.account_id` and the
`chatgpt_user_id` claim, read-only, and **nothing from `tokens` is logged,
stored or sent** — only the HMAC leaves the desktop. `access_token`,
`refresh_token` and `OPENAI_API_KEY` stay unread, and `LabelFrom` still skips
`tokens` because the label never needed them. A test pins that the id does not
move when those secrets do.

## Not verified — do not write these down as facts until they are

- **The same key names on the Mac mini and the Windows PC.** Only one machine's
  files were read. A key-name-only capture over ssh on each is still to do, and
  it is the cheapest way to turn this into a confirmed claim on all three.
- **That one Claude account gives one `account_id` on all three PCs.** It
  should, because the ids come from the provider, but nothing has compared them
  across machines. The live check is the desktop's verbose log on each PC
  (cli plus the first six hex only).
- **Whether `chatgpt_user_id` is stable across a re-login** and whether Codex
  and Grok limits are per user or per team. The id is conservative either way:
  the failure is two cards, not a wrong merge.
- **That `resets_at` agrees across PCs** for one account (the phone tolerates
  120 s of difference; not measured).
- **Older Claude Code builds**: nothing here checks that `oauthAccount` always
  carries `accountUuid` on versions older than the one measured.
