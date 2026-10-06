Monitor Audio Router 0.1.15 includes desktop app and Chrome/Firefox companion extension 0.1.6 updates.

Changes:
- Better protects manual Windows Volume Mixer assignments and only changes routes the app can verify it owns.
- Improves browser handoffs by ignoring stale tab and window updates and handling browser or extension restarts cleanly.
- Fixes shutdown, reload, sleep/resume, and audio-session edge cases that could leave an app routed to the wrong device.
- Keeps existing autostart, browser-extension, private-browsing, and extension settings during updates and reinstalls.
- Makes failed upgrades safer by restoring only files and registry values the installer actually changed.
- Hardens the built-in updater by validating GitHub download locations and rechecking the installer checksum immediately before launch.

Validation:
- Built Release with 0 warnings and 0 errors.
- Passed 8/8 regression suites: 78/78 C# checks, 3/3 installer/uninstaller checks, and 8/8 browser-extension checks.
- Verified release asset checksums after upload.
