# Monitor Audio Router 0.1.15

Monitor Audio Router 0.1.15 ships with Chrome and Firefox companion extension 0.1.6.

## Changes

- Preserves manual Windows Volume Mixer choices through stricter process identity and route-ownership checks.
- Rejects stale browser snapshots and safely accepts a restarted extension as a new ordered source.
- Bounds local browser messages by size and read time before updating route state.
- Makes shutdown and audio-session cleanup single-owner operations.
- Preserves existing installer choices during upgrades.
- Rolls back only files and registry values that remain unchanged from the failed install attempt.
- Verifies updater downloads against the release checksum before launch.
- Produces deterministic store, payload, and GitHub release archives.

The installer remains unsigned, so Windows SmartScreen may show a warning.

## Release Assets

- `MonitorAudioRouterSetup.exe`
- `MonitorAudioRouter-0.1.15-github-release.zip`
- `SHA256SUMS.txt`
- `chrome-monitor-audio-router.zip`
- `firefox-monitor-audio-router.zip`
- `firefox-monitor-audio-router.xpi`

## Store Reviewer Notes

### Chrome 0.1.6

The extension reports audible-window metadata to the installed local native messaging host. Version 0.1.6 adds a random source-instance ID and increasing sequence number so the desktop app can reject stale snapshots. It does not send data to a remote service.

The upload package contains only `nativeMessaging` and `tabs` permissions. The source-tree development key, optional `processes` permission, process API calls, and permission-request flow are removed by the store build before packaging. The published extension ID remains `jnjminkakfohjeffdpeamngcnfneckog`.

### Firefox 0.1.6

The extension reports audible tab titles and browser-window bounds to the installed local native messaging host. Version 0.1.6 adds the same source-instance and sequence ordering used by Chrome. It does not collect URLs, page contents, browsing history, or telemetry, and it does not send data to a remote service.

Firefox lists browsing activity and website content because audible tab titles are used locally to match a browser window to its Windows audio session. The Gecko ID remains `monitor-audio-router@example.local`.
