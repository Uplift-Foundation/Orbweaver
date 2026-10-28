# CB-268 S0 — group backdrop window: z-order and hit-testing

The spike answers the platform unknowns behind plan §E: can a group's backdrop window sit **behind** the orbs, and can it catch the pointer **only inside** its silhouette? It follows this repository's convention of separating what was **confirmed on a real machine** from what is **assumed**. Every reading below came from `tools/backdrop-probe`, which shows an orb stand-in plus an overlapping 300×300 DIP backdrop with a circular silhouette, then asks the window manager where things are. It never synthesizes input. Rerun it with:

```bash
dotnet run --project tools/backdrop-probe                 # z-order + hit-test read-back
dotnet run --project tools/backdrop-probe -- --cpu        # + macOS cursor-poll CPU cost
dotnet run --project tools/backdrop-probe -- --hold 6     # pause at level 2 for an outside CGWindowList reading
```

Every positive reading is paired with a **negative control** that forces the backdrop in front. The "behind" readings would otherwise prove nothing, since they would also come back from a probe that measured the wrong thing.

All readings were taken 2026-10-27 at architect commit `ac81f062`, with Avalonia 12.1.1 (the app's version).

## Machines

| Machine | State | Usable for |
| --- | --- | --- |
| MacBook (`Warrens-MacBook-Pro`), macOS 27.0.1 arm64, one display | in use by Warren, unlocked | z-order, hit-test read-back, CPU. Probe windows were non-activating and shown for about 35 s near the lower-left corner. No input was synthesized. |
| Mac mini, macOS 27.0.1 arm64, one 1920×1080 display | **locked**, reached over SSH, probe run as a GUI-domain LaunchAgent (`launchctl bootstrap gui/$(id -u)`) | z-order only. Every hit-test reading returned the same foreign window (`other(2372)`), which fits the lock screen's shield. The CPU numbers were noise (idle 3.5 ms/s, above the 60 Hz run's 3.4 ms/s) and are not used. |
| Windows PC, Windows 10.0.26200 x64 | the owner logged in but **Disconnected** (session 2, no console attached); one 1024×768 "WinDisc" screen | z-order, `SetWindowRgn` and `WindowFromPoint`, all of which are user32 state. Run through a `/IT` scheduled task so the windows landed on that session's desktop. **Nothing was looked at**, so rendering is unverified. |

No machine has a second display, so nothing about multi-monitor was measured (§4).

## 1. macOS z-order: `setLevel:` one below the orbs

**Confirmed (MacBook and mini):**

- Orbs are on window level **3** (`NSFloatingWindowLevel` / `kCGFloatingWindowLevel`). That is what Avalonia's `Topmost = true` sets. Read three ways: `-[NSWindow level]` on the probe's orb, `CGWindowListCopyWindowInfo` for every live orb and team arrow of the installed app on the MacBook (all `layer=3`), and the same on the mini.
- `setLevel:2` on a `Topmost = false` backdrop puts it **behind every level-3 window and in front of every normal one**. The global window list during `--hold` on the MacBook read: about 20 real orbs and arrows at layer 3, then the probe's orb at layer 3, then the backdrop at **layer 2**, then iTerm2 at layer 0. On the mini it was the probe's orb (3), then the backdrop (2), then Microsoft Edge (0).
- **Control:** `setLevel:4` flips the reading to `backBehindOrb=False` on both Macs, and `setLevel:2` restores it.
- The level **survives** `Hide()`/`Show()` of the backdrop, `Hide()`/`Show()` of the orb, and moving the backdrop (stages m2–m4, both Macs).
- **Avalonia overwrites the level whenever `Topmost` is assigned.** `Topmost = true` gives level 3 and `Topmost = false` gives level 0 (m5 and m6, both Macs). So the backdrop must never touch `Topmost` after construction, and anything that does must re-apply the level.

**Assumed, not measured:** that level 2 also behaves on another Space and next to a full-screen app once `ShowOnAllSpaces()` (`CanJoinAllSpaces | FullScreenAuxiliary`) is applied, as `LinkWindow` does. The probe did not apply it or switch Spaces.

## 2. macOS hit-testing only inside the silhouette

**Confirmed (MacBook), read through AppKit's own `+[NSWindow windowNumberAtPoint:belowWindowWithWindowNumber:]`:**

