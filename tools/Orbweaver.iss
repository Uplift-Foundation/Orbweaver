; Inno Setup script for the Orbweaver Windows installer.
;
; Build it with (from the repo root):
;   dotnet publish Orbweaver.csproj -c Release -r win-x64 -p:DebugType=none
;   iscc /DVersion=0.1.0-beta tools\Orbweaver.iss
;
; tools\build-windows-installer.ps1 wraps both steps and reads the version out
; of the csproj, which is what CI calls.
;
; Deliberately a per-user install: it goes under %LOCALAPPDATA%, needs no
; administrator rights, and therefore raises no UAC prompt. This is a menu-bar
; style utility that only ever touches the current user's Claude Code config, so
; a machine-wide install would buy nothing and cost an elevation dialog.

#ifndef Version
  #define Version "0.0.0-dev"
#endif

#define AppName "Orbweaver"
; The name every install before the rename used. Only the upgrade cleanup below
; refers to it: the Start Menu group, Startup shortcut and keep-alive task
; that an old install left behind are all keyed on it.
#define LegacyAppName "Claude Buddy"
#define AppPublisher "Kawika Miller and Repo Owner"
#define AppUrl "https://github.com/Uplift-Foundation/Orbweaver"
; The publish output, named after the csproj's <AssemblyName>.
#define AppExe "Orbweaver.exe"
; What every install before CB-256 shipped as the app's exe. An upgrade keeps
; the old install directory, so this file is still sitting beside the new one
; and, on a machine where the app is running, still executing: see
; [InstallDelete] and PrepareToInstall below. Never installed again.
#define LegacyAppExe "ClaudeBuddy.exe"

; CFBundleVersion's Windows equivalent: VersionInfoVersion must be a plain
; dotted number, so strip any prerelease label for it while the user-visible
; AppVersion keeps the full label. Cutting at the first hyphen handles -rc.1 and
; anything else semver allows, not just -beta.
#if Pos("-", Version) > 0
  #define NumericVersion Copy(Version, 1, Pos("-", Version) - 1)
#else
  #define NumericVersion Version
#endif

