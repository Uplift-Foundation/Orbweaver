<#
.SYNOPSIS
Wires the Orbweaver hook into Codex on Windows, so Codex sessions show orbs.

.DESCRIPTION
The Windows twin of tools/install-codex-hooks.sh, and the sibling of
install-windows-hooks.ps1, which does this job for Claude Code.

Three things differ from the Claude Code installer, all of them measured on
macOS against a real Codex -- see docs/codex-findings.md:

 1. The target is its own file. $CODEX_HOME\hooks.json is discovered
    automatically; nothing needs adding to config.toml.
 2. Codex has no Notification event. PermissionRequest is the analogue, and a
    better one: it carries the tool name. An event name Codex does not know is
    dropped *silently*, with no warning and no error, so a wrong name here would
    look exactly like a hook that never fires.
 3. PermissionRequest is installed async. A synchronous hook on that event can
    deny the user's own approval by exiting non-zero or printing anything that
    is not its expected JSON. async makes it structurally unable to return a
    decision, which is the only guarantee worth having when the failure mode is
    refusing something the user asked for.

Safe to re-run: it strips existing entries of ours before adding fresh ones,
so it converges rather than accumulating duplicates. That matters more here
than for Claude Code, because Codex's own /import copies a Claude Code setup
across and can leave hooks of its own behind.

"Ours" is either hook filename, OrbweaverHook.* or the pre-rename
ClaudeBuddyHook.* (CB-255), in command or commandWindows, so an upgrade
re-wires in the same pass. The old claude-buddy script folder is left on disk
for sessions still running against it, with a .superseded marker the app's
LegacyHookCleanup reads.

.PARAMETER Uninstall
Remove just our entries, leaving any other tool's hooks alone.
#>
param(
    [switch]$Uninstall,
    [string]$CodexHome = '',
    [string]$HooksPath = '',
    [string]$HookDir = ''
)

$ErrorActionPreference = 'Stop'

if (-not $CodexHome) {
    $CodexHome = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE '.codex' }
}
if (-not $HookDir)   { $HookDir   = Join-Path $CodexHome 'orbweaver' }
if (-not $HooksPath) { $HooksPath = Join-Path $CodexHome 'hooks.json' }
$legacyHookDir = Join-Path $CodexHome 'claude-buddy'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path

# Alongside (installed layout) wins over one level up (repo checkout), matching
# how install-windows-hooks.ps1 resolves the same script.
$source = @(
    (Join-Path $here 'OrbweaverHook.ps1'),
    (Join-Path (Split-Path -Parent $here) 'OrbweaverHook.ps1')
) | Where-Object { Test-Path $_ } | Select-Object -First 1

$installed = Join-Path $HookDir 'OrbweaverHook.ps1'

if (-not $Uninstall) {
    if (-not $source) {
        Write-Error "Can't find OrbweaverHook.ps1 next to $here or one level up."
        exit 1
    }
    New-Item -ItemType Directory -Path $HookDir -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $installed -Force
    Write-Host "Hook installed: $installed"

    # Mark, never delete, and only when absent: a refreshed mtime on every
    # re-run would keep the app's 14-day retirement clock from running out.
    $marker = Join-Path $legacyHookDir '.superseded'
    if ((Test-Path -LiteralPath $legacyHookDir -PathType Container) -and -not (Test-Path -LiteralPath $marker)) {
        [System.IO.File]::WriteAllText($marker, '')
        Write-Host "Marked $legacyHookDir as superseded; Orbweaver retires it once nothing calls it."
    }
}

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

if (-not (Test-Path $HooksPath)) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $HooksPath) -Force | Out-Null
    '{}' | Set-Content -LiteralPath $HooksPath -Encoding ASCII
    Write-Host "Created $HooksPath"
}

$json = Get-Content -LiteralPath $HooksPath -Raw -Encoding UTF8
$config = if ([string]::IsNullOrWhiteSpace($json)) { @{} } else { ConvertTo-HashtableDeep ($json | ConvertFrom-Json) }

$backup = "$HooksPath.orbweaver-backup"
Copy-Item -LiteralPath $HooksPath -Destination $backup -Force
Write-Host "Backed up hooks to $backup"

if (-not $config.ContainsKey('hooks') -or $null -eq $config['hooks']) { $config['hooks'] = @{} }
$hooks = $config['hooks']

# Which Codex event drives which orb state.
#
# PostToolUse has no counterpart in the Claude Code table. It undoes an amber
# that turned out not to need answering: Codex fires PermissionRequest before it
# decides whether to actually ask, so a call that is then auto-approved would
# otherwise leave the orb saying "needs you" until the turn ended.
$wanted = @(
    @{ Event = 'SessionStart';      Matcher = $null; State = 'idle';       Async = $false }
    @{ Event = 'UserPromptSubmit';  Matcher = $null; State = 'generating'; Async = $false }
    @{ Event = 'PreToolUse';        Matcher = '.*';  State = 'generating'; Async = $false }
    @{ Event = 'PermissionRequest'; Matcher = $null; State = 'waiting';    Async = $true  }
    @{ Event = 'PostToolUse';       Matcher = '.*';  State = 'generating'; Async = $false }
    @{ Event = 'Stop';              Matcher = $null; State = 'idle';       Async = $false }
    @{ Event = 'SessionEnd';        Matcher = $null; State = 'ended';      Async = $false }
)

