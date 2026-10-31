# Installs the Orbweaver hook into Claude Code's Windows settings, and
# optionally into one or more WSL distros' settings too.
#
# The hook is what makes orbs appear: Claude Code runs it on session start,
# prompt submit, tool use, stop and session end, and it writes a small status
# file per session that the app watches. Without it wired into settings.json
# nothing happens at all -- no error, just no orbs, which is a confusing way to
# fail and the reason this script exists rather than a README instruction to
# hand-edit JSON.
#
#   .\tools\install-windows-hooks.ps1                     # native Windows only (default, unchanged)
#   .\tools\install-windows-hooks.ps1 -Uninstall          # remove our entries everywhere, incl. WSL
#   .\tools\install-windows-hooks.ps1 -Wsl                # also wire every WSL distro that has Claude Code
#   .\tools\install-windows-hooks.ps1 -Wsl -WslDistro Ubuntu
#   .\tools\install-windows-hooks.ps1 -UninstallWsl -WslDistro Ubuntu   # unwire just that one distro
#   .\tools\install-windows-hooks.ps1 -ProfileDir .claude-work                # + a second native account
#   .\tools\install-windows-hooks.ps1 -Wsl -WslProfileDir .claude-work,.claude-personal
#   .\tools\install-windows-hooks.ps1 -UninstallWsl -WslDistro Ubuntu -WslProfileDir .claude-work -WslProfileDirOnly
#     # unwire just that one extra WSL profile, leaving the distro's default ~/.claude alone
#
# Safe to re-run: it strips any existing entries of ours before adding fresh
# ones, so it converges rather than accumulating duplicates.
#
# "Ours" is either hook filename, OrbweaverHook.* or the pre-rename
# ClaudeBuddyHook.* (CB-255), matched case-insensitively, so an upgrade
# re-wires in the same pass. The old %LOCALAPPDATA%\ClaudeBuddy copy is left on
# disk for sessions still running against it, with a .superseded marker the
# app's LegacyHookCleanup reads.
#
# WSL is opt-in via -Wsl/-UninstallWsl so a bare invocation -- which is what the
# installer's [Run]/[Icons] entries and the plain "wire up hooks" shortcut all
# do -- keeps behaving exactly as before. -Uninstall is the exception: it always
# sweeps every WSL distro too, since leaving a dangling hook that points at a
# script this same run just deleted would make Claude Code log an error on
# every event in every affected WSL session.
#
# -ProfileDir/-WslProfileDir cover a second (or third...) Claude Code account
# managed via CLAUDE_CONFIG_DIR (e.g. an alias like
# `alias kwork="CLAUDE_CONFIG_DIR=~/.claude-work claude"`) -- each is a config
# directory name wired in *addition* to the default ~/.claude, never a
# replacement for it, and never auto-discovered: only names explicitly passed
# here or already saved via the app's Settings window are ever touched.

[CmdletBinding()]
param(
    [switch] $Uninstall,

    # Where the hook script is copied to. Kept out of the repo so the hook keeps
    # working if the clone moves or is deleted.
    [string] $InstallDir = (Join-Path $env:LOCALAPPDATA 'Orbweaver'),

    [string] $SettingsPath = (Join-Path $env:USERPROFILE '.claude\settings.json'),

    # Wire hooks into WSL distros too (install mode only; -Uninstall always
    # covers WSL regardless of this switch).
    [switch] $Wsl,

    # Remove WSL hook entries only, leaving native Windows hooks untouched --
    # the "turn WSL support off for this one distro" case, as opposed to
    # -Uninstall which is the full teardown used by the app's uninstaller.
    [switch] $UninstallWsl,

    # Restrict -Wsl / -UninstallWsl to specific distro name(s). Default is
    # every distro Get-WslDistros reports.
    [string[]] $WslDistro,

    # Touch only the -WslProfileDir entries, leaving each distro's own
    # default ~/.claude untouched -- the "clean up just this one extra
    # profile" case. Without this, -UninstallWsl -WslProfileDir X always
    # unwired the distro's default profile too, on the way to unwiring X,
    # which is surprising and was found to actually happen: a one-off
    # cleanup of a single stale extra profile silently took the distro's
    # main orb down with it.
    [switch] $WslProfileDirOnly,

    # Wire a WSL distro even though Claude Code wasn't detected on its PATH.
    # Without this, a distro nobody has installed Claude Code into is silently
    # skipped rather than gaining a dead ~/.claude/settings.json.
    [switch] $Force,

    # Extra Claude Code CLI config directory names -- e.g. ".claude-work" for
    # a `CLAUDE_CONFIG_DIR=~/.claude-work claude` alias managing a second
    # account -- to also wire on native Windows, beyond the default
    # -SettingsPath. $null (not passed at all) defaults to whatever the app's
    # own Settings window has saved; pass an explicit @() to wire only the
    # default profile even if the app has some saved. Never auto-discovered:
    # only directories the user explicitly opted into (here or in Settings)
    # are ever touched.
    [string[]] $ProfileDir,

    # Same, applied to every WSL distro being processed.
    [string[]] $WslProfileDir
)

