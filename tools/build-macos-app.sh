#!/usr/bin/env bash
# Builds "Orbweaver.app" — a real macOS app bundle you can double-click,
# drop in /Applications, and add to Login Items.
#
# The bundle was "Claude Buddy.app" until CB-255. What did NOT change with it,
# deliberately: the bundle id (see BUNDLE_ID below — Automation consent is
# keyed on it). The executable inside followed in phase 3 (CB-256): it is the
# assembly name, Contents/MacOS/Orbweaver now, and was Contents/MacOS/ClaudeBuddy
# in every build before — including the phase-2 Orbweaver.app, which is why
# --install knows three executable paths rather than two (see INSTALLED_EXE).
# --install removes the legacy bundle as well as the current one, so the two
# never sit side by side under one bundle id.
#
#   ./tools/build-macos-app.sh              # build into dist/
#   ./tools/build-macos-app.sh --install    # ...and copy to /Applications
#   ./tools/build-macos-app.sh --rid osx-x64
#
# Signing: set MACOS_SIGNING_IDENTITY to a "Developer ID Application: ..."
# identity in the keychain to produce a distributable, notarizable build.
# Without it the bundle is ad-hoc signed, which is fine locally but cannot be
# notarized or opened on someone else's Mac without a Gatekeeper override.
# tools/build-macos-dmg.sh is the packaging entry point and sets this up for you.
#
# Why a bundle rather than the bare published binary:
#   * Finder/Dock/Login Items treat it as an application.
#   * LSUIElement makes it a menu-bar-only app at the OS level (no Dock icon,
#     no app switcher entry) instead of relying on an Avalonia setting.
#   * It carries NSAppleEventsUsageDescription, which macOS requires before
#     it will even show the Automation prompt that click-to-focus needs.
#   * A bundle has a stable code identity, so the Automation permission you
#     grant sticks to this app instead of to whatever terminal happened
#     to launch a loose binary.

set -euo pipefail

cd "$(dirname "$0")/.."

APP_NAME="Orbweaver"
# The name every build before CB-255 installed under. Only --install reads it,
# to stop and remove a copy still sitting in /Applications.
LEGACY_APP_NAME="Claude Buddy"
# The bundle's executable, Contents/MacOS/$EXECUTABLE. Not a free choice: it is
# the file `dotnet publish` names after <AssemblyName> in Orbweaver.csproj, and
# CFBundleExecutable has to name that file. Equal to APP_NAME by coincidence of
# the rename, not by definition, so it is spelled out on its own (CB-256).
EXECUTABLE="Orbweaver"
# Kept as-is even though the canonical repo is Uplift-Foundation/Orbweaver, and
# deliberately so: macOS keys the Automation (Apple Events) consent a user grants
# to the bundle identifier. Renaming it makes every existing install look like a
# brand-new app, so click-to-focus silently stops working until each user
# re-approves it in System Settings — and that grant is invisible when it breaks,
# which cost a long debugging session once already (see the Automation note in the
# README). Not worth it for a cosmetic rename. The signing certificate belongs to
# the UPLIFT FOUNDATION team, so it signs this identifier regardless of which
# repository builds it. If it is ever renamed, do it in a release whose notes say
# plainly that click-to-focus needs re-approving.
BUNDLE_ID="io.github.wtvamp.claudebuddy"
DIST="dist"
INSTALL=0

# The csproj owns the version; parse it out rather than keeping a second copy
# here that would silently drift from the one compiled into the binary.
VERSION="$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' Orbweaver.csproj | head -1)"
[[ -n "$VERSION" ]] || { echo "Could not read <Version> from Orbweaver.csproj" >&2; exit 1; }

# CFBundleVersion must be a plain dotted number — a "-beta" suffix in it makes
# the bundle unlaunchable — so strip any prerelease label for that key while
# CFBundleShortVersionString keeps the full label users should see.
VERSION_NUMERIC="${VERSION%%-*}"

SIGN_IDENTITY="${MACOS_SIGNING_IDENTITY:-}"
ENTITLEMENTS="tools/Orbweaver.entitlements"

