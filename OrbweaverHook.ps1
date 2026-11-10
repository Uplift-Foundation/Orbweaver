param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('idle', 'generating', 'waiting', 'ended')]
    [string]$State,

    # Which CLI is asking. The bash twin takes this as a bare leading word
    # because a shell command line is what it is written into; here it is a
    # named parameter for the same reason everything else is, and the installers
    # bake it in at wiring time.
    #
    # Almost nothing below cares. Finding the terminal, finding the session's
    # process and writing the status file are the same job whoever asked.
    [ValidateSet('claude', 'codex', 'grok')]
    [string]$Agent = 'claude',

    # Baked in as a literal by install-windows-hooks.ps1 at wiring time,
    # computed there in a normal, full environment -- not re-derived here,
    # where a WSL-interop-launched invocation's environment can't be trusted
    # to have TEMP/TMP set at all (already known to omit PATH; found to omit
    # TEMP too on a real machine, which silently pointed this script at an
    # unrelated relative-path folder with no visible error). Falls back to
    # GetTempPath() for anyone who wired this by hand via the README's JSON
    # snippets, where it isn't passed.
    [string]$TempDir = ''
)

$ErrorActionPreference = 'SilentlyContinue'

$sessionId = 'unknown'
$cwd = ''
$transcript = ''
try {
    $payload = [Console]::In.ReadToEnd() | ConvertFrom-Json
    if ($payload.session_id) { $sessionId = $payload.session_id }
    elseif ($payload.sessionId) { $sessionId = $payload.sessionId }
    if ($payload.cwd) { $cwd = $payload.cwd }
    if ($payload.transcript_path) { $transcript = $payload.transcript_path }
    elseif ($payload.transcriptPath) { $transcript = $payload.transcriptPath }
} catch {}

# Grok injects these on every hook, including ones loaded from Claude Code's
# settings.json. That is a stronger signal than -Agent: a Grok session that
# reached this script as claude is still a Grok session.
if ($env:GROK_SESSION_ID -or $env:GROK_HOOK_EVENT) {
    $Agent = 'grok'
    if ($sessionId -eq 'unknown' -and $env:GROK_SESSION_ID) {
        $sessionId = $env:GROK_SESSION_ID
    }
    if (-not $cwd -and $env:GROK_WORKSPACE_ROOT) {
        $cwd = $env:GROK_WORKSPACE_ROOT
    }
}

# What the chat is called and what color it's been given. Claude Code keeps
# all three of these in the transcript, re-emitting them as the session goes:
#   {"type":"custom-title","customTitle":"claude-buddy",...}   <- /rename
#   {"type":"ai-title","aiTitle":"Package app with a tray",...} <- auto-named
#   {"type":"agent-color","agentColor":"green",...}             <- /color
# A name set with /rename wins over the generated one regardless of which was
# written last; a session with neither falls back to the directory name.
#
# WSL sessions land here with a Linux transcript path this script can't read,
# so they keep the folder-name fallback -- see the platform notes in README.
$title = ''
$color = ''
if ($Agent -eq 'claude' -and $State -ne 'ended' -and $transcript -and (Test-Path $transcript)) {
    try {
        # Read the tail first: transcripts reach tens of MB and this runs on
        # every tool call. Only scan the whole file when a long run of tool
        # output has pushed all three records out of that window.
        #
        # The tail is the last 256KB by byte offset, the same window the bash
        # twin takes with `tail -c 262144` -- NOT Get-Content -Tail, which this
        # used to be. Windows PowerShell 5.1's -Tail walks backwards hunting
        # for line breaks and goes pathological on long lines: measured on a
        # real 5.1MB transcript whose longest line was ~1.5M characters (an
        # inline image), `-Tail 400` was still running after 200 seconds, so
        # every hook for that session hit Claude Code's 30s timeout. A byte
        # seek costs the same whatever the lines look like. The partial line
        # the seek lands in can't start with `{"type":`, so it never matches.
        #
        # Explicit UTF-8 is load-bearing on 5.1, which otherwise reads these
        # transcripts as the ANSI codepage and turns an accented letter (an
        # e-acute, say) into two garbage characters. PowerShell 7 already
        # defaults to UTF-8; being explicit is correct on both.
        #
        # ReadWrite sharing because Claude Code may hold this file open for
        # appending; File.ReadLines asks for exclusive-write and can be
        # refused, which is why the whole-file fallback reuses this stream.
        $pattern = '^\{"type":"(custom-title|ai-title|agent-color)"'
        $stream = [System.IO.File]::Open($transcript, 'Open', 'Read', 'ReadWrite')
        try {
            [void]$stream.Seek([math]::Max(0, $stream.Length - 262144), 'Begin')
            $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8)
            $meta = $reader.ReadToEnd() -split "`r?`n" | Where-Object { $_ -match $pattern }
            if (-not $meta) {
                [void]$stream.Seek(0, 'Begin')
                $reader.DiscardBufferedData()
                $meta = @(while (($l = $reader.ReadLine()) -ne $null) {
                    if ($l -match $pattern) { $l }
                })
            }
        } finally { $stream.Dispose() }

        $newest = {
            param($type)
            $meta | Where-Object { $_.StartsWith('{"type":"' + $type + '"') } | Select-Object -Last 1
        }

        $line = & $newest 'custom-title'
        if ($line) { $title = ($line | ConvertFrom-Json).customTitle }
        if (-not $title) {
            $line = & $newest 'ai-title'
            if ($line) { $title = ($line | ConvertFrom-Json).aiTitle }
        }

        $line = & $newest 'agent-color'
        if ($line) { $color = ($line | ConvertFrom-Json).agentColor }
    } catch {}
}