$ErrorActionPreference = 'Stop'

# Same source of truth the Settings window itself writes to -- not a separate
# config format, and not IPC, so the installer, a Start Menu re-run, and the
# running app can never disagree about which extra profiles are configured.
# Works with no app installed or ever run yet: that's just an absent file,
# same as the caller genuinely having nothing configured.
#
# Orbweaver's folder first, then the pre-rename ClaudeBuddy one: on upgrade day
# the Inno installer runs this before the new app has started and moved the
# folder, and reading only the new path then would wire zero extra profiles.
function Get-ConfiguredExtraProfileDirs {
    # ORBWEAVER_SETTINGS_DIR first and alone, as the app and the macOS
    # installers do, so a test instance pointed at a scratch directory is not
    # wired from the real list (CB-258); its pre-rename spelling still works
    # (CB-256), and the new one wins when both are set. Otherwise Orbweaver's
    # folder, then the pre-rename ClaudeBuddy one.
    if ($env:ORBWEAVER_SETTINGS_DIR) {
        $path = Join-Path $env:ORBWEAVER_SETTINGS_DIR 'settings.json'
    } elseif ($env:CLAUDE_BUDDY_SETTINGS_DIR) {
        $path = Join-Path $env:CLAUDE_BUDDY_SETTINGS_DIR 'settings.json'
    } else {
        $path = Join-Path $env:APPDATA 'Orbweaver\settings.json'
        if (-not (Test-Path -LiteralPath $path)) { $path = Join-Path $env:APPDATA 'ClaudeBuddy\settings.json' }
    }
    if (-not (Test-Path -LiteralPath $path)) { return @() }

    try {
        $json = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
        return @($json.claudeCodeProfileDirs | Where-Object { $_ })
    }
    catch {
        # Said, not swallowed: an unreadable list used to look exactly like an
        # empty one (CB-258).
        Write-Warning "Could not read the saved profile list from $path ($($_.Exception.Message)); no extra profiles will be wired."
        $script:ProfileListUnreadable = $true
        return @()
    }
}

if ($null -eq $ProfileDir) { $ProfileDir = @(Get-ConfiguredExtraProfileDirs) }
if ($null -eq $WslProfileDir) { $WslProfileDir = @(Get-ConfiguredExtraProfileDirs) }

$repoRoot = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repoRoot 'OrbweaverHook.ps1'
$installed = Join-Path $InstallDir 'OrbweaverHook.ps1'
# Where every install before the rename copied the script: the sibling named
# ClaudeBuddy, so a custom -InstallDir finds its own legacy twin.
$legacyInstallDir = Join-Path (Split-Path -Parent $InstallDir) 'ClaudeBuddy'

# -UninstallWsl on its own is documented above as touching only WSL, leaving
# native Windows hooks exactly as they were. Every other combination still
# (re)wires native, matching a bare invocation's existing behavior -- that's
# what WslIntegration.ReapplyProfiles and the Settings window's per-distro
# checkbox (SetWired, which passes bare -Wsl) rely on. Without this guard,
# -UninstallWsl alone fell through to the "not $Uninstall" branch below and
# silently re-copied the hook script and re-wired native hooks -- invisible
# whenever native was already wired (the re-wire is idempotent), but
# confirmed to reactivate native hooks that had just been fully removed by
# -Uninstall, when later cleaning up a WSL-only profile by hand.
$touchNative = $Uninstall -or $Wsl -or (-not $UninstallWsl)

if ($touchNative) {
    if (-not $Uninstall) {
        if (-not (Test-Path -LiteralPath $source)) {
            throw "Can't find OrbweaverHook.ps1 next to the repo root ($source)."
        }

        New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $installed -Force
        Write-Host "Hook installed: $installed"

        # Mark, never delete, and only when absent: the Settings window
        # reapplies hooks on every colour toggle, and a refreshed mtime each
        # time would keep the app's 14-day retirement clock from running out.
        # The folder also still holds Logs\ until the app's data-dir migration
        # moves it, which is one more reason this script must not delete it.
        $legacyMarker = Join-Path $legacyInstallDir '.superseded'
        if ((Test-Path -LiteralPath $legacyInstallDir -PathType Container) -and -not (Test-Path -LiteralPath $legacyMarker)) {
            [System.IO.File]::WriteAllText($legacyMarker, '')
            Write-Host "Marked $legacyInstallDir as superseded; Orbweaver retires it once nothing calls it."
        }
    }
}

