# Monitor Audio Router Hardening Design

## Purpose

Harden Monitor Audio Router for production distribution without changing the routing behavior already validated in
daily use. The release must remain easy to understand, preserve manual Windows audio choices, recover reliably after
sleep and restart, and keep browser handoffs responsive.

This is a surgical hardening release, not a broad rewrite. Existing configuration keys, command-line flags, native
messaging identifiers, browser store identifiers, registry contracts, monitor matching, browser inference,
pause/resume route retention, VR exclusions, tray commands, and installer defaults remain compatible.

## Release Scope

- Desktop app, native messaging host, and installer version: `0.1.15`.
- Chromium and Firefox extension version: `0.1.6`.
- Windows 10 and Windows 11 remain supported.
- No new runtime or package-manager dependency is introduced.
- The public README remains concise and user-focused.
- Store-ready Chrome ZIP and Firefox XPI files are release outputs.
- The source, tag, installer, release archive, checksums, and browser packages are published to GitHub only after the
  complete validation suite passes.

## Compatibility Invariants

The following behaviors must not regress:

1. An audio session follows the configured output for the monitor containing its window.
2. A monitor configured as `Default` uses the current Windows default playback device.
3. A manual Windows Volume Mixer assignment is never replaced by automatic routing.
4. A route set by Monitor Audio Router remains owned until the app deliberately returns it to default, transfers that
   ownership to the same executable after a process restart, or verifies that the route is no longer present.
5. Browser routes remain held while media is paused or browser-process ownership is temporarily ambiguous.
6. A newly audible browser window wins a pause/play handoff without waiting for a stale audible flag to expire.
7. Same-title browser windows on different monitors continue to resolve correctly.
8. VR audio exclusions, tray enable/disable behavior, single-instance enforcement, route configuration, autostart,
   manual scanning, and update-on-demand behavior remain unchanged unless explicitly hardened below.

## Routing State and Windows Audio Policy

### Tri-state policy reads

Reading a persisted Windows endpoint must return one of three explicit states:

- `Default`: Windows has no explicit endpoint assignment for the process.
- `Explicit`: Windows reports a specific endpoint identifier.
- `Unavailable`: the policy API could not produce a trustworthy answer.

`Unavailable` must never be interpreted as `Default`. A transient COM or registry failure must not cause the router to
forget ownership, claim that a manual route is absent, or clear an endpoint speculatively.

### Partial writes and readback

Windows exposes several audio roles. A route write can partially succeed even when the overall operation reports a
failure. After every set or clear attempt, the router reads the persisted endpoint back. If the requested explicit
endpoint is present, the router records ownership. If a requested clear is verified as default, ownership is removed.
Ambiguous readback preserves the prior ownership record and schedules a retry.

### Process identity

Before clearing a managed route, the router verifies that the current process at that PID is the process represented by
the saved ownership record. A reused PID belonging to another executable is never modified.

The saved identity includes the executable path when Windows permits it. If an owned process exits and the same
executable resumes under a new PID with the same explicit managed endpoint, the router may transfer the dormant
ownership record to the new PID. A missing or inaccessible executable path is treated conservatively: the router keeps
the old record for later reconciliation but does not clear an unverified process.

### State persistence

The state file remains the recovery source after crash, sleep, shutdown, update, and restart. State is written only when
its serialized content changes. Normal passive scans must not rewrite an identical file.

## Browser Hint Pipeline

### Ordered snapshots

Each browser extension serializes snapshot generation so asynchronous tab and window queries cannot send an older
snapshot after a newer one. Every message includes a stable extension-source identifier, a monotonically increasing
sequence number for that source instance, and the existing timestamp. The tray app retains the newest sequence seen
per source and rejects older messages.

A browser profile or extension restart creates a new source identifier, allowing its sequence to begin again without
being rejected by state retained from an older instance.

### PID validation

A PID supplied by an extension is advisory. The tray app uses it only when the process exists, belongs to the named
browser family, and owns a relevant Windows audio session. Otherwise, existing native-window and title matching remains
the authoritative fallback.

### Privacy

Browser titles may be used in memory for local window matching, but logs must not contain raw tab or window titles.
Diagnostics use a short one-way hash and non-content metadata such as browser family, PID, monitor, and dimensions.
The extension continues to send no URLs, page contents, or remote telemetry.

## Native Messaging and Audio Notifications

The local named pipe accepts connections only from the current Windows user. Each connection has a bounded message
size and a finite read timeout before token validation. Oversized, incomplete, unauthenticated, and malformed messages
are rejected without changing routing state.

Disconnected audio-session controls are removed and disposed outside the notification callback. Endpoint refresh and
shutdown continue to dispose every remaining control. Notification registration remains compatible with the current
COM threading model unless a focused test proves a threading change is required.

## Tray and Configuration Lifecycle

The tray app owns one context menu for its lifetime and updates item state in place. It replaces the tray icon only when
the enabled state changes and disposes replaced UI resources.