# Strip our own entries wherever they appear. Matched on the filename rather
# than the full path, so a config written by an older version -- or carried over
# by the /import command in Codex, which points at a .claude path -- is still
# recognised as ours and replaced instead of left to fire twice. Either
# filename, either extension: the pre-rename ClaudeBuddyHook.* entries are the
# upgrade's re-wire, and an /import from macOS carries the .sh spelling.
# -like is case-insensitive, as a Windows path is.
#
# $event is an automatic variable in PowerShell; using it as a loop variable
# here would shadow it and can misbehave.
function Test-OurCommand($value) {
    ($value -like '*ClaudeBuddyHook.*') -or ($value -like '*OrbweaverHook.*')
}

foreach ($eventName in @($hooks.Keys)) {
    $groups = @($hooks[$eventName])
    $kept = @()

    foreach ($group in $groups) {
        if ($null -eq $group) { continue }

        $inner = @(@($group['hooks']) | Where-Object {
            $_ -and -not (Test-OurCommand $_['command']) `
                 -and -not (Test-OurCommand $_['commandWindows'])
        })

        if ($inner.Count -gt 0) { $group['hooks'] = $inner; $kept += $group }
    }

    if ($kept.Count -gt 0) { $hooks[$eventName] = $kept } else { $hooks.Remove($eventName) }
}

if (-not $Uninstall) {
    # TEMP is baked in at wiring time for the reason OrbweaverHook.ps1's own
    # comment gives: a hook invoked through an interop shell cannot be trusted
    # to have TEMP set, and without it the script writes its status file
    # somewhere the app never looks -- with no visible error.
    #
    # TrimEnd('\') as install-windows-hooks.ps1 already does (CB-316):
    # GetTempPath() ends in a backslash, so the wired argument ended `\"`, which
    # PowerShell's -File parsing reads as an escaped quote. The hook then got a
    # TempDir with a stray quote and wrote no status file at all -- measured on
    # the Windows box: the untrimmed form exits 0 and writes nothing, the
    # trimmed form writes the file. It also keeps the wired command identical
    # across installs, which is what Codex's trust review hashes.
    $temp = ([System.IO.Path]::GetTempPath()).TrimEnd('\')

    foreach ($entry in $wanted) {
        $command = "powershell -NoProfile -ExecutionPolicy Bypass -File `"$installed`" " +
                   "-Agent codex -State $($entry.State) -TempDir `"$temp`""

        # commandWindows as well as command: Codex accepts both, and a config
        # that names only the POSIX form would do nothing here. Both are set to
        # the same thing rather than left to a default, so what fires is what
        # this file says.
        $handler = @{ type = 'command'; command = $command; commandWindows = $command }
        if ($entry.Async) { $handler['async'] = $true }

        $group = @{ hooks = @($handler) }
        if ($entry.Matcher) { $group['matcher'] = $entry.Matcher }

        $existing = if ($hooks.ContainsKey($entry.Event)) { @($hooks[$entry.Event]) } else { @() }
        $hooks[$entry.Event] = @($existing) + @($group)
    }
}

$config['hooks'] = $hooks
if ($config['hooks'].Keys.Count -eq 0) { $config.Remove('hooks') }

# UTF-8 *without* a BOM, for the reason install-windows-hooks.ps1 gives: the
# parsers on the other end treat a leading BOM as an invalid start of value, and
# PowerShell 5.1's Set-Content adds one by default.
$out = $config | ConvertTo-Json -Depth 20
[System.IO.File]::WriteAllText($HooksPath, $out, (New-Object System.Text.UTF8Encoding($false)))

if ($Uninstall) {
    Write-Host "Removed Orbweaver hooks from $HooksPath"
    Write-Host "The installed hook script was left in place; delete $HookDir (and $legacyHookDir, if present) if you want it gone."
    exit 0
}

Write-Host "Wired $($wanted.Count) hook entries into $HooksPath"
Write-Host ''
# The part people get stuck on, and the reason this prints a paragraph rather
# than "done". Codex does not run a hook it has not been told to trust, and a
# hooks.json written by anything other than Codex starts out untrusted. Until
# that is done there is no error anywhere: no hook fires, no orb appears, and
# the app looks broken.
Write-Host 'One more step, and nothing works without it:'
Write-Host ''
Write-Host '  Codex will not run a hook it has not been told to trust, and a hooks.json'
Write-Host '  written by anything other than Codex itself starts out untrusted. Start'
Write-Host '  Codex and accept the hook review it shows you, or run /hooks inside it'
Write-Host '  and trust the Orbweaver entries.'
Write-Host ''
Write-Host '  Editing hooks.json later - including re-running this installer - changes'
Write-Host '  its hash and asks you again.'
Write-Host ''
Write-Host 'Then restart any running Codex sessions: hooks are read at session start.'