# ConvertFrom-Json -AsHashtable is PowerShell 6+, and the powershell.exe Claude
# Code invokes on Windows is 5.1, so convert the object graph by hand. Recursive
# because settings.json nests: hooks -> event -> groups -> hooks -> command.
function ConvertTo-HashtableDeep($value) {
    if ($null -eq $value) { return $null }

    if ($value -is [System.Collections.IDictionary]) {
        $copy = @{}
        foreach ($key in @($value.Keys)) { $copy[$key] = ConvertTo-HashtableDeep $value[$key] }
        return $copy
    }

    if ($value -is [System.Management.Automation.PSCustomObject]) {
        $copy = @{}
        foreach ($property in $value.PSObject.Properties) {
            $copy[$property.Name] = ConvertTo-HashtableDeep $property.Value
        }
        return $copy
    }

    # Strings are enumerable; check them before the array branch.
    if ($value -is [string]) { return $value }

    if ($value -is [System.Collections.IEnumerable]) {
        return @(foreach ($item in $value) { ConvertTo-HashtableDeep $item })
    }

    return $value
}

# Which Claude Code events drive which orb state. Notification carries a matcher
# because only some notifications mean "Claude needs you"; the rest would make
# every orb amber, which would make amber meaningless. Shared between native and
# every WSL distro -- only the command line differs.
$script:Wanted = @(
    @{ Event = 'SessionStart';     Matcher = $null;                  State = 'idle' },
    @{ Event = 'UserPromptSubmit'; Matcher = $null;                  State = 'generating' },
    @{ Event = 'PreToolUse';       Matcher = '.*';                   State = 'generating' },
    @{ Event = 'Stop';             Matcher = $null;                  State = 'idle' },
    @{ Event = 'SessionEnd';       Matcher = $null;                  State = 'ended' },
    @{ Event = 'Notification';     Matcher = 'permission_prompt';    State = 'waiting' },
    @{ Event = 'Notification';     Matcher = 'elicitation_dialog';   State = 'waiting' },
    @{ Event = 'Notification';     Matcher = 'elicitation_complete'; State = 'generating' }
)

