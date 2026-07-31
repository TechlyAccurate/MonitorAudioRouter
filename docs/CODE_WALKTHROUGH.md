# Monitor Audio Router Code Walkthrough

This document explains the code at a maintainer level. It is meant to make the
project easy to read, debug, and explain without changing the simple downloader
README.

## Mental model

Monitor Audio Router has one job:

1. Find visible app windows.
2. Decide which monitor each relevant audio-producing process belongs to.
3. Look up the audio device assigned to that monitor.
4. Set or clear the Windows per-app output device for that process.
5. Leave user-owned Windows Volume Mixer choices alone.

The most important safety rule is ownership:

- The app owns only routes it set and verified with Windows readback.
- If the app clears a route, it verifies that Windows now reports `Default`.
- If Windows reports a different explicit endpoint, the app treats that as a
  manual user choice and stops touching it.
- A PID is a Windows audio-session address. It is not a durable browser-tab
  identity.

## Main files

`src/Program.cs`

- Tray app entry point.
- Tray menu and config window.
- Event watchers for windows, displays, power, audio sessions, and browser hints.
- Routing engine.
- Config/state/log helpers.
- Windows audio and window interop.

`native-host-src/Program.cs`

- Small executable used by browser native messaging.
- Launches the main app in native-host mode.

`extensions/firefox/background.js`

- Finds audible Firefox tabs grouped by browser window.
- Sends window bounds, window ID, tab titles, and active-window titles to the
  local native messaging host.
- Firefox does not expose a reliable tab OS process ID here, so the tray app
  must infer the active audio PID when possible.

`extensions/chromium/background.js`

- Does the same browser-window hinting for Chromium browsers.
- Uses Chromium's optional `processes` API when available.
- Any process ID from the extension is still treated as advisory until Windows
  also reports an active audio session for that PID.

`installer-src/Program.cs`

- GUI and quiet installer.
- Installs the tray app, native host manifests, browser extension policy entries,
  Start Menu shortcut, and autostart setting.

## Runtime flow

### Startup

`Program.Main` initializes user data paths and handles command-line diagnostics.
If no diagnostic command was requested, it acquires the single-instance mutex and
starts `RouterTrayContext`.

`RouterTrayContext` creates:

- the tray icon and menu;
- one `RoutingEngine`;
- one `ScanScheduler`;
- event watchers that ask the scheduler for burst scans.

### Events

The app is event-driven with a passive scan fallback.

- Window events catch foreground changes, moves, show/hide, and location changes.
- Display events catch monitor layout changes.
- Power events catch resume from sleep.
- Audio endpoint and session events catch device and playback changes.
- Browser hints arrive when the extension sees audible tab/window changes.

Every watcher calls the scheduler instead of routing immediately. The scheduler
debounces the event and runs a short burst of scans so the app reacts quickly
without polling aggressively all the time.

### Browser hints

Browser extensions cannot set Windows audio. They only provide hints.

The hint payload says:

- browser name;
- browser window ID;
- browser window bounds;
- audible tab titles;
- active tab title;
- optional process IDs when the browser exposes them.

`BrowserHintStore` keeps hints briefly. Old hints expire quickly so stale browser
state does not keep routing forever.

The routing engine reconciles those hints with native Windows windows. This
matters because browser-reported bounds can be stale or affected by DPI changes.
The native window title and dimensions are used to correct those cases.

### Routing scan

`RoutingEngine.Scan` is the core workflow.

It does this in order:

1. Load active audio endpoints and the current system default endpoint.
2. Enumerate visible windows.
3. Build target routes with `BuildProcessRouteTargets`.
4. Clear managed routes that no longer have a valid target.
5. Apply the desired route for each current target.
6. Save route ownership state.

A target route means:

```text
process ID -> monitor -> audio endpoint
```

Normal apps can usually map directly from visible window PID to audio session
PID. Browser apps are harder because one browser can have several windows and
many tabs. Browser targets use extension hints first, then fall back to safe
inference only when there is one audible browser window and one matching active
browser audio session.