# ORBWEAVER_STATUS_ROOT first, then the baked -TempDir, then GetTempPath():
# the order the app's StatusDirectory.Root uses, so a test that hands the app
# a private status directory through that seam hands this script the same one
# (CB-306). -TempDir stays baked, per the param block: Windows's per-user temp
# is stable across reboots, which is what lets the installer compute it once.
# macOS cannot do that -- its per-user directory carries a per-boot token -- so
# the bash twin asks the C library on every call instead.
$statusRoot = $env:ORBWEAVER_STATUS_ROOT
$resolvedTempDir = if ($statusRoot) { $statusRoot } elseif ($TempDir) { $TempDir } else { [System.IO.Path]::GetTempPath() }
$dir = Join-Path $resolvedTempDir 'orbweaver'
if (-not (Test-Path $dir)) {
    New-Item -ItemType Directory -Path $dir | Out-Null
}

$file = Join-Path $dir "$sessionId.txt"

if ($State -eq 'ended') {
    Remove-Item -Path $file -Force
    exit 0
}

# Identify the terminal hosting this session so a click on the orb can jump
# to it. Windows Terminal advertises itself via WT_SESSION (which flows
# through WSL too, via WSLENV); VS Code's integrated terminal sets
# TERM_PROGRAM. For native sessions, walk up the parent process chain to
# the first process that owns a top-level window -- that's the terminal
# (WindowsTerminal.exe, Code.exe, the conhost shell, ...). The walk finds
# nothing for WSL sessions (the Windows-side parent is an interop bridge,
# not the terminal), which is what the term_program fallback is for.
#
# term_id is "the handle this terminal understands", and on Windows only
# WezTerm has one -- the console route needs nothing but the session's own pid,
# which is why Windows Terminal, conhost and VS Code all work without a line
# here. WEZTERM_PANE is recorded because `wezterm cli send-text --pane-id`
# reaches the exact pane, where the console route reaches the exact process:
# both are right, and the pane one costs no console juggling.
$termProgram = ''
$termId = ''
if ($env:WT_SESSION) { $termProgram = 'WindowsTerminal' }
elseif ($env:TERM_PROGRAM) { $termProgram = $env:TERM_PROGRAM }

if ($env:WEZTERM_PANE) {
    $termId = $env:WEZTERM_PANE
    if (-not $termProgram) { $termProgram = 'WezTerm' }
}