# Default to this Mac's architecture; override for cross-building.
case "$(uname -m)" in
  arm64) RID="osx-arm64" ;;
  *)     RID="osx-x64" ;;
esac

while [[ $# -gt 0 ]]; do
  case "$1" in
    --install) INSTALL=1; shift ;;
    --rid) RID="$2"; shift 2 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done

APP="$DIST/$APP_NAME.app"
CONTENTS="$APP/Contents"

echo "==> Publishing ($RID)"
# Multi-file on purpose: PublishSingleFile (the csproj default, for handing
# someone one loose executable) would self-extract native libs to a temp dir
# at every launch, which is exactly what a .app bundle exists to avoid.
dotnet publish Orbweaver.csproj -c Release -r "$RID" \
  -p:PublishSingleFile=false \
  -p:DebugType=none \
  -o "$DIST/publish-$RID" \
  --nologo -v quiet

echo "==> Assembling $APP"
rm -rf "$APP"
mkdir -p "$CONTENTS/MacOS" "$CONTENTS/Resources"
cp -R "$DIST/publish-$RID/." "$CONTENTS/MacOS/"
chmod +x "$CONTENTS/MacOS/$EXECUTABLE"

# Ship the hook and both its installers inside the bundle. Orbs only appear
# once the hook is wired into the CLI's own config, so carrying all three means
# a user who downloaded a DMG (and has no clone) can still finish setup with one
# command against a stable path under /Applications. Both installers prefer a
# hook script sitting alongside them, which is this layout.
#
# One hook script, two per-CLI installers, and install-hooks.sh over the top of
# them. The script is the same for Claude Code and Codex and only takes a
# different first argument, but where it has to be registered is completely
# different — settings.json under ~/.claude for one, hooks.json under
# $CODEX_HOME for the other, with a trust step that only the second has.
#
# install-hooks.sh is what every install path calls, and the per-CLI ones are
# what it calls. Nobody should have to know which of two scripts their machine
# needs; that knowledge belongs in a script, not in a README step someone reads
# once.
cp OrbweaverHook.sh \
   tools/install-hooks.sh \
   tools/install-macos-hooks.sh \
   tools/install-codex-hooks.sh \
   tools/install-grok-hooks.sh \
   tools/install-skill.sh "$CONTENTS/Resources/"
chmod +x "$CONTENTS/Resources/OrbweaverHook.sh" \
         "$CONTENTS/Resources/install-hooks.sh" \
         "$CONTENTS/Resources/install-macos-hooks.sh" \
         "$CONTENTS/Resources/install-codex-hooks.sh" \
         "$CONTENTS/Resources/install-grok-hooks.sh" \
         "$CONTENTS/Resources/install-skill.sh"

# The orbweaver-team skill (CB-291), which install-skill.sh copies into each
# Claude Code account. Beside the installers, where they look for it first.
rm -rf "$CONTENTS/Resources/skills"
mkdir -p "$CONTENTS/Resources/skills"
cp -R skills/orbweaver-team "$CONTENTS/Resources/skills/"
chmod +x "$CONTENTS/Resources/skills/orbweaver-team/orbweaver-team.sh"

echo "==> Building icon"
ICONSET="$DIST/$APP_NAME.iconset"
rm -rf "$ICONSET"; mkdir -p "$ICONSET"
for size in 16 32 128 256 512; do
  sips -z $size $size Assets/appicon-1024.png \
    --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z $double $double Assets/appicon-1024.png \
    --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$CONTENTS/Resources/$APP_NAME.icns"
rm -rf "$ICONSET"