[Setup]
; Never change AppId — it is how Windows recognises an existing install and
; upgrades it in place instead of stacking a second copy in Apps & Features.
; It is what makes the Claude Buddy -> Orbweaver rename an upgrade rather than
; a second app.
AppId={{4046CFD2-79A9-4270-8302-21B87A92C0A5}
AppName={#AppName}
AppVersion={#Version}
AppVerName={#AppName} {#Version}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#NumericVersion}

; Per-user, no elevation. Note the install directory is \Programs\Orbweaver,
; deliberately distinct from the %LOCALAPPDATA%\Orbweaver that
; install-windows-hooks.ps1 copies the hook script into. If they were the same
; folder, that script would try to copy the hook onto itself and fail.
;
; Only a fresh install lands in Programs\Orbweaver. An upgrade from Claude Buddy
; stays in Programs\ClaudeBuddy, because UsePreviousAppDir (default yes) reuses
; the directory the AppId was last installed to -- moving it would strand every
; path the old install handed out.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\Orbweaver
DisableProgramGroupPage=yes
DefaultGroupName={#AppName}
; Unlike the directory, the Start Menu group must not be reused: with the
; default (yes), an upgrade from Claude Buddy would put Orbweaver's shortcuts
; back into a "Claude Buddy" group -- the very folder [InstallDelete] below
; removes -- and the Start Menu would keep the old name forever.
UsePreviousGroup=no

; win-x64 self-contained publish; there is no 32-bit build to fall back to.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; A .txt copy, made by build-windows-installer.ps1: Inno decides between plain
; text and RTF by extension, and the repo's LICENSE has none.
LicenseFile=..\dist\LICENSE.txt
OutputDir=..\dist
OutputBaseFilename=Orbweaver-{#Version}-win-x64-setup
SetupIconFile=..\Assets\Orbweaver.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

; The app has no main window — it lives in the notification area — so a plain
; "close the app" request has nothing to close. Restart Manager detects the file
; lock on Orbweaver.exe and terminates it, which is what makes upgrading over
; a running copy work instead of failing on a locked file.
;
; Restart Manager only asks about files this installer is about to write, so it
; cannot see a running pre-rename ClaudeBuddy.exe at all; PrepareToInstall in
; [Code] stops that one (and this one, belt and braces) by hand.
CloseApplications=force
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; Checked by default and called out in its own wizard page, because an install
; without it produces an app that runs correctly and displays nothing, which
; reads as broken software rather than an unfinished setup.
Name: "wirehooks"; Description: "Wire up agent hooks for Claude Code and Codex (required for orbs to appear)"; GroupDescription: "Setup:"
; Only offered on a machine that actually has WSL; a plain file check is
; enough here since this runs at Windows-install time, not inside WSL itself.
; Deliberately a sub-option of "wirehooks" rather than independent: it does
; nothing if that box is unchecked (see WireUpHooks below), which is a
; simpler, safer default than trying to make WSL wiring stand alone.
Name: "wirewslhooks"; Description: "Also wire up hooks for Claude Code running under WSL"; GroupDescription: "Setup:"; Check: WslIsInstalled
Name: "startup"; Description: "Start {#AppName} automatically when I sign in"; GroupDescription: "Setup:"

[Files]
Source: "..\bin\Release\net10.0\win-x64\publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
; The hook script sits at {app}\ and its installers at {app}\tools\, mirroring
; the repo layout. Each installer resolves the hook as ..\OrbweaverHook.ps1
; relative to itself, so this layout is what makes them work unmodified.
;
; Three installers: one per CLI, and install-hooks.ps1 over the top of them,
; which is the only one anything else calls. Nobody should have to know which of
; two scripts their machine needs.
Source: "..\OrbweaverHook.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\tools\install-hooks.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "..\tools\install-windows-hooks.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "..\tools\install-codex-hooks.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "..\tools\install-grok-hooks.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
; Inno never removes what an earlier version installed and this script no
; longer declares, so an upgrade from Claude Buddy would leave its Start Menu
; group beside the new one and its Startup shortcut beside Orbweaver.lnk -- two
; entries under two names, and the app started twice at every sign-in (the
; second exits on the single-instance mutex, but it is still debris). This runs
; before [Icons] recreates anything, and on a fresh install it matches nothing.
;
; {autoprograms} rather than {userprograms}: it is where {group} resolved to,
; for whichever install mode the old install ran in.
Type: filesandordirs; Name: "{autoprograms}\{#LegacyAppName}"
Type: files; Name: "{userstartup}\{#LegacyAppName}.lnk"
; The source copy of the pre-rename hook in an upgraded install dir. Nothing
; runs it -- every wired hook entry points at the copy install-windows-hooks.ps1
; made under %LOCALAPPDATA%, which that script leaves alone for sessions still
; running against it -- so this is purely the old install's leftover.
Type: files; Name: "{app}\ClaudeBuddyHook.ps1"
; The pre-rename exe, likewise left in an upgraded install dir (CB-256). With
; the hook above, it is the only file an old install laid down that this one
; does not overwrite under the same name (read off v0.5.10-beta's [Files]); the
; publish is single-file with DebugType=none, so there is no .pdb, .deps.json
; or .runtimeconfig.json beside it either. Deleting it only succeeds because
; PrepareToInstall has stopped it first -- a running exe is locked, and Inno
; skips a failed delete here without saying so.
Type: files; Name: "{app}\{#LegacyAppExe}"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
; Full path to powershell.exe here and everywhere below. Claude Code itself
; invokes Windows PowerShell 5.1, which is what install-windows-hooks.ps1 is
; written against, and naming it explicitly avoids resolving to a pwsh 7 that
; happens to shadow it on PATH.
Name: "{group}\Wire up agent hooks"; \
  Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\install-hooks.ps1"""; \
  Comment: "Re-run hook setup, repair it after a reinstall, or pick up a CLI you added later"
Name: "{userstartup}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: startup

[Run]
; This installer adds no firewall rule, on purpose (CB-261).
;
; The peer link listens on a port, and a per-user install (PrivilegesRequired=lowest,
; with no override, so never an administrative install) cannot add a rule without
; elevation. It does not need to: the peer link is opt-in (PeerLinkEnabled defaults
; to off), so Windows Firewall's own first-listen prompt arrives exactly when a user
; turns it on, which is the right moment to ask. Pre-opening a port for a feature
; that is off would be the wrong default even where it could be done.
;
; History, because it explains debris that may still exist. Releases up to
; v0.5.8-beta ran netsh here ungated. From a desktop launch it exited 1 and changed
; nothing, but a Setup that inherited an elevated token (an administrator SSH
; session, say) succeeded, and the add-only entry stacked one legacy-named rule per
; upgrade. CB-213 then gated the entries on IsAdminInstallMode, which this installer
; can never be in, so they were dead code; CB-261 removed them.
;
; Leftover legacy-named rules are not removed by the installer, also on purpose: an
; ungated delete would bring back CB-213's exit-1-in-the-log problem and would only
; work by the accident of an elevated parent. They are inert (they name an exe that
; no longer exists) and are cleared once by hand, from an administrator prompt:
; netsh advfirewall firewall delete rule name="<the pre-rename name> peer link"
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName} now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; No firewall step here either: the installer never adds a rule (see [Run]).
; Take the hook entries back out of every CLI they were wired into, or the CLI
; keeps invoking a script that is about to be deleted and logs a hook error on
; every event. runhidden because an uninstall should not flash a console window.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\install-hooks.ps1"" -Uninstall"; \
  Flags: runhidden; RunOnceId: "unwirehooks"
; CB-49: the crash keep-alive task, if one was ever registered, goes with the
; app -- left behind it would name an exe that no longer exists, which is debris
; a user cannot interpret later. Unlike
; install time, uninstall doesn't need to check the setting first: the app is
; leaving either way, so the task comes out unconditionally -- under both its
; names, since the pre-rename one may survive an upgrade whose reconcile could
; not delete it.
Filename: "{sys}\schtasks.exe"; \
  Parameters: "/delete /tn ""OrbweaverCrashKeepAlive"" /f"; \
  Flags: runhidden skipifdoesntexist; RunOnceId: "removekeepalivetask"
Filename: "{sys}\schtasks.exe"; \
  Parameters: "/delete /tn ""ClaudeBuddyCrashKeepAlive"" /f"; \
  Flags: runhidden skipifdoesntexist; RunOnceId: "removelegacykeepalivetask"
; Restart Manager only runs during install, so stop a running instance here too.
; Full {sys} path rather than bare "taskkill.exe" — skipifdoesntexist tests the
; filename as given, and an unqualified name would not resolve.
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#AppExe}"; Flags: runhidden skipifdoesntexist; RunOnceId: "stopapp"

[Code]
function WslIsInstalled(): Boolean;
begin
  Result := FileExists(ExpandConstant('{sys}\wsl.exe'));
end;

{ CB-49: crash keep-alive, via a Scheduled Task that restarts the app when
  Windows logs an Application Error against it -- not a plain "Run" key,
  which only fires at logon and does nothing after a crash mid-session, and
  not the task's own "restart on failure" setting either, which only judges
  whether *launching* the task succeeded and has no way to notice that the
  long-running process it started later died. An event-log trigger is the
  standard way to get real crash-restart out of Task Scheduler for an
  ordinary EXE: Event ID 1000 from source "Application Error" fires whenever
  any process crashes, with the faulting executable's name in its AppName
  field, so the trigger below filters on that rather than firing for every
  crash on the machine.

  Gated on the same "Serve on launch" (Remote Control) setting macOS checks
  in install-hooks.sh, and for the same reason: a task that brings the app
  back after every exit, deliberate or not, is the "app that will not stay
  quit" CB-49 itself warns against, and only a machine already asked to keep
  serving should get that. ServeOnLaunchEnabled below does a hand-rolled
  substring check rather than a real JSON parse, because Pascal Script has
  no JSON support and this only needs one boolean out of the file. }
const
  KeepAliveTaskName = 'OrbweaverCrashKeepAlive';
  { What every install before the rename registered. Never re-created; only
    ever deleted, see ReconcileKeepAliveTask. }
  LegacyKeepAliveTaskName = 'ClaudeBuddyCrashKeepAlive';

function ServeOnLaunchEnabled(): Boolean;
var
  SettingsPath: String;
  Contents: AnsiString;
  Body: String;
  KeyPos, TruePos, FalsePos: Integer;
begin
  Result := False;
  { %APPDATA%\Orbweaver\settings.json -- OrbweaverSettings.Directory
    resolves via SpecialFolder.ApplicationData, which is roaming AppData on
    Windows, not the LocalAppData directory this installer itself lives
    under. Do not write an Inno constant in brace form inside this comment --
    comments don't nest, so a brace pair anywhere in here closes the comment
    at the first closing brace and leaves the rest to be parsed as code.

    Falls back to the pre-rename %APPDATA%\ClaudeBuddy\settings.json: on
    upgrade day this runs before the new app has ever started, and it is the
    app that moves that folder, so reading only the new path would reconcile
    the keep-alive against "not enabled" and silently remove it. }
  SettingsPath := ExpandConstant('{userappdata}\Orbweaver\settings.json');
  if not FileExists(SettingsPath) then
    SettingsPath := ExpandConstant('{userappdata}\ClaudeBuddy\settings.json');
  if not FileExists(SettingsPath) then Exit;
  if not LoadStringFromFile(SettingsPath, Contents) then Exit;

  Body := String(Contents);
  KeyPos := Pos('"remoteControlServeOnLaunch"', Body);
  if KeyPos = 0 then Exit;

  { Look only at what follows the key, so a same-named value elsewhere in the
    file (there isn't one today, but nothing guarantees that) can't be
    mistaken for this one. }
  Body := Copy(Body, KeyPos, Length(Body) - KeyPos + 1);
  TruePos := Pos('true', Body);
  FalsePos := Pos('false', Body);
  Result := (TruePos > 0) and ((FalsePos = 0) or (TruePos < FalsePos));
end;

function BuildKeepAliveTaskXml(ExePath: String): String;
begin
  { Task Scheduler's XML schema, not schtasks' own flag syntax -- event
    triggers with an XPath filter aren't expressible as plain /create flags,
    only via /xml. }
  { UTF-8, matching what SaveStringToFile actually writes below (an
    AnsiString, not a real UTF-16 buffer) -- fine for this task's own text,
    which is all ASCII. An install path containing non-ASCII characters
    (a non-Latin Windows username, most likely) is the one case this hasn't
    been verified against; this project's per-user install already puts
    that path under %LOCALAPPDATA%, so it inherits whatever that user's
    system codepage does with it. }
  Result :=
    '<?xml version="1.0" encoding="UTF-8"?>' + #13#10 +
    '<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">' + #13#10 +
    '  <RegistrationInfo>' + #13#10 +
    '    <Description>Restarts Orbweaver after a crash. Installed because "Serve on launch" (Remote Control) is turned on; removed if that is turned off and this installer is re-run, and always removed on uninstall.</Description>' + #13#10 +
    '  </RegistrationInfo>' + #13#10 +
    '  <Triggers>' + #13#10 +
    '    <EventTrigger>' + #13#10 +
    '      <Enabled>true</Enabled>' + #13#10 +
    '      <Subscription>&lt;QueryList&gt;&lt;Query Id="0" Path="Application"&gt;&lt;Select Path="Application"&gt;*[System[Provider[@Name=''Application Error''] and EventID=1000] and EventData[Data[@Name=''AppName'']=''{#AppExe}'']]&lt;/Select&gt;&lt;/Query&gt;&lt;/QueryList&gt;</Subscription>' + #13#10 +
    '    </EventTrigger>' + #13#10 +
    '  </Triggers>' + #13#10 +
    '  <Principals>' + #13#10 +
    '    <Principal id="Author">' + #13#10 +
    '      <LogonType>InteractiveToken</LogonType>' + #13#10 +
    '      <RunLevel>LeastPrivilege</RunLevel>' + #13#10 +
    '    </Principal>' + #13#10 +
    '  </Principals>' + #13#10 +
    '  <Settings>' + #13#10 +
    '    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>' + #13#10 +
    '    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>' + #13#10 +
    '    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>' + #13#10 +
    '    <StartWhenAvailable>true</StartWhenAvailable>' + #13#10 +
    '    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>' + #13#10 +
    '  </Settings>' + #13#10 +
    '  <Actions Context="Author">' + #13#10 +
    '    <Exec>' + #13#10 +
    '      <Command>' + ExePath + '</Command>' + #13#10 +
    '    </Exec>' + #13#10 +
    '  </Actions>' + #13#10 +
    '</Task>';
end;

{ Removes the named task unconditionally; schtasks exits nonzero for a task
  that isn't registered, which this treats the same as success -- "already
  gone" is the outcome either way. }
procedure RemoveKeepAliveTask(TaskName: String);
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\schtasks.exe'),
       '/delete /tn "' + TaskName + '" /f', '', SW_HIDE,
       ewWaitUntilTerminated, ResultCode);
end;

{ Reconciles the task against the current setting, the same shape as
  install-hooks.sh's reconcile_keepalive: on if the setting says on, off
  (idempotently) otherwise. Runs on every install and every upgrade, via
  CurStepChanged below, so flipping the setting and re-running this
  installer is how the task catches up with it on Windows -- there is no
  live, in-app equivalent of macOS's re-run-anytime install-hooks.sh here. }
procedure ReconcileKeepAliveTask();
var
  ResultCode: Integer;
  XmlPath, TaskXml: String;
begin
  { The pre-rename task goes first, whichever way the setting points: left in
    place beside a freshly created new one, a single crash would launch the
    app twice; left in place with the setting off, it is the "app that will not
    stay quit" this gate exists to prevent, under a name nothing else deletes. }
  RemoveKeepAliveTask(LegacyKeepAliveTaskName);

  if not ServeOnLaunchEnabled() then
  begin
    RemoveKeepAliveTask(KeepAliveTaskName);
    Exit;
  end;

  XmlPath := ExpandConstant('{tmp}\OrbweaverKeepAlive.xml');
  TaskXml := BuildKeepAliveTaskXml(ExpandConstant('{app}\{#AppExe}'));
  SaveStringToFile(XmlPath, TaskXml, False);

  if not Exec(ExpandConstant('{sys}\schtasks.exe'),
              '/create /tn "' + KeepAliveTaskName + '" /xml "' + XmlPath + '" /f',
              '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    MsgBox('Could not start schtasks.exe to register the crash keep-alive task.' + #13#10#13#10 +
           'Orbweaver is installed and will run either way; it just will not' + #13#10 +
           'restart itself automatically after a crash.',
           mbError, MB_OK);
  end;

  DeleteFile(XmlPath);
end;

{ Hook wiring runs from code rather than a [Run] entry so its exit code can be
  checked. A [Run] line would fail silently, and "hooks quietly not installed"
  is precisely the confusing failure this project already goes out of its way to
  avoid — the user would be left with an app that shows nothing and no clue why. }
procedure WireUpHooks();
var
  ResultCode: Integer;
  Script: String;
  Params: String;
begin
  Script := ExpandConstant('{app}\tools\install-hooks.ps1');
  Params := '-NoProfile -ExecutionPolicy Bypass -File "' + Script + '"';

  { -Wsl is forwarded to the Claude Code installer, whose own handling skips
    any distro it detects wsl.exe isn't ready for (see install-windows-hooks.ps1's
    timeout-guarded WSL discovery), so this can't turn a plain Windows-only
    install into a hang even on a machine where WSL is present but misbehaving.
    Codex has no WSL equivalent to forward it to. }
  if WizardIsTaskSelected('wirewslhooks') then
    Params := Params + ' -Wsl';

  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
              Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    MsgBox('Could not start PowerShell to wire up the agent hooks.' + #13#10#13#10 +
           'Orbweaver is installed and will run, but no orbs will appear until' + #13#10 +
           'the hooks are set up. Run this from a PowerShell prompt to finish:' + #13#10#13#10 +
           '  & "' + Script + '"',
           mbError, MB_OK);
    Exit;
  end;

  if ResultCode <> 0 then
    MsgBox('Hook setup failed (exit code ' + IntToStr(ResultCode) + ').' + #13#10#13#10 +
           'Orbweaver is installed and will run, but no orbs will appear until' + #13#10 +
           'the hooks are set up. Run this from a PowerShell prompt to see the error:' + #13#10#13#10 +
           '  & "' + Script + '"',
           mbError, MB_OK);
end;

{ CB-256: stop a running copy of the app before anything is installed.

  CloseApplications=force has always done this for the current exe, but
  Restart Manager is only asked about files this installer is about to write,
  and since the rename the pre-rename exe is not one of them. Without this, an
  upgrade over a running pre-rename build leaves that process running out of
  its locked exe: the delete in [InstallDelete] fails silently, the old process
  keeps the legacy single-instance mutex, and the new exe that the post-install
  launch (or the next sign-in) starts finds the mutex taken and exits 0. The
  user sees an upgrade that did nothing, still running the old build.

  taskkill /F is the same blunt instrument the uninstaller already uses, and
  no ruder than Restart Manager's force: the app has nothing it must flush on
  the way out. A forced termination does not log Event 1000, so the pre-rename
  keep-alive task does not bring the old exe straight back.

  taskkill returns before the process is actually gone, so this polls until
  tasklist no longer lists the image, for up to ten seconds. If it is still
  there after that -- most plausibly a copy belonging to another user, which a
  per-user install has no rights to stop -- setup carries on regardless and
  says so in the log; refusing to install would help nobody. }
function ImageIsRunning(Image: String): Boolean;
var
  ResultCode: Integer;
begin
  { tasklist exits 0 whether or not anything matched, so its output goes
    through find, which exits 0 only on a match. The no-match line it prints
    instead ("INFO: No tasks are running ...", localised) never contains the
    image name. A failure to start cmd at all reads as not running, which
    lets setup proceed exactly as it would have before this existed. }
  Result := Exec(ExpandConstant('{cmd}'),
                 '/C ""' + ExpandConstant('{sys}\tasklist.exe') + '" /NH /FI "IMAGENAME eq ' + Image + '" | "' +
                   ExpandConstant('{sys}\find.exe') + '" /I "' + Image + '""',
                 '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
            and (ResultCode = 0);
end;

procedure StopRunningImage(Image: String);
var
  ResultCode, Waited: Integer;
begin
  if not ImageIsRunning(Image) then
  begin
    Log(Image + ' is not running.');
    Exit;
  end;

  Log('Stopping ' + Image + ' before installing.');
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM ' + Image, '', SW_HIDE,
       ewWaitUntilTerminated, ResultCode);
  Log('taskkill /F /IM ' + Image + ' exited ' + IntToStr(ResultCode) + '.');

  Waited := 0;
  while ImageIsRunning(Image) and (Waited < 10000) do
  begin
    Sleep(250);
    Waited := Waited + 250;
  end;

  if ImageIsRunning(Image) then
    Log('Warning: ' + Image + ' is still running; continuing anyway.')
  else
    Log(Image + ' has stopped.');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningImage('{#LegacyAppExe}');
  StopRunningImage('{#AppExe}');
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  { ssPostInstall, not ssInstall: the script has to be on disk before it runs. }
  if CurStep = ssPostInstall then
  begin
    if WizardIsTaskSelected('wirehooks') then
      WireUpHooks();

    { Unconditional (no task checkbox): this reconciles against the setting
      itself, so an upgrade with the box unavailable (it's not offered again
      on a repair/upgrade run) still catches up with a setting the user
      changed since the last install. }
    ReconcileKeepAliveTask();
  end;
end;