# Does the actual read-strip-add-write for one settings.json, wherever it lives
# (a local path for native Windows, a \\wsl.localhost\... UNC path for WSL).
# $CommandBuilder turns a state name into the full hook command line, since
# that's the only part that differs between native and WSL invocations.
function Set-ClaudeBuddyHooks {
    param(
        [Parameter(Mandatory)] [string] $SettingsPath,
        [Parameter(Mandatory)] [scriptblock] $CommandBuilder,
        [switch] $Uninstall
    )

    if (-not (Test-Path -LiteralPath $SettingsPath)) {
        if ($Uninstall) { return }  # nothing to remove

        # An absent settings file is normal on a fresh install; start one rather
        # than refusing, but never invent anything beyond the hooks themselves.
        New-Item -ItemType Directory -Path (Split-Path -Parent $SettingsPath) -Force | Out-Null
        '{}' | Set-Content -LiteralPath $SettingsPath -Encoding ASCII
        Write-Host "Created $SettingsPath"
    }

    # Read with -Raw and parse: this preserves every existing setting, which
    # matters because this file holds the user's model, permissions, status
    # line and so on.
    $json = Get-Content -LiteralPath $SettingsPath -Raw -Encoding UTF8
    $settings = if ([string]::IsNullOrWhiteSpace($json)) { @{} } else { ConvertTo-HashtableDeep ($json | ConvertFrom-Json) }

    $backup = "$SettingsPath.orbweaver-backup"
    Copy-Item -LiteralPath $SettingsPath -Destination $backup -Force
    Write-Host "Backed up settings to $backup"

    if (-not $settings.ContainsKey('hooks') -or $null -eq $settings['hooks']) {
        $settings['hooks'] = @{}
    }

    $hooks = $settings['hooks']

    # Strip our own entries wherever they appear, so re-running repairs rather
    # than duplicating, and an uninstall leaves other tools' hooks untouched.
    # Either filename is ours: the pre-rename ClaudeBuddyHook.ps1 entries go in
    # the same pass, which is the whole of the upgrade's re-wire. -like is
    # case-insensitive, as a Windows path is.
    # $event is an automatic variable in PowerShell; using it as a loop
    # variable here would shadow it and can misbehave.
    foreach ($eventName in @($hooks.Keys)) {
        $groups = @($hooks[$eventName])
        $kept = @()

        foreach ($group in $groups) {
            if ($null -eq $group) { continue }

            $inner = @(@($group['hooks']) | Where-Object {
                $_ -and ($_['command'] -notlike '*ClaudeBuddyHook.*') `
                   -and ($_['command'] -notlike '*OrbweaverHook.*')
            })

            if ($inner.Count -gt 0) {
                $group['hooks'] = $inner
                $kept += $group
            }
        }

        if ($kept.Count -gt 0) { $hooks[$eventName] = $kept } else { $hooks.Remove($eventName) }
    }

    if (-not $Uninstall) {
        foreach ($entry in $script:Wanted) {
            $group = @{ hooks = @(@{ type = 'command'; command = (& $CommandBuilder $entry.State) }) }
            if ($entry.Matcher) { $group['matcher'] = $entry.Matcher }

            $existing = if ($hooks.ContainsKey($entry.Event)) { @($hooks[$entry.Event]) } else { @() }
            $hooks[$entry.Event] = @($existing) + @($group)
        }
    }

    $settings['hooks'] = $hooks

    # UTF-8 *without* a BOM. System.Text.Json -- which Claude Code and this app
    # both use -- treats a leading BOM as an invalid start of value, and
    # PowerShell 5.1's Set-Content adds one by default. This exact mistake has
    # bitten this project before, in the hook itself.
    $out = $settings | ConvertTo-Json -Depth 20
    [System.IO.File]::WriteAllText($SettingsPath, $out, (New-Object System.Text.UTF8Encoding($false)))

    if ($Uninstall) {
        Write-Host "Removed Orbweaver hooks from $SettingsPath"
    }
    else {
        Write-Host "Wired $($script:Wanted.Count) hook entries into $SettingsPath"
    }
}

# The orbweaver-team skill (CB-291): copied beside a settings.json, into
# <config dir>\skills\orbweaver-team, because each Claude Code account reads only
# its own config directory -- native, a second native account, or a WSL distro's
# (a \\wsl.localhost\... path, which Copy-Item reaches like any other). Called
# once for every settings.json this script wires. It is shipped code, not the
# user's files (their personas, roles and teams live in their pool or project),
# so an install replaces the folder outright and an uninstall removes that one
# folder and nothing beside it. A skill that cannot be found is a warning: the
# hooks are what this script is for.
$skillSource = Join-Path $repoRoot 'skills\orbweaver-team'

# A junction or symlink is removed as the link it is and never walked into: on
# Windows PowerShell 5.1, Remove-Item -Recurse follows one and deletes what it
# points at, which for a folder someone linked into place is their own files.
function Remove-SkillDirectory([string] $Path) {
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
        $item.Delete()
    }
    else {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

function Set-OrbweaverSkill {
    param(
        [Parameter(Mandatory)] [string] $SettingsPath,
        [switch] $Uninstall
    )

    $skillDir = Join-Path (Join-Path (Split-Path -Parent $SettingsPath) 'skills') 'orbweaver-team'

    if ($Uninstall) {
        if (Test-Path -LiteralPath $skillDir) {
            Remove-SkillDirectory $skillDir
            Write-Host "Removed skill: $skillDir"
        }
        return
    }

    if (-not (Test-Path -LiteralPath (Join-Path $skillSource 'SKILL.md'))) {
        Write-Warning "Can't find skills\orbweaver-team next to the repo root ($skillSource); the team skill was not installed for $(Split-Path -Parent $SettingsPath)."
        return
    }

    if (Test-Path -LiteralPath $skillDir) { Remove-SkillDirectory $skillDir }
    New-Item -ItemType Directory -Path $skillDir -Force | Out-Null
    Copy-Item -Path (Join-Path $skillSource '*') -Destination $skillDir -Recurse -Force
    Write-Host "Skill installed: $skillDir"
}

# Computed once, here, in this script's own normal full environment, and
# baked into every wired command as a literal -- rather than letting
# OrbweaverHook.ps1 re-derive it via $env:TEMP at hook-run time, where a
# WSL-interop-launched invocation's environment can't be trusted to have
# TEMP/TMP set at all (see OrbweaverHook.ps1's own comment on this; found
# on a real machine to silently point the hook at an unrelated folder with no
# visible error -- the hook reported success, but the app never saw a status
# file). This keeps every hook, on both native Windows and WSL, resolving to
# the exact same folder Path.GetTempPath() gives the app itself, with no
# environment-dependent guessing at the point the hook actually runs.
#
# TrimEnd('\') matters: GetTempPath() always returns a trailing backslash,
# and embedding that directly inside "..." quotes produces a command-line
# argument ending in \" -- which Windows' argument parser reads as an escaped
# literal quote, not a closing delimiter, so the quoted region never actually
# closes and swallows the rest of the command line. Caught by tracing through
# the exact argument this would have produced before shipping it, not by
# hitting it live.
$resolvedTempDir = ([System.IO.Path]::GetTempPath()).TrimEnd('\')

$nativeCommandBuilder = {
    param($State)
    $quoted = '"' + $installed + '"'
    $tempQuoted = '"' + $resolvedTempDir + '"'
    "powershell.exe -NoProfile -ExecutionPolicy Bypass -File $quoted -State $State -TempDir $tempQuoted"
}

if ($touchNative) {
    Set-ClaudeBuddyHooks -SettingsPath $SettingsPath -CommandBuilder $nativeCommandBuilder -Uninstall:$Uninstall
    Set-OrbweaverSkill -SettingsPath $SettingsPath -Uninstall:$Uninstall

    foreach ($entry in $ProfileDir) {
        $extraPath = if ([System.IO.Path]::IsPathRooted($entry)) {
            Join-Path $entry 'settings.json'
        }
        else {
            Join-Path (Join-Path $env:USERPROFILE $entry) 'settings.json'
        }
        Write-Host "--- Additional Claude Code profile: $entry ---"
        Set-ClaudeBuddyHooks -SettingsPath $extraPath -CommandBuilder $nativeCommandBuilder -Uninstall:$Uninstall
        Set-OrbweaverSkill -SettingsPath $extraPath -Uninstall:$Uninstall
    }
}

# ---------------------------------------------------------------------------
# WSL: each distro is a completely separate Claude Code install with its own
# ~/.claude/settings.json, invisible to the native wiring above. Both WSL and
# native Windows hooks ultimately shell out to powershell.exe as a normal
# Windows process, so $env:TEMP resolves to the same real folder either way --
# a WSL session and a native session show up as two independent orbs in one
# running Orbweaver.exe, which is the whole point of doing this.
# ---------------------------------------------------------------------------

$wslExe = Join-Path $env:SystemRoot 'System32\wsl.exe'

# Distro discovery deliberately never calls wsl.exe at all: `wsl.exe -l/--list`
# has a real, still-open Microsoft bug (microsoft/WSL#4607) where it writes
# UTF-16LE to stdout even when redirected, which a plain PowerShell 5.1 `&`
# capture decodes as one character per line ("Ubuntu" arrives as six one-letter
# lines) -- confirmed against a real WSL install. An earlier version of this
# script "fixed" that by toggling [Console]::OutputEncoding around the call,
# which in turn was found to corrupt *later*, unrelated `wsl.exe -d ...`
# invocations in the same process -- a second bug introduced while chasing the
# first. Reading the registry instead sidesteps both: it's the same place
# `wsl.exe -l` gets its answer from, needs no subprocess, and can't be hit by
# either encoding bug. Confirmed working correctly against a real machine.
function Get-WslDistros {
    $lxssKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Lxss'
    if (-not (Test-Path -LiteralPath $lxssKey)) { return @() }

    $names = Get-ChildItem -LiteralPath $lxssKey -ErrorAction SilentlyContinue |
        ForEach-Object { (Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue).DistributionName } |
        Where-Object { $_ }

    # @()-wrapped deliberately: a Where-Object pipeline that resolves to
    # exactly one item -- the common case, one real distro once docker-desktop
    # is filtered out -- is *not* auto-wrapped in an array by PowerShell, and
    # indexing or iterating a bare string doesn't do what you'd expect (e.g.
    # `("Ubuntu")[0]` is the character 'U', not the string "Ubuntu"). Confirmed
    # by hitting this exact bug during testing. Every call site that consumes
    # this list re-wraps defensively too, in case this function is ever
    # refactored to skip the wrap internally.
    return @($names | Where-Object { $_ -notlike 'docker-desktop*' } | Sort-Object -Unique)
}

# Runs one wsl.exe invocation with a hard timeout, mirroring the safe-
# subprocess idiom already used on the C# side of this app (TerminalFocuser.
# TryRun, ClaudeDesktopManager.Run): redirect both streams, start async reads
# *before* waiting (reading first would make the timeout unreachable -- it only
# returns once the pipe closes, which a wedged child never does), kill on
# timeout, never throw. Without this, a wedged distro would hang this script
# forever, and by extension the installer's blocking post-install step or a
# Settings-window click that shells out to this script.
#
# PowerShell 5.1 has no `Start-Process -Wait -Timeout` that also captures
# output cleanly, and `Start-Job` has its own overhead/reliability problems on
# 5.1, so this is done by hand against System.Diagnostics.Process directly --
# same shape as the C# idiom, translated to PS 5.1-compatible syntax.
#
# Quotes and joins arguments into the single command-line string
# ProcessStartInfo.Arguments expects. Deliberately not ArgumentList (a plain
# collection you'd .Add() each argument to) -- that property only exists on
# .NET Core 2.1+/.NET 5+, and Windows PowerShell 5.1 always runs on the older
# .NET Framework, which never got it. There, $psi.ArgumentList silently
# evaluates to $null, and .Add($a) on it throws "You cannot call a method on
# a null-valued expression" -- caught by hitting this exact error on a real
# Windows box. This mirrors the quoting .NET's own ArgumentList-to-string
# conversion does internally, so it stays correct if an argument ever needs
# an embedded quote or backslash, not just the plain words used today.
function ConvertTo-QuotedArgument([string] $Value) {
    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') { return $Value }

    $builder = New-Object System.Text.StringBuilder
    [void]$builder.Append('"')
    for ($i = 0; $i -lt $Value.Length; $i++) {
        $backslashes = 0
        while ($i -lt $Value.Length -and $Value[$i] -eq '\') { $backslashes++; $i++ }

        if ($i -eq $Value.Length) {
            [void]$builder.Append('\' * ($backslashes * 2))
            break
        }
        elseif ($Value[$i] -eq '"') {
            [void]$builder.Append('\' * ($backslashes * 2 + 1))
            [void]$builder.Append('"')
        }
        else {
            [void]$builder.Append('\' * $backslashes)
            [void]$builder.Append($Value[$i])
        }
    }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Invoke-WslTimeout {
    param(
        [Parameter(Mandatory)] [string[]] $Arguments,
        # Deliberately short: this is meant to answer "is this specific
        # command actually stuck," which only means something once the shared
        # WSL2 VM is already up. The VM's own cold-boot cost -- common, not an
        # edge case, since it shuts down after a period of inactivity and
        # every fresh install starts from exactly that state -- is paid once,
        # up front, by the explicit warm-up call below, specifically so this
        # number doesn't also have to absorb it. A login shell (-lc) sourcing
        # nvm/asdf-style .bashrc setup still adds some latency on every call
        # regardless of VM state, which is why this is 5s rather than
        # something tighter, but it's not trying to cover a VM boot too.
        [int] $TimeoutMs = 5000
    )

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $wslExe
    $psi.Arguments = ($Arguments | ForEach-Object { ConvertTo-QuotedArgument $_ }) -join ' '
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $psi

    try {
        [void]$process.Start()

        # Start both async reads *before* waiting -- same idiom the C# side of
        # this app already uses (TerminalFocuser.TryRun): a blocking
        # ReadToEnd() first would make the timeout unreachable, and
        # undrained stderr can deadlock a chatty child once its pipe buffer
        # fills. Reading each Task's result via .GetAwaiter().GetResult()
        # after WaitForExit, rather than Register-ObjectEvent +
        # BeginOutputReadLine, on purpose: the event-based version was tried
        # first and found unreliable on a real machine -- the child exits
        # cleanly and WaitForExit returns true, but the queued
        # OutputDataReceived action doesn't reliably get *processed* by
        # PowerShell's own eventing subsystem while the engine is sitting
        # inside a raw WaitForExit call, so the captured buffer can still be
        # empty even on a clean, fast exit. Calling ReadToEndAsync()
        # directly has no dependency on that queue at all.
        $outputTask = $process.StandardOutput.ReadToEndAsync()
        $errorTask = $process.StandardError.ReadToEndAsync()

        if (-not $process.WaitForExit($TimeoutMs)) {
            # .NET Framework (what PS 5.1 runs on) has no Kill(entireTree:
            # true) overload -- that's .NET Core 3+/net8.0 only, which is what
            # the C# app itself targets, but not this script. taskkill /T is
            # the best available substitute here. This guarantees the *script*
            # doesn't hang forever; it does not guarantee whatever's wedged
            # inside the WSL VM actually dies, which is an accepted scope
            # boundary, not an oversight.
            try { $process.Kill() } catch { }
            try { & "$env:SystemRoot\System32\taskkill.exe" /PID $process.Id /T /F 2>$null } catch { }
            # A timeout and an exception both end up looking identical to
            # callers (both just get an empty array back) unless something
            # says which one happened -- worth knowing for anyone debugging a
            # distro that unexpectedly got skipped, not just this run.
            Write-Host "  (wsl.exe $($Arguments -join ' ') timed out after ${TimeoutMs}ms)"
            return @()
        }

        $stdout = $outputTask.GetAwaiter().GetResult()
        [void]$errorTask.GetAwaiter().GetResult()

        return @($stdout -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    }
    catch {
        Write-Host "  (wsl.exe $($Arguments -join ' ') failed: $_)"
        return @()
    }
}

# One combined call per distro instead of two -- halves the wsl.exe invocation
# count (and therefore the hang/stress exposure) versus separate `printenv
# HOME` and `command -v claude` calls.
#
# Finding 'claude' isn't as simple as checking PATH in some fixed shell mode,
# because there's no single dotfile convention: nvm/pyenv/rustup/asdf-style
# installers put their PATH-modifying line in ~/.bashrc *or* ~/.zshrc
# depending on the user's shell, both of which are conventionally read only
# by an *interactive* shell -- a login shell (-l) reads /etc/profile +
# ~/.profile instead, and never those, regardless of which other flags are
# also given. Hardcoding one shell is a real gap, not a hypothetical one:
# tested against a real machine where 'claude' is installed via nvm -- bash
# -lc reported it missing, and separately, the user's actual interactive
# shell is zsh, so a bash-only fix would have kept failing for exactly the
# case this whole feature targets. A plain "does this work in an interactive
# terminal" spot check can also be misleading here, since a shell spawned
# *from* an already-interactive session inherits its parent's already-correct
# PATH regardless of its own startup-file choice -- the gap only shows up on
# a genuinely fresh invocation, which is exactly what `wsl.exe -d <distro>
# --` is.
#
# So: try the current (non-interactive) PATH first, then bash's interactive
# startup (~/.bashrc), then zsh's if zsh is even installed (~/.zshrc), then
# bash's login startup (~/.profile) as a last resort -- first one to find it
# wins. Still one wsl.exe call: the fallback chain runs as a single compound
# command inside it, not as separate invocations.
function Get-WslDistroInfo([string] $Distro) {
    $marker = '__ORBWEAVER_SPLIT__'
    $findClaude = "command -v claude 2>/dev/null" +
        " || bash -ic 'command -v claude' 2>/dev/null" +
        " || { command -v zsh >/dev/null 2>&1 && zsh -ic 'command -v claude' 2>/dev/null; }" +
        " || bash -lc 'command -v claude' 2>/dev/null"
    # A bit more headroom than the default: this may start up to three
    # nested interactive shells (including a heavier one like oh-my-zsh)
    # in sequence before giving up.
    $lines = Invoke-WslTimeout -TimeoutMs 10000 -Arguments @(
        '-d', $Distro, '--', 'bash', '-c',
        "printenv HOME; echo $marker; $findClaude"
    )
    if ($lines.Count -eq 0) { return $null }

    $splitAt = [array]::IndexOf($lines, $marker)
    if ($splitAt -lt 0) {
        # No marker came back at all -- treat the whole thing as unusable
        # rather than guess which line was which.
        return $null
    }

    return @{
        Home          = if ($splitAt -gt 0) { $lines[0] } else { $null }
        HasClaudeCode = $splitAt -lt ($lines.Count - 1)
    }
}

# The settings.json inside a distro, addressed from the Windows side. Prefer
# \\wsl.localhost\..., the current form; \\wsl$\... is the older alias, kept as
# a fallback for builds where .localhost isn't registered. $ProfileDirName
# defaults to the standard '.claude', but any CLAUDE_CONFIG_DIR-style name
# works the same way -- it's just the directory settings.json lives in.
function Get-WslSettingsPath([string] $Distro, [string] $LinuxHome, [string] $ProfileDirName = '.claude') {
    $rel = ($LinuxHome.TrimStart('/') -replace '/', '\') + '\' + $ProfileDirName + '\settings.json'
    $viaLocalhost = "\\wsl.localhost\$Distro\$rel"
    if (Test-Path -LiteralPath (Split-Path -Parent $viaLocalhost) -ErrorAction SilentlyContinue) {
        return $viaLocalhost
    }
    "\\wsl`$\$Distro\$rel"
}

if ($Uninstall -or $Wsl -or $UninstallWsl) {
    # @()-wrapped at every step per the scalar-collapse note above -- the
    # single-distro case is the common one, not an edge case.
    $distros = @(Get-WslDistros)
    if ($WslDistro) {
        $distros = @($distros | Where-Object { $WslDistro -contains $_ })
    }

    $removeWsl = $Uninstall -or $UninstallWsl

    # Pay the shared WSL2 VM's cold-boot cost exactly once, up front, with a
    # generous timeout -- not per distro, and not folded into
    # Invoke-WslTimeout's own default (see the comment there for why). This
    # is a real, common cost (the VM shuts down after a period of inactivity,
    # so a fresh install or "haven't touched WSL in a while" both start from
    # a cold VM), not a rare edge case worth ignoring. 'echo ready' rather
    # than a no-output command like 'true': a timeout and a genuinely empty
    # successful result would otherwise look identical to the code below.
    $warmupFailed = $false
    if ($distros.Count -gt 0) {
        $warmup = Invoke-WslTimeout -Arguments @('-d', $distros[0], '--', 'echo', 'ready') -TimeoutMs 20000
        if ($warmup.Count -eq 0) {
            Write-Host 'WSL did not respond within 20s -- skipping WSL entirely this run.'
            Write-Host "If this persists, check that 'wsl.exe' works normally from an ordinary terminal."
            $warmupFailed = $true
            $distros = @()
        }
    }

    foreach ($distro in $distros) {
        # Both branches need a home directory to compute the settings.json
        # path (Get-WslSettingsPath), and -Force/uninstall don't change that --
        # only whether HasClaudeCode gets consulted below.
        $info = Get-WslDistroInfo $distro

        if (-not $info -or -not $info.Home) {
            Write-Host "Skipping WSL distro '$distro': couldn't read its `$HOME (is it running / installed correctly?)."
            continue
        }

        $wslSettingsPath = Get-WslSettingsPath $distro $info.Home

        if (-not $removeWsl -and -not $Force -and -not $info.HasClaudeCode) {
            Write-Host "Skipping WSL distro '$distro': no 'claude' on its PATH. Pass -Force to wire it anyway."
            continue
        }

        # Plain Windows-style path for -File, not a /mnt/c/... conversion:
        # confirmed on a real machine that WSL only resolves a /mnt/... path
        # for the *executable* it launches, not for arguments handed to that
        # executable afterward. A /mnt/c/... argument reaches powershell.exe
        # completely unrewritten, which fails immediately with "the argument
        # ... does not exist" -- silently as far as Claude Code's hook
        # success/failure detection is concerned, since powershell.exe still
        # exits 0 in this case. This is the actual reason orbs never worked
        # for WSL sessions at all, predating every other change made today.
        # A plain Windows path has no such translation step to get wrong:
        # bash's double-quote rules don't treat backslashes before ordinary
        # characters as special, so it passes through unchanged, exactly like
        # -TempDir already does above.
        $wslCommandBuilder = {
            param($State)
            "/mnt/c/WINDOWS/System32/WindowsPowerShell/v1.0/powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$installed`" -State $State -TempDir `"$resolvedTempDir`""
        }.GetNewClosure()

        if (-not $WslProfileDirOnly) {
            Write-Host "--- WSL distro: $distro ---"
            Set-ClaudeBuddyHooks -SettingsPath $wslSettingsPath -CommandBuilder $wslCommandBuilder -Uninstall:$removeWsl
            Set-OrbweaverSkill -SettingsPath $wslSettingsPath -Uninstall:$removeWsl
        }

        foreach ($entry in $WslProfileDir) {
            $extraWslSettingsPath = Get-WslSettingsPath $distro $info.Home $entry
            Write-Host "--- WSL distro: $distro, profile: $entry ---"
            Set-ClaudeBuddyHooks -SettingsPath $extraWslSettingsPath -CommandBuilder $wslCommandBuilder -Uninstall:$removeWsl
            Set-OrbweaverSkill -SettingsPath $extraWslSettingsPath -Uninstall:$removeWsl
        }
    }

    if ($distros.Count -eq 0 -and ($Wsl -or $UninstallWsl) -and -not $warmupFailed) {
        Write-Host 'No WSL distros found (or none matched -WslDistro).'
    }
}

if ($Uninstall) {
    Write-Host ''
    Write-Host 'Removed Orbweaver hooks (native Windows and any WSL distros found).'
    Write-Host 'The installed hook script was left in place; delete it if you want it gone.'
}
else {
    Write-Host ''
    Write-Host 'Restart any running Claude Code sessions -- hooks are read at session start,'
    Write-Host 'so existing sessions will not produce orbs until they are restarted.'
}

# Exit 3 means the saved profile list could not be read: the default profile was
# still wired, but any extra ones were not (HookInstaller.SavedListUnreadableExit,
# CB-258).
if ($script:ProfileListUnreadable) { exit 3 }