Reloading a valid configuration that disables routing first clears every verifiably owned route back to default, then
starts the disabled engine. A malformed configuration fails disabled, leaves existing autostart policy untouched, and
shows a clear error rather than silently loading enabled defaults. A missing first-run configuration still receives the
normal defaults.

## Installer, Upgrade, and Uninstall

### Process handling

Upgrade and uninstall operations identify Monitor Audio Router processes by their resolved installed executable paths,
not by process name alone. The installer stops the installed tray app and waits for it to exit before running the route
cleanup helper, preventing the live scanner from reasserting routes during cleanup. It never kills an unrelated process
that happens to use the same filename.

### Preserved choices

An update launched from the About dialog preserves the installed choices for:

- autostart;
- browser extension deployment;
- private/incognito support;
- published extension IDs and store URLs.

The install metadata records those choices explicitly. Interactive fresh installs retain the existing checked-by-default
options.

### Registry ownership

The installer records the browser-policy and native-host registry values it creates, including the prior value when one
existed. Uninstall removes a value only when its current contents still match the value written by this installer; it
restores a recorded prior value where applicable. Failed registry changes are logged and surfaced instead of being
reported as successful.

The development deployment script merges its Firefox `ExtensionSettings` entry into the existing policy document and
removes only its own entry during cleanup.

### File replacement and rollback

An upgrade stages the complete new payload before replacing installed files. If replacement fails, it restores the
previous payload and install metadata. Files no longer present in the new manifest are removed only after the new
payload has been verified. Uninstall and upgrade cleanup remain limited to the resolved installation directory.

## On-demand Update Security

The updater keeps its explicit user action and UAC prompt. It validates that release and asset URLs use HTTPS and the
expected GitHub hosts, downloads into a newly created per-update directory, rejects reparse-point destinations, verifies
the published SHA-256 value, and verifies the file again immediately before launch.

These checks protect against accidental corruption and same-machine path substitution. They do not establish publisher
identity because the executable is unsigned and the checksum is hosted with the release. The UI and documentation must
not claim that the update is cryptographically authenticated by the publisher.

## Build and Package Reliability

Store-manifest transformations assert that every expected replacement occurs exactly once. A missing or duplicate
replacement fails the build.

Browser packages are deterministic: stable file order, normalized archive paths, and fixed timestamps produce the same
bytes from the same source. The GitHub source archive and release payload use the same deterministic archive helper where
practical; embedded executable build metadata may still make separately compiled binaries differ.

## Readability Requirements

New and materially changed code follows the Universal Code Style, Readability, and Review Guide:

- descriptive names replace shorthand in app-owned code;
- high-level orchestration remains visible;
- guard clauses reduce nesting without scattering cleanup paths;
- comments explain routing ownership, platform constraints, and non-obvious failure handling rather than narrating
  syntax;
- external contract names remain unchanged at boundaries;
- behavior changes are not mixed with unrelated whole-file formatting;
- complex logic is extracted only where a focused helper makes the behavior easier to test and understand.

The large existing C# files are not broadly reorganized in this release. Focused helpers and tests are preferred over a
high-risk structural rewrite.

## Verification

The repository gains a dependency-free regression harness that runs with the installed .NET SDK and Node.js. Tests cover
at least:

- policy read results for default, explicit, and unavailable states;
- partial multi-role write readback;
- PID reuse and same-executable ownership transfer;
- unchanged-state write suppression;
- valid, missing, and malformed configuration loading;
- stale browser snapshot rejection and source restart;
- browser PID-family validation;
- bounded native-message parsing;
- installer option preservation and path-specific process matching;
- registry ownership restore/remove decisions;
- exact package transformation counts.

Release validation also performs:

1. Release builds of the tray app, native host, and installer with zero warnings and errors.
2. JavaScript syntax checks for source and packaged extensions.
3. JSON parsing and version checks for every manifest.
4. PowerShell parser checks for every maintained script.
5. Store-package content checks, including prohibited development-only files and values.
6. A full installer/package build from a clean disposable source archive.
7. SHA-256 verification of every release asset after upload.
8. A clean Git worktree check before and after publication, excluding intentional ignored build outputs.

## Publication

After all checks pass:

1. Commit the implementation and release documentation.
2. Push `main` to `https://github.com/TechlyAccurate/MonitorAudioRouter`.
3. Create and push tag `v0.1.15`.
4. Publish GitHub release `v0.1.15` with the installer, release archive, checksums, Chrome ZIP, Firefox ZIP, and Firefox
   XPI.
5. Compare every GitHub-reported asset digest with the local SHA-256 value.

Browser store submission remains a manual owner action because Chrome Web Store and Firefox Add-ons reviews use the
owner's authenticated accounts. The release must provide clearly named, ready-to-upload packages and concise review
notes.

## Out of Scope

- Changing the proven monitor-selection or browser-window selection policy.
- Routing two simultaneous windows that share one Windows audio session to different devices.
- Automatic unattended updates.
- Purchasing or provisioning a code-signing certificate.
- Claiming publisher authenticity for the unsigned installer.
- Broad source-file decomposition or cosmetic reformatting unrelated to the hardening changes.