cat > "$CONTENTS/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>              <string>$APP_NAME</string>
    <key>CFBundleDisplayName</key>       <string>$APP_NAME</string>
    <key>CFBundleIdentifier</key>        <string>$BUNDLE_ID</string>
    <key>CFBundleExecutable</key>        <string>$EXECUTABLE</string>
    <key>CFBundleIconFile</key>          <string>$APP_NAME</string>
    <key>CFBundlePackageType</key>       <string>APPL</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>CFBundleVersion</key>           <string>$VERSION_NUMERIC</string>
    <key>LSMinimumSystemVersion</key>    <string>11.0</string>
    <key>NSHighResolutionCapable</key>   <true/>
    <!-- Menu-bar-only: no Dock icon, no Cmd-Tab entry. -->
    <key>LSUIElement</key>               <true/>
    <!-- Shown in the Automation prompt the first time an orb is clicked, and
         again for Claude Desktop the first time a profile is quit from the
         menu (quitting sends a quit Apple Event, which is TCC-gated). -->
    <key>NSAppleEventsUsageDescription</key>
    <string>$APP_NAME uses automation to bring the terminal window of a Claude Code session to the front when you click its orb, and to quit a Claude Desktop profile when you choose Quit from its menu.</string>
    <!-- Shown the moment PvRecorder opens the input device, which only
         happens if the user has turned on voice input in Settings and then
         clicks the mic that appears on hover — see VoiceRecorder. Without
         this key macOS kills the process instead of prompting, so it has to
         be here before that feature can work at all, the same way the
         Automation key above has to exist before click-to-focus can prompt.
         Typing the transcribed text into the terminal is a second, separate
         permission (Accessibility, for the "System Events keystroke"
         AppleScript in TerminalFocuser.SendText) — TCC prompts for that one
         itself the first time it's needed, with no Info.plist key of its
         own, but it's tied to this bundle's code identity exactly like the
         Automation grant is, so the same "a rebuild can invalidate it"
         caveat above applies to it too. -->
    <!-- Claude Desktop's own schemes, declared here so Claude Buddy is
         *eligible* to handle them. Being eligible is not the same as being
         chosen: Claude Desktop declares them too, so the default is claimed
         explicitly at runtime (MacOSUrlScheme), and only once there is more
         than one profile to route between.

         Why claim them at all: LaunchServices resolves a scheme to a bundle
         id, and every tinted clone Claude Buddy makes is a byte-identical copy
         of Claude.app — same id, same claimed schemes. So the id cannot say
         which profile a link belongs to, and a LaunchServices launch carries
         neither --user-data-dir nor CLAUDE_USER_DATA_DIR — only our own `open`
         ever passes them — which means every sign-in callback lands in the
         Default profile no matter which profile started it. See
         ClaudeDesktopUrlRouting for the full story. The msauth scheme is the
         Microsoft sign-in callback, and is why this shows up as "I can't log
         in" rather than only as a stray window.

         No backticks anywhere in this block: the heredoc below is unquoted so
         that $APP_NAME and friends expand, which means backticks in a comment
         are run as a command substitution. One here cost a build. -->
    <key>CFBundleURLTypes</key>
    <array>
        <dict>
            <key>CFBundleURLName</key>
            <string>Claude</string>
            <key>CFBundleURLSchemes</key>
            <array>
                <string>claude</string>
                <string>msauth.com.anthropic.claudefordesktop</string>
            </array>
            <!-- Viewer, not Editor: these schemes are Anthropic's, and this app
                 forwards them rather than owning them. -->
            <key>CFBundleTypeRole</key>
            <string>Viewer</string>
        </dict>
    </array>
    <key>NSMicrophoneUsageDescription</key>
    <string>$APP_NAME uses the microphone to transcribe what you say, entirely on this machine, when you click the mic that appears on hovering an orb — only after you turn voice input on in Settings.</string>

    <!-- Required before the app may open a socket to, or listen for, another
         machine on the local network. Without it the peer link fails as
         EHOSTUNREACH, which reads as an ordinary network fault and sends
         everybody to check the wrong thing — see CB-38, where exactly that cost
         hours against the OpenClaw gateway.

         Two things about this permission are worth remembering, because both
         have already bitten:

           * The grant is tied to the app's code identity. Replacing
             /Applications/Orbweaver.app gives the bundle a new CDHash,
             macOS re-evaluates, and the grant DOES NOT carry over. It breaks on
             every upgrade.
           * Nothing prompts loudly enough to notice, because LSUIElement is
             true: no Dock icon and no window. -->
    <key>NSLocalNetworkUsageDescription</key>
    <string>$APP_NAME connects directly to your other machines running $APP_NAME, so you can see and reply to sessions on them. It talks only to machines you have paired, over an encrypted connection, and nothing leaves your network.</string>