# Give this session a colour when it has none, the way each CLI allows.
#
# The bash twin has the reasoning in full. In short: for Claude Code this writes
# the record /color writes and Claude Code reads back, so the colour is real
# rather than a stand-in; for Codex there is no per-session colour to write, and
# nothing displays one either, so a derived colour disagrees with nothing.
#
# Keyed on the working directory so a project keeps one colour across sessions
# and across both CLIs. Windows has no cksum, so the hash is computed here -- the
# same arithmetic, over the same bytes, giving the same answer as the bash side.
# The marker the app writes beside the status files, for the reason the bash
# twin gives: a flag in the hook command would rewrite Codex's hooks.json and
# cost the user their hook trust every time the setting was toggled.
$autoColor = Test-Path (Join-Path $dir '.auto-color')
if ($autoColor -and -not $color -and $cwd) {
    try {
        # cksum's CRC-32, so a directory gets the same colour on either
        # platform. Table-free: 32 bits, one byte at a time, then the length,
        # which is what the POSIX definition does.
        function Get-CksumCrc([string]$text) {
            $bytes = [System.Text.Encoding]::UTF8.GetBytes($text)
            $crc = [uint32]0
            foreach ($b in $bytes) {
                $crc = $crc -bxor ([uint32]$b -shl 24)
                for ($i = 0; $i -lt 8; $i++) {
                    if ($crc -band 0x80000000) { $crc = (($crc -shl 1) -bxor 0x04C11DB7) -band 0xFFFFFFFF }
                    else { $crc = ($crc -shl 1) -band 0xFFFFFFFF }
                }
            }
            $len = $bytes.Length
            while ($len -gt 0) {
                $crc = $crc -bxor ([uint32]($len -band 0xFF) -shl 24)
                for ($i = 0; $i -lt 8; $i++) {
                    if ($crc -band 0x80000000) { $crc = (($crc -shl 1) -bxor 0x04C11DB7) -band 0xFFFFFFFF }
                    else { $crc = ($crc -shl 1) -band 0xFFFFFFFF }
                }
                $len = [math]::Floor($len / 256)
            }
            return (-bnot $crc) -band 0xFFFFFFFF
        }

        $names = @('red','orange','yellow','green','teal','cyan','blue','purple','violet','magenta','pink')
        $picked = $names[(Get-CksumCrc $cwd) % $names.Length]

        if ($Agent -eq 'claude' -and $transcript -and (Test-Path $transcript)) {
            # The same single-line append the bash hook makes, and the same
            # record /color writes. UTF-8 without a BOM, appended, for the
            # reasons the status-file write below gives.
            $record = '{"type":"agent-color","agentColor":"' + $picked + '","sessionId":"' + $sessionId + '"}'
            [System.IO.File]::AppendAllText($transcript, $record + "`n", (New-Object System.Text.UTF8Encoding($false)))
            $color = $picked
        }
        elseif ($Agent -eq 'codex' -or $Agent -eq 'grok') {
            # Codex and Grok have no per-session colour to write. Derive into
            # the status file only -- never append a Claude Code agent-color
            # record into their transcripts.
            $color = $picked
        }
    } catch {}
}

