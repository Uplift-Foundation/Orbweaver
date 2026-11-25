# The Windows half of the bug-fix org's machine runner (CB-470). windows.py
# copies this file to the PC and runs it with `powershell -File`, never with
# arguments typed into the ssh shell: that shell is Git Bash, and Git Bash
# rewrites anything shaped like `/FLAG` into a path, which is every Inno Setup
# switch.
#
#   -Action stamp     print ProductVersion of the installed Orbweaver.exe
#                     ("1.0.13+<40 hex>"), or nothing when none is installed
#   -Action keep      copy the install folder aside, once per hold
#   -Action install   -Setup <path>: stop the app, silent install, start it
#   -Action restore   put the kept folder back exactly, start the app, drop it
param(
  [Parameter(Mandatory = $true)][ValidateSet('stamp', 'keep', 'install', 'restore')][string]$Action,
  [string]$Setup,
  # CB-475: names this call in done-<action>.txt, so the runner can tell this
  # call's result from an earlier one's.
  [string]$Nonce = ''
)
$ErrorActionPreference = 'Stop'
$App = Join-Path $env:LOCALAPPDATA 'Programs\Orbweaver'
$Exe = Join-Path $App 'Orbweaver.exe'
$Keep = Join-Path $env:LOCALAPPDATA 'Orbweaver-machine-keep'
$Task = 'OrbweaverMachineRunnerStart'

function Get-Stamp {
  if (Test-Path $Exe) { (Get-Item $Exe).VersionInfo.ProductVersion }
}

function Stop-App {
  # Stop-Process raises no crash event, so the installer's keep-alive task
  # (which fires on Application Error 1000) does not restart it under us.
  Get-Process Orbweaver -ErrorAction SilentlyContinue | Stop-Process -Force
  $deadline = (Get-Date).AddSeconds(30)
  while ((Get-Process Orbweaver -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
  }
  if (Get-Process Orbweaver -ErrorAction SilentlyContinue) { throw 'Orbweaver did not stop within 30 s' }
}

function Start-App {
  # A process started from ssh lands in session 0, where nobody can see a tray
  # icon. A scheduled task with an Interactive principal starts it in the
  # console session instead. The bare user name matters: DOMAIN\user fails.
  $action = New-ScheduledTaskAction -Execute $Exe
  $principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive
  Register-ScheduledTask -TaskName $Task -Action $action -Principal $principal -Force | Out-Null
  Start-ScheduledTask -TaskName $Task
}

function Copy-Mirror($from, $to) {
  robocopy $from $to /MIR /NFL /NDL /NJH /NJS /NP /R:2 /W:2 | Out-Null
  # robocopy's exit code is a bit field; 8 and above is a failure.
  if ($LASTEXITCODE -ge 8) { throw "robocopy $from -> $to exited $LASTEXITCODE" }
  $global:LASTEXITCODE = 0
}

# CB-475: ssh to this PC can stay open for minutes after the script has
# finished (seen after installs and restores that start the app; cause not
# diagnosed). So the outcome is also written to done-<action>.txt beside this
# script, first line "<nonce> ok" or "<nonce> failed", then the output. When
# ssh hangs, the runner cuts it off and reads this instead of waiting.
$Done = Join-Path (Split-Path -Parent $PSCommandPath) "done-$Action.txt"
function Write-Done($state, $text) {
  if ($Nonce) { [IO.File]::WriteAllText($Done, "$Nonce $state`n$text") }
}
# A run of this script cut off by the runner may still be going (CB-475): an
# install or restore must not race one. Any other powershell running this
# script, and any installer it started, is stopped first.
function Stop-EarlierRuns {
  $me = $PID
  Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
    Where-Object { $_.ProcessId -ne $me -and $_.CommandLine -match 'ow-machine\.ps1' } |
    ForEach-Object {
      Get-CimInstance Win32_Process -Filter "ParentProcessId=$($_.ProcessId)" |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
      Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
    }
}

# Test seam for the real-machine proof (CB-475), inert unless a person creates
# stall-<action>.txt beside this script on the PC: "before <s>" sleeps before
# the work (a genuinely stuck action), "after <s>" sleeps after the outcome is
# recorded (the observed hang: work done, channel still open). One-shot: the
# file is deleted when read.
$Stall = Join-Path (Split-Path -Parent $PSCommandPath) "stall-$Action.txt"
$StallWhen, $StallFor = '', 0
if (Test-Path $Stall) {
  $StallWhen, $StallFor = ((Get-Content $Stall -Raw).Trim() -split '\s+')
  Remove-Item $Stall -Force
}

try {
if ($Action -in 'install', 'restore') { Stop-EarlierRuns }
if ($StallWhen -eq 'before') { Start-Sleep -Seconds ([int]$StallFor) }
$Out = & { switch ($Action) {
  'stamp' { Get-Stamp }
  'keep' {
    # Once per hold: a second install for a new head must not keep aside the
    # first install's build over the one that was there before the hold.
    if (Test-Path $Keep) { 'already kept'; break }
    if (-not (Test-Path $Exe)) { 'nothing installed to keep'; break }
    # Copied under a temporary name and renamed, so a copy cut short is never
    # mistaken for a complete one by the next restore.
    if (Test-Path "$Keep.tmp") { Remove-Item "$Keep.tmp" -Recurse -Force }
    Copy-Mirror $App "$Keep.tmp"
    Rename-Item "$Keep.tmp" $Keep
    "kept $(Get-Stamp)"
  }
  'install' {
    $setupPath = (Resolve-Path $Setup).Path
    Stop-App
    # No /CLOSEAPPLICATIONS: from session 0 it waits on a dialog nobody sees.
    $log = Join-Path (Split-Path $setupPath) 'setup.log'
    $p = Start-Process -FilePath $setupPath -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$log`"" -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "setup exited $($p.ExitCode); log at $log" }
    Start-App
    "installed $(Get-Stamp)"
  }
  'restore' {
    if (-not (Test-Path $Keep)) { 'nothing kept aside'; break }
    Stop-App
    Copy-Mirror $Keep $App
    Remove-Item $Keep -Recurse -Force
    Start-App
    "restored $(Get-Stamp)"
  }
} }
} catch {
  Write-Done 'failed' $_.Exception.Message
  throw
}
Write-Done 'ok' (($Out | Out-String).Trim())
$Out
if ($StallWhen -eq 'after') { Start-Sleep -Seconds ([int]$StallFor) }