</dict>
</plist>
PLIST

# Sign from the inside out, nested code first, bundle last. `codesign --deep`
# looks like it does this in one step but Apple explicitly does not support it
# for distribution: it applies the *bundle's* entitlements to every nested
# binary and seals in an order the notary service rejects. A self-contained
# publish drops ~16 dylibs plus the `createdump` helper next to the executable,
# and an unsigned nested Mach-O is a hard notarization failure, so each one has
# to be signed on its own.
if [[ -n "$SIGN_IDENTITY" ]]; then
  echo "==> Signing (Developer ID: $SIGN_IDENTITY)"
  SIGN_ARGS=(--force --timestamp --options runtime --sign "$SIGN_IDENTITY")
else
  echo "==> Signing (ad-hoc — not distributable, see --help)"
  # No --timestamp or --options runtime: a timestamp needs Apple's server and
  # the hardened runtime without a real identity just breaks the app locally.
  SIGN_ARGS=(--force --sign -)
fi

# Everything in Contents/MacOS, not just the obvious binaries. Contents/MacOS is
# the bundle's *executable* directory, so codesign treats every file in it as
# nested code and refuses to seal the bundle while any one of them is unsigned
# ("code object is not signed at all"). For a self-contained .NET publish that
# means all ~200 files: the dylibs, the extensionless `createdump` helper, the
# managed .dll assemblies (PE32, which sign as Format=generic), and even
# Orbweaver.runtimeconfig.json — .NET's apphost requires those sit next to the
# executable, so they cannot be moved to Resources/ to sidestep this.
#
# The main executable is skipped: signing the bundle covers it, and it has to
# come last so the entitlements land on it.
#
# Resources/ is untouched by this loop on purpose. It is not an executable
# directory, so the hook scripts there are sealed as ordinary resources.
MAIN_EXE="$APP/Contents/MacOS/$EXECUTABLE"
while IFS= read -r nested; do
  [[ "$nested" == "$MAIN_EXE" ]] && continue
  codesign "${SIGN_ARGS[@]}" "$nested" >/dev/null 2>&1 ||
    { echo "    failed to sign $nested" >&2; exit 1; }
done < <(find "$APP/Contents/MacOS" -type f -print)

# The bundle last, and only here do the entitlements apply — they describe what
# the app as a whole is allowed to do (JIT, unsigned exec memory, Apple Events).
if [[ -n "$SIGN_IDENTITY" ]]; then
  codesign "${SIGN_ARGS[@]}" --entitlements "$ENTITLEMENTS" "$APP"
  echo "==> Verifying signature"
  codesign --verify --strict --deep --verbose=1 "$APP"
else
  codesign "${SIGN_ARGS[@]}" "$APP" 2>/dev/null
fi

rm -rf "$DIST/publish-$RID"