$termPid = 0
# The CLI process itself, recorded so the app can tell a running session from
# a status file left behind by one that never exited cleanly (Ctrl+C fires no
# SessionEnd). See SessionManager.SessionGone.
#
# Found by walking up for the first ancestor belonging to the CLI this hook
# speaks for, NOT by taking this script's immediate parent -- which is what
# this did, on the assumption that "Claude Code spawns the hook directly".
# It does not, on Windows: the hook command runs through a short-lived shell,
# so the immediate parent is that shell, and it exits the moment the hook
# does. Measured on a real machine -- successive hook writes for one live
# session recorded 54076, then 83076, both already dead, while the session's
# actual claude.exe sat at 36804 the whole time. The app reads a dead
# session_pid as "Ctrl+C'd without a SessionEnd" and suppresses the orb, so
# every live session on Windows went invisible while a three-day-old status
# file with no session_pid at all kept its orb.
#
# Nothing to do with the Unix hook, which asks a different question entirely
# (first ancestor owning a real tty) and was never wrong this way.
#
# The walk is the one that was already here looking for term_pid: claude.exe
# owns no top-level window of its own, so it is always passed on the way up to
# the terminal that does, and both answers come out of a single climb.
#
# Left at 0 when no claude ancestor is found, which is the safe direction: the
# app treats an unrecorded pid as "can't check" and keeps the orb on the
# lifetime rule, rather than hiding a session that is really running. That is
# also what the WSL case now gets, where the Windows-side chain dead-ends in an
# interop bridge and previously recorded a doomed pid.
$sessionPid = 0
$codexDaemon = $false
try {
    $cur = Get-CimInstance Win32_Process -Filter "ProcessId=$PID"
    for ($i = 0; $i -lt 10 -and $cur; $i++) {
        $parentId = $cur.ParentProcessId
        if (-not $parentId) { break }
        $proc = Get-Process -Id $parentId -ErrorAction Stop
        if (-not $proc) { break }

        $cur = Get-CimInstance Win32_Process -Filter "ProcessId=$parentId"

        # Tested here, after advancing $cur and before the window check below,
        # because the name and command line come off the CIM object and the
        # window check can break out of the loop. Costs one extra CIM query in
        # the iteration that finds the terminal; a hook runs a handful of them
        # already.
        #
        # `node` with claude on its command line covers an npm-style install,
        # where the session is node running the CLI rather than a claude.exe.
        # `claude.exe*` rather than an exact match: Claude Code self-updates by
        # renaming the running binary aside, leaving names like
        # claude.exe.old.1786153043553 on disk. Observed on this machine -- CIM's
        # Name still reported the stable "claude.exe" for the live process while
        # Get-Process reported the renamed image, so an exact match happens to
        # work today, but which API reports which name is not worth depending on
        # when a session outliving an update is entirely ordinary.
        #
        # Which name counts as "the session" is whichever CLI this hook speaks
        # for, not claude unconditionally. Hard-coding claude here made every
        # Codex orb on Windows impossible: the walk found no claude ancestor,
        # left session_pid at 0, and SessionManager drops a Codex file naming
        # no process on sight -- deliberately, because for Codex that means a
        # session that ended without clearing up. Two rules each right on their
        # own, combining into a session that could never be shown. Observed on
        # a real chain: powershell -> pwsh -> codex.exe -> node.exe -> sh.exe.
        #
        # The node.exe arm matches on $Agent for the same reason it exists at
        # all -- an npm-style install puts the CLI's name on node's command line
        # rather than in the image name, and both CLIs ship that way.
        if ($sessionPid -eq 0 -and $cur) {
            $name = "$($cur.Name)"
            $commandLine = "$($cur.CommandLine)"

            # Claude's viewer and background-spare processes are not the
            # session that fired this hook. Keeping either makes a dispatched
            # agent claim the viewer's pid, so its orb later focuses the wrong
            # session. CIM sees argv even though Get-Process does not, which is
            # why this belongs here rather than in a name-only predicate.
            $isClaudeWrapper = $Agent -eq 'claude' -and
                $commandLine -match '(?i)(bg-pty-host|daemon run|--bg-spare|\sagents(?:\s|$))'

            # Codex 0.167+ runs turns, and so hooks, in a shared
            # `codex app-server` daemon (CB-316, measured on macOS): the first
            # codex ancestor is the daemon, not the session's TUI, and the
            # windowed ancestor above it is whichever console first started the
            # daemon. Resolved after the walk instead of recorded here.
            $isCodexDaemon = $Agent -eq 'codex' -and $commandLine -match '(?i)\sapp-server(\s|$)'
            if ($isCodexDaemon) { $codexDaemon = $true; break }

            if (-not $isClaudeWrapper -and
                ($name -like "$Agent.exe*" -or
                 ($name -eq 'node.exe' -and $commandLine -match $Agent))) {
                $sessionPid = [int]$parentId
            }
        }

        if ($proc.MainWindowHandle -ne 0) { $termPid = [int]$parentId; break }
    }
} catch {}

# Under the Codex daemon nothing terminal-shaped in this environment is the
# session's, so all of it is dropped. Windows cannot read another process's
# working directory, so the session's TUI is found by uniqueness instead of by
# folder: exactly one codex TUI (not an app-server) running is taken as the
# session, and its own windowed ancestor as the terminal. Two or more record
# no pid and no terminal, which the app refuses to type into -- the safe
# direction. The residual risk (one TUI plus a session from a non-TUI client)
# is named in CB-316's PR.
if ($codexDaemon) {
    $termPid = 0; $termProgram = ''; $termId = ''; $sessionPid = 0
    try {
        $tuis = @(Get-CimInstance Win32_Process -Filter "Name like 'codex%'" |
            Where-Object { "$($_.Name)" -like 'codex.exe*' -and "$($_.CommandLine)" -notmatch '(?i)\sapp-server(\s|$)' })
        if ($tuis.Count -eq 1) {
            $sessionPid = [int]$tuis[0].ProcessId
            $cur = $tuis[0]
            for ($i = 0; $i -lt 10 -and $cur; $i++) {
                $p = Get-Process -Id $cur.ProcessId -ErrorAction SilentlyContinue
                if ($p -and $p.MainWindowHandle -ne 0) { $termPid = [int]$cur.ProcessId; break }
                if (-not $cur.ParentProcessId) { break }
                $cur = Get-CimInstance Win32_Process -Filter "ProcessId=$($cur.ParentProcessId)"
            }
        }
    } catch {}
}

