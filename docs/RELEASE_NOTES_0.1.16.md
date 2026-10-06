Monitor Audio Router 0.1.16 is a desktop app update. The Chrome and Firefox companion extensions remain at 0.1.6.

Changes:
- Fixes an update race where a browser could restart the native host while the installer was replacing the app files.
- Restarts the existing tray app if an update fails instead of leaving audio routing offline.

Validation:
- Built Release with 0 warnings and 0 errors.
- Passed 8/8 regression suites: 87/87 C# checks, 3/3 installer/uninstaller checks, and 8/8 browser-extension checks.
- Verified an over-the-top installation with Firefox and Chromium native hosts running.