- With `setIgnoresMouseEvents:YES`, a point in the silhouette and a point in the corner both resolve to the **window underneath** (iTerm2's window id, matched against the CGWindowList reading). The orb's centre still resolves to the orb.
- With `setIgnoresMouseEvents:NO`, both resolve to the backdrop, and the orb's centre still resolves to the orb (it is above).
- **That AppKit query does not model per-pixel alpha.** With the default `ignoresMouseEvents` (never set) the corner resolves to the backdrop at fill alpha 0, 1/255 and 64/255 alike. So alpha-based pass-through can be neither confirmed nor ruled out by read-back. The plan already rejects relying on it, because fill opacity can be 0. This spike adds that it is untestable without a real click, which is one more reason.

**Measured cost of a cursor poll** (MacBook, one backdrop, 360-point polygon, one run of 8 s each, process CPU time):

| | CPU |
| --- | --- |
| idle baseline | 0.8 ms/s |
| 30 Hz poll | 4.3 ms/s (≈ +3.5 ms/s, 0.35 % of one core) |
| 60 Hz poll | 18.2 ms/s (≈ +17.5 ms/s, 1.7 % of one core) |

These are single samples with the cursor mostly still (3 toggles). The jump from 30 to 60 Hz is larger than linear, so read them as an order of magnitude, not a curve.

**Considered and not recommended:** an `NSEvent` global plus local mouse-moved monitor. It is event-driven with no idle cost, but it needs Objective-C blocks built from C# (this repository has no helper for that), two monitors (the global one sees nothing while the cursor is over our own window), and it was not prototyped. Keep it as an optimisation if the poll ever shows up in a profile.

## 3. Windows: `SetWindowRgn` and z-order

**Confirmed (Windows box, disconnected session, user32 read-back):**

- Avalonia 12's transparent window is **`WS_EX_TOPMOST | WS_EX_NOREDIRECTIONBITMAP`, not `WS_EX_LAYERED`** (ex-style `0x200008` on both the orb stand-in and the backdrop). The plan's worry about `WS_EX_LAYERED` doesn't apply: composition-backed transparency is in use.
- `SetWindowRgn` with a `CreatePolygonRgn` (360 points, `WINDING`) region returns 1, and `GetWindowRgn` reads `COMPLEXREGION`.
- **Hit-test, via `WindowFromPoint` + `GetAncestor(GA_ROOT)`:**
  - With no region, the corner resolves to the backdrop.
  - With the region, the corner resolves to the **window underneath**, a point inside the silhouette resolves to the backdrop, and the orb's centre resolves to the orb.
  - **Control:** with the backdrop forced to `HWND_TOPMOST`, the orb's centre resolves to the backdrop.
- The region **survives** `Hide()`/`Show()`, a move, and a resize (`Width` 300 → 310), so Avalonia does not reset it. The region stays in window-relative device pixels, so a resize does not rescale it. S5 sets it again whenever the silhouette changes.
- **Z-order:**
  - `SetWindowPos(back, orb, …, SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE)` puts the backdrop directly behind the orb and keeps `WS_EX_TOPMOST`.
  - **Control:** `HWND_TOPMOST` flips it in front, and `PlaceBehind` restores it.
  - Re-`Show()`ing the backdrop, moving it, resizing it, and re-showing the orb all kept it behind.
  - **A newly shown topmost window lands in front of the backdrop** (a second orb shown afterwards read `orb2.z=3`, `back.z=5`), which matches `WindowsWindowOrder`'s comment. The reverse case, a *new* backdrop shown after the orbs, was not isolated. Assume it lands in front.

**Assumed:**

- That `WindowFromPoint` routes the same way a real click does. It is the documented user32 hit-test, and this repository already used it as a measurement in `WindowsWindowOrder`.
- That a region on a `NOREDIRECTIONBITMAP` window clips the *drawn* output cleanly. The session was disconnected and nobody looked.
- That an interactive console session behaves the same as the disconnected one for both of these points.

## 4. Multi-monitor

**Not measured** (no second display anywhere). From the code at `ac81f062`: the arrangement fits against the **primary** screen's `WorkingArea` (`SessionManager.ComputeClusteredPositions`, `Screens.Primary`), and `ShiftArrangementAnchor` deliberately lets a drag overshoot that the next arrange pulls back (CB-211). Under the plan, `ComputeGroups` fits every group against the same `Work`, and `SeparateGroups` slides boxes back into it. So **an arranged group never spans two screens**, and the only spanning case is a group mid-drag.

**Recommendation:**

- Keep one backdrop window per group, and don't split it per screen.
- Let it straddle during the drag. On macOS, with "Displays have separate Spaces" on (the default), a straddling window is drawn only on the display holding most of it. That is assumed from Apple's documented behaviour and not seen here. Windows draws across both.
- On release, the arrange pass clamps the group back onto the primary work area, exactly as the single shape does today, so nothing regresses.
- Scale a backdrop by its own screen, measured the way `LinkWindow.Apply` measures (`PointToScreen` delta, not `Scaling`).

Groups living on a second monitor would be new scope, not part of CB-268's decisions, and this note does not reopen them.

## Recommendation for S5 (`GroupBackdrops`)

**Construction:** copy `LinkWindow`'s constructor with **`Topmost = false`**. In `Opened`, call `ShowOnAllSpaces()`, then apply the platform order below. Never assign `Topmost` afterwards.

**macOS order:** `setLevel:` to `NSFloatingWindowLevel - 1` (= 2), as a named constant whose comment cites this doc, via `objc_msgSend`. Do it once in `Opened`, and again after anything that assigns `Topmost`. Hide/Show and moves keep it (confirmed). Put the call in `MacOSWindowExtensions` beside `ShowOnAllSpaces`.

**macOS hit-test:**

- One shared `DispatcherTimer` at **30 Hz**, running only while at least one backdrop is shown.
- Each tick:
  - Read `[NSEvent mouseLocation]` and convert Cocoa bottom-left coordinates to top-left using the main display's height.
  - Skip the tick if the cursor hasn't moved, or if any mouse button is down (`[NSEvent pressedMouseButtons] != 0`). Freezing during a press stops a drag or click from losing its window halfway through.
  - For each backdrop, test the cursor against the bounds rectangle first and the polygon second.
  - Send `setIgnoresMouseEvents:` **explicitly in both directions**, and only when the answer changes.
- Never rely on the default (never-set) state.

**Windows order:** add `WindowsWindowOrder.PlaceBehindAll(Window back, IReadOnlyCollection<Window> ours)`:

- Walk `GetTopWindow(NULL)`/`GW_HWNDNEXT`, find the **lowest** of the app's own topmost, non-backdrop windows (orbs, team arrows and the mic flyout alike), then `SetWindowPos(back, lowest, NOMOVE|NOSIZE|NOACTIVATE)`.
- Call it when a backdrop is first shown, on every `Update` (scan or arrange), and when a drag ends. Don't call it on every drag frame: moves don't change the order (confirmed), and the walk visits every top-level window.

**Windows hit-test:**

- On every silhouette change, `SetWindowRgn(hwnd, CreatePolygonRgn(points × RenderScaling, window-relative), true)`.
- Don't delete the region after a successful call, because the system owns it.
- No timer is needed.

**Keep the decisions pure, so the 100%-of-added-lines rule can reach them.** The native calls above go in `[ExcludeFromCodeCoverage]` members, named in the PR as the uncovered lines, in the same way `TeamLinks`, `MacOSWindowExtensions` and `WindowsWindowOrder` already are. Everything they decide lives in pure functions with unit tests:

- `BackdropHitTest.ShouldCatch(cursor, bounds, polygon, buttonsDown, previous) → bool?`, where null means "leave it alone".
- `BackdropOrder.LowestOf(IReadOnlyList<IntPtr> zOrder, ISet<IntPtr> ours, ISet<IntPtr> backdrops) → IntPtr?`
- The Cocoa→top-left conversion.
- The region's point scaling.

**Team arrows** stay at level 3 / topmost. They are among the windows the backdrop goes behind, which is what the plan asks for.

## For a human: 30-second checks only someone at the screen can do

1. **MacBook, unlocked:** with outlines on and **fill opacity 0**, press in the empty space inside a group's shape. A group drag should start, which confirms the explicit `ignoresMouseEvents:NO` catches fully transparent pixels.
2. **MacBook:** click something visible in a corner of a backdrop's bounding box, outside the silhouette, such as a Finder icon or an app window. It should get the click.
3. **Windows box, at the console:** the same two checks, plus a look at the outline's edge. It should be clipped cleanly by the region, with no black or opaque fringe; that last point is the only one nothing here could read back.