# What Codex calls this chat.
#
# On macOS the hook reads this out of Codex's own state database, where
# /rename's name and Codex's generated title both live. Windows has no sqlite
# client to read it with -- Windows 11 ships winsqlite3.dll but no sqlite3.exe,
# and PowerShell has no built-in provider -- so this takes the same first
# message that Codex builds its own title from, out of the rollout.
#
# The practical difference is that /rename does not reach a Windows orb. That
# is a smaller gap than it sounds: it matches what a Claude Code session under
# WSL already does, and both fall back to something true rather than to
# nothing. It is written down in the README's platform notes rather than left
# to be discovered.
if ($Agent -eq 'codex' -and $State -ne 'ended') {
    try {
        # Codex's transcript_path is nullable, so resolve the rollout by the
        # session id its filename ends with when the payload omits it.
        if (-not $transcript) {
            $codexHome = $env:CODEX_HOME
            if (-not $codexHome) { $codexHome = Join-Path $env:USERPROFILE '.codex' }
            $sessions = Join-Path $codexHome 'sessions'
            if (Test-Path $sessions) {
                $match = Get-ChildItem -Path $sessions -Recurse -Filter "rollout-*-$sessionId.jsonl" |
                    Select-Object -First 1
                if ($match) { $transcript = $match.FullName }
            }
        }

        if ($transcript -and (Test-Path $transcript)) {
            # A UserMessage is within the first handful of rows, so this reads
            # the head of the file rather than the file -- which matters, since
            # a rollout row carrying command output can reach a megabyte on its
            # own.
            $head = Get-Content -Path $transcript -TotalCount 40 -Encoding UTF8
            foreach ($line in $head) {
                if ($line -notmatch '"type":"UserMessage"') { continue }
                if ($line -match '"type":"UserMessage".*?"text":"(.*?)"') {
                    $title = $Matches[1] -replace '\\[nrt]', ' ' -replace '\\', ''
                    break
                }
            }

            if ($title) {
                $title = ($title -replace '\s+', ' ').Trim()
                if ($title.Length -gt 60) {
                    $title = $title.Substring(0, 60)
                    # Cut at 60 lands mid-word as often as not, and this is a
                    # name rather than a summary. Back off to the last space,
                    # unless that leaves too little to read.
                    $lastSpace = $title.LastIndexOf(' ')
                    if ($lastSpace -ge 30) { $title = $title.Substring(0, $lastSpace) }
                }
            }
        }
    } catch {}
}

# Grok's name lives in summary.json beside updates.jsonl. /rename sets
# title_is_manual. Colour was already derived above without writing into the
# transcript.
if ($Agent -eq 'grok' -and $State -ne 'ended') {
    try {
        if (-not $transcript -and $sessionId -ne 'unknown') {
            $grokHome = $env:GROK_HOME
            if (-not $grokHome) { $grokHome = Join-Path $env:USERPROFILE '.grok' }
            $sessions = Join-Path $grokHome 'sessions'
            if (Test-Path $sessions) {
                $match = Get-ChildItem -Path $sessions -Recurse -Filter 'updates.jsonl' -ErrorAction SilentlyContinue |
                    Where-Object { $_.Directory.Name -eq $sessionId } |
                    Select-Object -First 1
                if ($match) { $transcript = $match.FullName }
            }
        }

        if ($transcript -and (Test-Path $transcript)) {
            $summary = Join-Path (Split-Path $transcript) 'summary.json'
            if (Test-Path $summary) {
                $meta = Get-Content -Path $summary -Raw -Encoding UTF8 | ConvertFrom-Json
                if ($meta.title_is_manual -and $meta.title) { $title = [string]$meta.title }
                if (-not $title -and $meta.generated_title) { $title = [string]$meta.generated_title }
                if (-not $title -and $meta.session_summary) { $title = [string]$meta.session_summary }
            }
        }

        if ($title) {
            $title = ($title -replace '\\[nrt]', ' ' -replace '\\', '' -replace '\s+', ' ').Trim()
            if ($title.Length -gt 60) {
                $title = $title.Substring(0, 60)
                $lastSpace = $title.LastIndexOf(' ')
                if ($lastSpace -ge 30) { $title = $title.Substring(0, $lastSpace) }
            }
        }
    } catch {}
}