### Manual route preservation

Windows Volume Mixer can explicitly assign an app to an output device. The tray
app must not override that if the user set it manually.

The rule is:

- If Windows reports `Default`, the app may route the process.
- If Windows reports the same endpoint the app previously set, the app still
  owns that route.
- If Windows reports a different explicit endpoint, the user or Windows changed
  it. The app forgets ownership and skips that PID.

### Managed state

`state.json` is not user configuration. It is the app's ownership ledger.

It tracks routes like:

```text
process ID
process name
process start time
endpoint ID
endpoint name
last set time
```

Process start time protects against PID reuse. Windows can recycle a PID after a
process exits, so the app should not assume that a new process with the same PID
is the same app instance.

### Sleep and wake

Sleep/wake can temporarily disrupt browser hints and audio-session timing.

On resume, the engine remembers a snapshot of managed routes. If a later scan
finds the same process identity and Windows still reports the exact endpoint the
app had set, the engine recovers ownership instead of treating that endpoint as
a manual Volume Mixer assignment.

### Clearing routes

Clearing a route means setting the process back to Windows `Default`.

The app clears routes when:

- routing is disabled;
- the tray app exits normally;
- a managed app moves to a monitor configured as `Default`;
- a managed app no longer has a valid target and is not being held for browser
  ambiguity or power-resume recovery.

Clearing is verified by readback. If Windows still reports the old endpoint, the
app keeps ownership and retries later.

## Important invariants

- Do not route when `_settings.Enabled` is false.
- Do not route ignored processes such as VR audio services.
- Do not overwrite explicit manual Windows Volume Mixer assignments.
- Do not trust browser extension process IDs unless Windows reports those PIDs
  as active audio sessions.
- Do not forget a managed non-default route merely because a browser is paused,
  ambiguous, or waking after sleep.
- Do not claim ownership unless set/readback confirms the endpoint.
- Do not mark a route cleared unless clear/readback confirms `Default`.

## Where to change things

Add another default ignored app:

- `RouterSettings.IgnoreProcessNames`

Add another browser process:

- `RouterSettings.AllowProcessNames`
- `BrowserHintStore.IsBrowserProcessName`
- browser extension or installer support, if a store extension exists

Change route matching:

- `MonitorRoute.Matches`
- `EndpointMatcher.Find`
- `RouteConfigForm.BuildRoute`

Change browser-window matching:

- `BrowserHintStore.WindowMatchesHint`
- `RoutingEngine.ResolveNativeBrowserWindow`
- `RoutingEngine.AddHintTargets`

Change scan timing:

- `ScanScheduler`
- event watchers that call `RequestBurst`

Change Windows audio policy behavior:

- `AppAudioPolicy`
- `AudioPolicyConfigFactory21H2`
- `AudioPolicyConfigFactoryDownlevel`

## Debugging commands

List monitors, endpoints, and config paths:

```powershell
MonitorAudioRouter.exe --list
```

List active audio sessions and their current Windows output assignments:

```powershell
MonitorAudioRouter.exe --list-audio-sessions
```

Run one routing pass:

```powershell
MonitorAudioRouter.exe --scan-once
```

Clear all app-owned routes:

```powershell
MonitorAudioRouter.exe --clear-managed-routes
```

Clear one stuck PID back to `Default`:

```powershell
MonitorAudioRouter.exe --clear-pid-route 12345
```

## Safe readability work

The safest next readability improvements are:

1. Move each major class from `src/Program.cs` into its own file without changing
   class names or method bodies.
2. Keep `RoutingEngine` behavior changes separate from file-splitting commits.
3. Add small focused tests around pure functions such as monitor matching,
   endpoint matching, title normalization, and version parsing.
4. Keep COM interop signatures isolated and avoid cosmetic edits to GUIDs,
   method order, or marshal attributes.

The current app works well, so broad cleanup should happen in small commits with
builds and a real scan check after each step.