echo "==> Built $APP"
if [[ $INSTALL -eq 1 ]]; then
  INSTALLED_APP="/Applications/$APP_NAME.app"
  # Three executable paths are in the wild, and every stop and scan below
  # has to know all of them (CB-256):
  #
  #   INSTALLED_EXE  Orbweaver.app/Contents/MacOS/Orbweaver      this build
  #   INTERIM_EXE    Orbweaver.app/Contents/MacOS/ClaudeBuddy    phase 2 (CB-255)
  #   LEGACY_EXE     Claude Buddy.app/Contents/MacOS/ClaudeBuddy before CB-255
  #
  # The interim one is the easy one to forget: it sits in the bundle this
  # install replaces, under the bundle name this build already has, so
  # nothing about the folder says it is old. Left out, a phase-2 copy keeps
  # running, holds the single-instance mutex, and the new one exits 0 —
  # CB-206 again.
  INSTALLED_EXE="$INSTALLED_APP/Contents/MacOS/$EXECUTABLE"
  INTERIM_EXE="$INSTALLED_APP/Contents/MacOS/ClaudeBuddy"
  # The pre-CB-255 bundle. Same bundle id, so left in place it is a second
  # copy LaunchServices and a login item can pick instead of this one — see
  # MacOSLegacyBundle.cs, which does the same cleanup for people who install
  # by dragging from the DMG.
  LEGACY_APP="/Applications/$LEGACY_APP_NAME.app"
  LEGACY_EXE="$LEGACY_APP/Contents/MacOS/ClaudeBuddy"
  KEEPALIVE_PLIST="$HOME/Library/LaunchAgents/$BUNDLE_ID.plist"
  # The pids of Buddies running from any of the given executable paths.
  #
  # Never fails, and that is load-bearing. It is only ever called inside
  # `$(...)`, and under `set -euo pipefail` a failing substitution in an
  # assignment ends the script: `pgrep` exits 1 when nothing matches, and
  # the loop's last `[[ ]] && echo` returns 1 when the last pid is not ours.
  # So a fresh install with no Buddy running — the ordinary first install,
  # or a machine where launchd had not started the new one yet — exited 1
  # straight after a successful install, before the 0/1/many report below,
  # whose own "launch it with" arm could therefore never print. (CB-206's
  # install tail; found installing develop at 0e09981d, fixed under CB-245.)
  #
  # Both process names, because `pgrep -x` matches the executable's basename:
  # $EXECUTABLE for this build, ClaudeBuddy for every build before it (the
  # legacy bundle and the phase-2 one alike). Asking for one name only makes
  # the other half of the table invisible — a phase-2 copy is then neither
  # waited for nor stopped, and survives holding the mutex (CB-256). Two
  # calls rather than a pattern, each `|| true` for the reason above; the
  # comm check below is what actually decides, by full path.
  running_from() {
    local p comm exe
    for p in $(pgrep -x "$EXECUTABLE" || true) $(pgrep -x ClaudeBuddy || true); do
      comm="$(ps -o comm= -p "$p" 2>/dev/null || true)"
      for exe in "$@"; do
        if [[ "$comm" == "$exe" ]]; then
          echo "$p"
        fi
      done
    done
    return 0
  }

  # The ones running the binary this install put in place — the new path
  # only. A copy still running out of the legacy bundle, or the phase-2
  # executable, is exactly what the report below must not count as success.
  running_installed() { running_from "$INSTALLED_EXE"; }

  # Whether the bundle at $1 is ours, by its Info.plist's bundle id. Guards
  # the legacy removal below: a folder called "Claude Buddy.app" is only a
  # name, and rm -rf is not a thing to do to someone else's app on the
  # strength of one.
  is_our_bundle() {
    [[ "$(plutil -extract CFBundleIdentifier raw -o - "$1/Contents/Info.plist" 2>/dev/null || true)" == "$BUNDLE_ID" ]]
  }

  # Whether launchd currently has the keep-alive job. Asked rather than
  # inferred from the plist file, which is there whether or not it loaded.
  keepalive_loaded() { launchctl list "$BUNDLE_ID" >/dev/null 2>&1; }

  # Recorded before anything is stopped, because the next step can stop it
  # without stop-installed-buddy.sh ever seeing it: unloading the keep-alive
  # stops the Buddy launchd is managing. That is the ordinary case on every
  # machine opted in to the keep-alive, and with only STOPPED to go on the
  # tail below believed nothing had been running, skipped its wait, and read
  # RUNNING before launchd had started the new copy (CB-245 / CB-206).
  #
  # All three paths: on the first install after a rename, the Buddy that was
  # running is an older one — the phase-2 copy out of this same bundle, or
  # the legacy bundle's — and it still counts as "one was running" for the
  # relaunch below.
  WAS_RUNNING="$(running_from "$INSTALLED_EXE" "$INTERIM_EXE" "$LEGACY_EXE")"

  # CB-206: the running Buddy goes before the bundle under it is replaced.
  # The single-instance mutex is one per user since CB-206, so the copy the
  # keep-alive starts below would find the old one holding it and exit 0 —
  # and SuccessfulExit=false means launchd would not try again, leaving the
  # old binary running. Unloading the keep-alive comes first, because it is
  # launchd's own job that would otherwise restart the old copy the moment
  # it is stopped; the reconcile below loads it again either way.
  # tools/stop-installed-buddy.sh has the rest of the reasoning.
  if [[ -f "$KEEPALIVE_PLIST" ]]; then
    launchctl unload "$KEEPALIVE_PLIST" >/dev/null 2>&1 || true
  fi
  #
  # All three executables in one call, so a legacy, a phase-2 and a current
  # copy are stopped together and the grace period is waited out once.
  STOPPED="$(tools/stop-installed-buddy.sh "$INSTALLED_EXE" "$INTERIM_EXE" "$LEGACY_EXE")" ||
    echo "    warning: a running $APP_NAME survived SIGKILL" >&2
  if [[ -n "$STOPPED" ]]; then
    echo "==> Stopped the running $APP_NAME ($(echo $STOPPED | tr '\n' ' '))"
  fi

  echo "==> Installing to /Applications"
  rm -rf "$INSTALLED_APP"
  if [[ -d "$LEGACY_APP" ]]; then
    if is_our_bundle "$LEGACY_APP"; then
      rm -rf "$LEGACY_APP"
      echo "==> Removed the legacy $LEGACY_APP"
      echo "    If it was in your Login Items, add $APP_NAME instead (System Settings > General > Login Items)."
    else
      echo "    warning: left $LEGACY_APP alone -- its bundle id is not $BUNDLE_ID" >&2
    fi
  fi
  cp -R "$APP" "/Applications/"
  echo "==> Installed $INSTALLED_APP"
  # CB-49: reconcile the crash keep-alive LaunchAgent against whatever
  # settings.json already says about "Serve on launch" -- installed here
  # rather than assumed, so a --install run on a machine already opted in
  # comes back up under launchd's KeepAlive after this build replaces the
  # running one. --keepalive-only skips hook wiring; install-hooks.sh's own
  # header explains why this call lives there instead of in this script.
  # Best-effort: a failure here shouldn't fail an otherwise-successful build.
  "$INSTALLED_APP/Contents/Resources/install-hooks.sh" --keepalive-only || true

  # CB-206: one Buddy, running the new binary, if one was running before.
  # With the keep-alive registered, launchd has just started it; give that a
  # moment to show. Otherwise — not opted in to the keep-alive, or its load
  # failed — relaunch the copy this install stopped, through LaunchServices
  # and without CLAUDE_CONFIG_DIR, which an agent shell would otherwise leak
  # into the app and mislabel every account orb with. Nothing is launched if
  # nothing was running: a first install still leaves starting it to you.
  #
  # Waited for if one was running before this install — whichever of the
  # keep-alive unload or stop-installed-buddy.sh actually stopped it — or if
  # the reconcile left the keep-alive loaded, since launchd starts it then
  # either way. Relaunched by hand only in the first case: nothing is
  # launched on a machine where nothing was running.
  if [[ -n "$STOPPED" || -n "$WAS_RUNNING" ]] || keepalive_loaded; then
    for _ in 1 2 3 4 5 6 7 8 9 10; do
      [[ -n "$(running_installed)" ]] && break
      sleep 0.5
    done
    if [[ -z "$(running_installed)" && ( -n "$STOPPED" || -n "$WAS_RUNNING" ) ]]; then
      env -u CLAUDE_CONFIG_DIR open -a "/Applications/$APP_NAME.app"
      sleep 2
    fi
  fi

  RUNNING="$(running_installed)"
  case "$(printf '%s' "$RUNNING" | grep -c .)" in
    0) echo "    Launch it with: open -a \"$APP_NAME\"" ;;
    1) echo "==> Running: pid $RUNNING" ;;
    *) echo "    warning: more than one $APP_NAME is running: $(echo $RUNNING)" >&2 ;;
  esac
else
  echo "    Try it with:    open \"$APP\""
  echo "    Install it with: $0 --install"
fi