# Claude Code's CLAUDE_CODE_ENTRYPOINT: "cli" in a terminal, "claude-desktop"
# when Claude Desktop's Code tab spawned it. Only Claude Code sets it, so a
# Codex or Grok file gets '' even if inherited. Reduced to [A-Za-z0-9_-] to
# match the .sh hook, so the two write the same value for the same input.
$entrypoint = ''
if ($Agent -eq 'claude' -and $env:CLAUDE_CODE_ENTRYPOINT) {
    $entrypoint = $env:CLAUDE_CODE_ENTRYPOINT -replace '[^A-Za-z0-9_-]', ''
}

# Claude Desktop's own id for the chat (CLAUDE_CODE_HOST_SESSION_ID,
# "local_<uuid>"), which names the per-chat record the app reads to find a
# Remote Control bridge (CB-266). Same rules and sanitising as the entrypoint.
$hostSessionId = ''
if ($Agent -eq 'claude' -and $env:CLAUDE_CODE_HOST_SESSION_ID) {
    $hostSessionId = $env:CLAUDE_CODE_HOST_SESSION_ID -replace '[^A-Za-z0-9_-]', ''
}

$statusData = @{
    state           = $State

    # Which CLI wrote this, so the app can tell a Codex session from a Claude
    # Code one. A file from a hook older than this key has none, which reads as
    # Claude Code -- which is what it was.
    cli             = $Agent
    entrypoint      = $entrypoint
    host_session_id = $hostSessionId
    cwd             = $cwd
    title           = $title
    color           = $color
    term_program    = $termProgram
    term_id         = $termId
    term_pid        = $termPid
    session_pid     = $sessionPid
    transcript_path = $transcript
}

# A session running in WSL inside tmux. This script runs on the Windows side, so it sees the pane only
# because the WSL hook command lists TMUX and TMUX_PANE in WSLENV and hands over the claude process's
# pid and the distro's name (OW_WSL_PID / OW_WSL_DISTRO: plain hand-offs between two halves of one command,
# not user overrides, so deliberately outside the ORBWEAVER_ list BrandEnv holds). Recorded as the pane, the
# socket and tmux_bin "wsl:<distro>", which tells the app to run tmux through wsl.exe in that distro,
# and as wsl_pid -- never session_pid, because a Linux pid names no process on this host and the
# console route would otherwise AttachConsole to whatever has that number. Each value is checked for
# its shape before it is written: nothing here is trusted just because it came from an environment.
if ($env:TMUX_PANE -match '^%\d+$' -and $env:OW_WSL_PID -match '^\d+$' -and $env:OW_WSL_DISTRO -match '^[A-Za-z0-9._-]+$') {
    $statusData.tmux_pane   = $env:TMUX_PANE
    $statusData.tmux_socket = if ($env:TMUX) { ($env:TMUX -split ',')[0] } else { '' }
    $statusData.tmux_bin    = "wsl:$($env:OW_WSL_DISTRO)"
    $statusData.wsl_pid     = [int]$env:OW_WSL_PID
}
$status = $statusData | ConvertTo-Json -Compress

# Not Set-Content: on Windows PowerShell 5.1 it writes the ANSI codepage and
# replaces anything outside it with "?", so a chat name with an em dash or an
# accent would reach the app corrupted. UTF-8 *without* a BOM specifically --
# System.Text.Json treats a leading BOM as an invalid start of value, which
# would make the app skip the file and drop the orb entirely.
[System.IO.File]::WriteAllText($file, $status, (New-Object System.Text.UTF8Encoding($false)))
