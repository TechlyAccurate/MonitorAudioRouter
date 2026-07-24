# Monitor Audio Router

Monitor Audio Router is a Windows tray app that sends an app's sound to the playback device assigned to the monitor where its window is located.

It is useful when a PC has several displays with different speakers. A video moved to a TV can play through the TV, while apps on desktop monitors continue using the normal speakers.

## Downloads

- [Latest Windows release](https://github.com/TechlyAccurate/MonitorAudioRouter/releases/latest)
- Chrome companion extension: Web Store review in progress
- [Firefox companion extension](https://addons.mozilla.org/en-US/firefox/addon/monitor-audio-router-bridge/)

The browser extensions are optional, but recommended when routing browser audio.

## Behavior

- Routes app audio when a window moves between configured monitors.
- Responds to window movement, new audio, display changes, and device changes.
- Leaves manual Windows Volume Mixer assignments alone.
- Ignores common VR audio processes by default.
- Keeps the last correct browser route while media is paused.
- Runs from the system tray and can start automatically with Windows.

The tray menu provides route configuration, enable/disable, `Scan now`, autostart, and access to the log.

## Install

Run `MonitorAudioRouterSetup.exe` as administrator.

The installer adds the tray app, browser communication helper, Start Menu shortcut, and current-user autostart. It also opens a local browser setup page for the optional companion extensions.

To install without browser extensions:

```powershell
MonitorAudioRouterSetup.exe /nobrowserextensions
```

Private/incognito browser support is optional:

```powershell
MonitorAudioRouterSetup.exe /enableprivatebrowsing
```

Chrome and Edge may still require the extension to be allowed in Incognito or InPrivate for each browser profile.

## Configure

Right-click the tray icon and choose `Open config`.

Assign a playback device to each detected monitor. Leave a monitor set to `Default` when apps on that screen should use the current Windows default device.

`Auto-detect` can match displays with similarly named HDMI or DisplayPort audio devices. Advanced users can inspect the underlying file with `View config JSON`.

## Browser Support

Modern browsers often share one audio process across several tabs and windows, so the companion extension tells the local tray app which browser window is producing sound.

- A single playing browser window routes normally, including quick pause/play handoffs.
- If multiple windows sharing one browser process play at the same time, Windows can send that process to only one output. The most recently started window takes priority.

## Privacy

The app has no telemetry or analytics.

The browser extension communicates only with the local native helper. It sends the browser name, window position, window ID, and titles needed to identify audible windows. It does not send URLs, page contents, browsing history, or data to a remote server.

Firefox lists browsing activity and website content permissions because audible tab titles are used for local window matching.

## Troubleshooting

The rolling log is stored at:

```text
%LOCALAPPDATA%\Monitor Audio Router\router.log
```

Useful tray commands:

- `Scan now` immediately checks active routes.
- Disable and re-enable routing to return app-managed routes to the Windows default.
- `Open config` verifies that each monitor is assigned to the intended playback device.
- If one app never follows its window, set that app's Windows Volume Mixer output device back to `Default`; explicit manual assignments take priority over automatic routing.

Windows and browser updates can affect per-app routing behavior. Routing failures are isolated and written to the log.

The installer is currently unsigned, so Windows SmartScreen may display a warning.

## Build

Requirements:

- Windows 10 or Windows 11
- A compatible .NET SDK

Build the installer and browser packages:

```powershell
.\Build-Installer.ps1
```

Browser extension source is kept in [`extensions`](extensions/). Release builds include packaged ZIP and XPI assets.
