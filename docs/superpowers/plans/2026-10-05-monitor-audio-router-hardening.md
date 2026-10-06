# Monitor Audio Router Hardening Implementation Plan

**Goal:** Ship Monitor Audio Router 0.1.15 and browser bridges 0.1.6 with stronger route ownership, ordered browser hints, bounded local IPC, safer lifecycle handling, and reliable upgrades without changing proven routing decisions.

**Architecture:** Keep the existing event-driven router and its public contracts. Add small testable decision helpers around Windows audio policy, process identity, browser-message ordering, configuration loading, installer ownership, and archive generation; keep platform calls at the current boundaries and make failures conservative.

**Tech Stack:** C#/.NET 8 Windows Forms, Windows Core Audio COM, JavaScript WebExtensions, native messaging, PowerShell 5.1-compatible build/install scripts, Node.js built-ins, GitHub CLI.

**Spec:** `docs/superpowers/specs/2026-10-05-monitor-audio-router-hardening-design.md`

## Global Constraints

- Preserve the routing compatibility invariants in the specification.
- Desktop app, native host, and installer become `0.1.15`; both browser extensions become `0.1.6`.
- Preserve Windows 10 and Windows 11 support and PowerShell 5.1 compatibility.
- Add no runtime or package-manager dependency.
- Follow the Universal Code Style, Readability, and Review Guide in materially changed code.
- Keep external configuration, registry, command-line, native-messaging, and store identifiers unchanged.
- Do not broadly reorganize or reformat the large existing source files.
- Do not publish any artifact unless the complete release validation passes.

## Review Focus

- A transient policy read failure must preserve ownership and must never masquerade as Windows `Default`; Task 2 tests all three policy states and partial writes.
- A stale PID reused by another executable must never have its audio route cleared; Task 2 tests mismatched and inaccessible process identities.
- Concurrent browser events from several profiles must not let an older snapshot replace a newer one; Task 3 tests per-source ordering and source restarts.
- An interrupted upgrade must leave either the previous complete installation or the new complete installation, never a mixed payload; Task 5 tests staged replacement and rollback decisions.
- Uninstall must not delete browser policy or native-host registry values changed by another installer or the user; Task 5 tests compare-before-remove and prior-value restoration.

---

### Task 1: Dependency-free regression harness and test seams

**Files:**
- Create: `tests/MonitorAudioRouter.RegressionTests/MonitorAudioRouter.RegressionTests.csproj`
- Create: `tests/MonitorAudioRouter.RegressionTests/Program.cs`
- Create: `tests/browser-extension-regression-tests.js`
- Create: `tests/Run-RegressionTests.ps1`
- Create: `src/Properties/AssemblyInfo.cs`
- Create: `installer-src/Properties/AssemblyInfo.cs`
- Modify: `.gitignore`

**Interfaces:**
- Consumes: existing `src` and `installer-src` assemblies plus the two extension background scripts.
- Produces: a zero-dependency console regression runner, `RegressionAssert` helpers, and one PowerShell entry point that returns nonzero on any failed C#, JavaScript, JSON, or script check.

- [ ] **Step 1: Write the failing harness smoke checks**

Add named checks proving the runner can report pass/fail, load both product assemblies through project references, parse both extension manifests, and evaluate each background script in a Node `vm` with stub browser APIs.

- [ ] **Step 2: Run the harness and verify it fails because the projects and test entry points do not exist**

Run: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\Run-RegressionTests.ps1`

Expected: nonzero exit with a specific missing-project or missing-test-runner message.

- [ ] **Step 3: Implement the minimal harness and assembly visibility**

Use an executable .NET project with project references and `[assembly: InternalsVisibleTo("MonitorAudioRouter.RegressionTests")]`. Keep assertion and test registration names descriptive; do not add xUnit, NUnit, Jest, or npm packages.

- [ ] **Step 4: Run the harness and establish a green baseline**

Run: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\Run-RegressionTests.ps1`

Expected: all baseline checks pass and the script prints a concise total.

- [ ] **Step 5: Commit**

```text
git add .gitignore src/Properties installer-src/Properties tests
git commit -m "test: add dependency-free regression harness"
```

### Task 2: Conservative route ownership and state persistence

**Files:**
- Modify: `src/Program.cs:2662-3075`
- Modify: `src/Program.cs:3935-3985`
- Modify: `src/Program.cs:4977-5420`
- Modify: `tests/MonitorAudioRouter.RegressionTests/Program.cs`

**Interfaces:**
- Consumes: existing `RoutingEngine`, `RouterState`, `ManagedRoute`, and Windows policy adapters.
- Produces: `PersistedEndpointStatus`, a tri-state `PersistedEndpoint`, executable-path-aware `ManagedRoute` identity, conservative clear/rebind decisions, and content-aware state writes.

- [ ] **Step 1: Write failing policy and ownership tests**

Add checks named for these assertions:

- all absent role values produce `Default`;
- any untrustworthy role query produces `Unavailable` unless a consistent explicit endpoint is proven;
- an explicit endpoint produces `Explicit` with its ID;
- failed multi-role set followed by matching readback claims ownership;
- unavailable readback preserves prior ownership;
- a reused PID with a different executable path is never cleared;
- the same executable under a new PID can inherit a matching dormant managed route;
- an inaccessible executable identity causes no route mutation;
- serializing unchanged state performs no file replacement.

- [ ] **Step 2: Run the C# regression runner and verify the new checks fail**

Run: `dotnet run --project tests\MonitorAudioRouter.RegressionTests\MonitorAudioRouter.RegressionTests.csproj -c Release`

Expected: failures identify the missing tri-state and ownership decision interfaces.

- [ ] **Step 3: Implement tri-state endpoint reads and readback-first writes**

Define `internal enum PersistedEndpointStatus { Default, Explicit, Unavailable }` and update `PersistedEndpoint` to expose `Status`, `EndpointId`, `IsDefault`, and `HasExplicitEndpoint`. Make `SetOwnedRouteWithReadback` and clear paths decide from readback even when a role write reports failure; an unavailable result retains state and retries later.

- [ ] **Step 4: Implement verified process identity and dormant ownership transfer**

Persist a normalized executable path when available. Add focused pure helpers for path equality and clear/rebind eligibility, then use them before every policy mutation in `ClearManagedRoutes` and `ClearUntargetedManagedRoutes`. Do not change browser/window target selection.

- [ ] **Step 5: Suppress identical state writes**

Keep `StateStore.Save(RouterState state)` as the call-site contract, but serialize once and replace `state.json` only when bytes differ. Preserve atomic temporary-file replacement and existing recovery behavior.

- [ ] **Step 6: Run focused and full regression checks**

Run: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\Run-RegressionTests.ps1`

Expected: all Task 1 and Task 2 checks pass.

- [ ] **Step 7: Commit**

```text
git add src/Program.cs tests/MonitorAudioRouter.RegressionTests/Program.cs
git commit -m "fix: preserve verified audio route ownership"
```

### Task 3: Ordered browser hints, private diagnostics, and bounded native IPC

**Files:**
- Modify: `extensions/chromium/background.js:1-260`
- Modify: `extensions/firefox/background.js:1-165`
- Modify: `src/Program.cs:1832-2445`
- Modify: `native-host-src/Program.cs`
- Modify: `tests/browser-extension-regression-tests.js`
- Modify: `tests/MonitorAudioRouter.RegressionTests/Program.cs`

**Interfaces:**
- Consumes: existing `audibleWindows` message shape, native-host token authentication, and browser-window reconciliation.
- Produces: `sourceInstanceId` and `sequence` message fields, a single-flight snapshot scheduler, stale-message rejection per source, browser-family PID validation, redacted title diagnostics, and bounded current-user pipe reads.

- [ ] **Step 1: Write failing browser and IPC tests**

Add checks asserting that burst events coalesce without overlapping collectors, sequence numbers increase in send order, a restarted source may begin at sequence one, an older message from the same source is rejected, different sources remain independent, a Firefox hint cannot claim a Chrome PID, raw titles never appear in diagnostic text, oversized pipe messages fail, and a timed-out partial line changes no state.

- [ ] **Step 2: Run the regression suite and verify the new checks fail**

Run: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\Run-RegressionTests.ps1`

Expected: named failures for ordering, redaction, PID validation, and bounded reads.

- [ ] **Step 3: Serialize extension snapshot collection**

In each background script, add descriptive scheduler state and a stable per-worker/profile source identifier. Event listeners request a snapshot; one asynchronous drain loop collects and sends the newest requested snapshot before advancing its sequence. Preserve Firefox burst timings and Chromium move/bounds responsiveness.

- [ ] **Step 4: Reject stale or mismatched hints in the tray app**

Extend `BrowserHintUpdate` with compatible optional fields. Track the highest sequence per `sourceInstanceId`; accept legacy messages without those fields for backward compatibility. Verify an advisory PID's executable belongs to the named browser family and owns a relevant audio session before using it.

- [ ] **Step 5: Redact browser-title logging**

Use a built-in SHA-256 helper to log a short deterministic title signature plus non-content metadata. Keep raw titles only in memory for matching.

- [ ] **Step 6: Bound and restrict the named pipe**

Create the pipe with `PipeOptions.CurrentUserOnly`. Add `ReadBoundedLineAsync(StreamReader reader, int maximumCharacters, TimeSpan timeout, CancellationToken token)` and reject any input that exceeds the fixed limit or does not complete before the timeout. Keep token validation before deserialization or state mutation.

- [ ] **Step 7: Run focused and full checks**

Run: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\Run-RegressionTests.ps1`

Expected: all browser and IPC checks pass, and `node --check` succeeds for source and packaged scripts.

- [ ] **Step 8: Commit**

```text
git add extensions src/Program.cs native-host-src/Program.cs tests
git commit -m "fix: order and validate browser routing hints"
```

### Task 4: Tray, configuration, and audio-session lifecycle

**Files:**
- Modify: `src/Program.cs:300-790`
- Modify: `src/Program.cs:3897-3970`
- Modify: `src/Program.cs:4400-4690`
- Modify: `tests/MonitorAudioRouter.RegressionTests/Program.cs`

**Interfaces:**
- Consumes: existing tray commands, `RouterSettings`, `RoutingEngine.ClearManagedRoutes`, and audio-session notifications.
- Produces: a reusable tray menu/icon lifecycle, `SettingsLoadResult`, fail-disabled malformed-config behavior, and deferred disposal of disconnected audio-session controls.

- [ ] **Step 1: Write failing lifecycle tests**

Add checks asserting that missing config returns normal defaults, malformed config returns an invalid fail-disabled result without an autostart decision, reloading from enabled to disabled requests route cleanup before engine disposal, unchanged tray state does not allocate a new menu/icon model, and a disconnected session is removed exactly once outside its callback.

- [ ] **Step 2: Run the C# runner and verify the checks fail**

Run: `dotnet run --project tests\MonitorAudioRouter.RegressionTests\MonitorAudioRouter.RegressionTests.csproj -c Release`

Expected: failures identify missing load-result and lifecycle decision helpers.

- [ ] **Step 3: Reuse and dispose tray UI resources**

Create the context menu and menu items once, update labels/check states in `RefreshTray`, replace the icon only when enabled state changes, and dispose the menu and superseded icon during shutdown.

- [ ] **Step 4: Make configuration loading explicit and conservative**

Return `SettingsLoadResult` with `Settings`, `Status`, and `ErrorMessage`. Treat absent config as first-run defaults; treat malformed config as disabled and skip autostart mutation. When a valid reload disables routing, clear verifiably owned routes before disposing the old engine.

- [ ] **Step 5: Dispose expired audio-session subscriptions**

Have the disconnect callback enqueue the affected control for manager-owned removal after callback return. Ensure endpoint refresh and shutdown still drain and dispose all controls.

- [ ] **Step 6: Run full regression checks and build the tray app**

Run: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\Run-RegressionTests.ps1`

Run: `dotnet build src\MonitorAudioRouter.csproj -c Release -warnaserror`

Expected: tests pass; build completes with zero warnings and errors.

- [ ] **Step 7: Commit**

```text
git add src/Program.cs tests/MonitorAudioRouter.RegressionTests/Program.cs
git commit -m "fix: harden tray and session lifecycles"
```

### Task 5: Transactional installer ownership and update preservation

**Files:**
- Create: `installer-src/InstallDecisions.cs`
- Modify: `installer-src/Program.cs:1-570`
- Modify: `installer-src/Program.cs:760-1175`
- Modify: `installer-src/Uninstall-MonitorAudioRouter.ps1`
- Modify: `Install-MonitorAudioRouter.ps1`
- Modify: `src/Program.cs:1230-1475`
- Modify: `tests/MonitorAudioRouter.RegressionTests/Program.cs`

**Interfaces:**
- Consumes: current installer flags, `install-info.json`, published extension identifiers/URLs, GitHub release metadata, and current install path.
- Produces: `InstallInfo`, `RegistryValueOwnership`, path-specific process matching, compare-before-remove registry decisions, preserved update arguments, staged payload replacement with rollback, and hardened update asset validation.

- [ ] **Step 1: Write failing installer decision tests**

Add checks asserting that update arguments preserve every installed option; executable matching uses normalized full paths; a changed registry value is retained; an unchanged installer-owned value is removed or restored to its recorded predecessor; failed staging never selects replacement; failed replacement selects rollback; only expected HTTPS GitHub release hosts are accepted; a reparse-point update directory is rejected; and the final installer hash must match immediately before launch.

- [ ] **Step 2: Run the regression suite and verify the installer checks fail**

Run: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\Run-RegressionTests.ps1`

Expected: named failures for option preservation, ownership, path matching, rollback, and update validation.

- [ ] **Step 3: Add readable pure installer decisions**

Implement focused types and helpers in `MonitorAudioRouter.Setup` for option persistence, path matching, registry ownership comparison, and transactional replacement decisions. Keep platform I/O in `Program.cs` and the uninstaller script.

- [ ] **Step 4: Stop only installed processes and clear routes after exit**

Resolve candidate executable paths, stop only exact installed tray/native-host processes, wait with a finite timeout, and then run the installed cleanup helper. Log and fail the upgrade when safe shutdown cannot be verified rather than killing every matching process name.

- [ ] **Step 5: Persist options and registry ownership**

Extend `install-info.json` without breaking older files. Record the browser deployment choice, private/incognito choice, autostart choice, store IDs/URLs, and each registry mutation's prior and written values. On uninstall, compare current values before restoring or deleting. Merge only the Monitor Audio Router entry in Firefox `ExtensionSettings` in both production and development scripts.

- [ ] **Step 6: Stage replacement and rollback**

Extract and validate the full payload in a staging directory, save the existing manifest and payload in a sibling backup, move the staged payload into place, and restore the backup on failure. Resolve and verify every delete/move target remains inside the installation directory.

- [ ] **Step 7: Harden the user-triggered updater**

Validate release and asset hosts, use a unique restricted update directory, reject reparse points, verify the checksum after download and again immediately before `runas`, and forward options loaded from `install-info.json`. Preserve the explicit prompt and do not add unattended updates.

- [ ] **Step 8: Run tests plus PowerShell parser and installer builds**

Run: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\Run-RegressionTests.ps1`

Run: `dotnet build installer-src\MonitorAudioRouterSetup.csproj -c Release -warnaserror`

Expected: all checks pass; scripts parse under Windows PowerShell 5.1; installer build has zero warnings and errors.

- [ ] **Step 9: Commit**

```text
git add installer-src Install-MonitorAudioRouter.ps1 src/Program.cs tests
git commit -m "fix: make upgrades preserve owned settings"
```

### Task 6: Deterministic packages, release versions, and documentation

**Files:**
- Create: `Build-Archive.ps1`
- Modify: `Build-Store-Packages.ps1`
- Modify: `Build-Installer.ps1`
- Modify: `src/MonitorAudioRouter.csproj`
- Modify: `src/app.manifest`
- Modify: `native-host-src/MonitorAudioRouterNativeHost.csproj`
- Modify: `installer-src/MonitorAudioRouterSetup.csproj`
- Modify: `installer-src/app.manifest`
- Modify: `installer-src/Program.cs:1-30`
- Modify: `extensions/chromium/manifest.json`
- Modify: `extensions/firefox/manifest.json`
- Modify: `README.md`
- Modify: `docs/CODE_WALKTHROUGH.md`
- Create: `docs/RELEASE_NOTES_0.1.15.md`
- Modify: `tests/Run-RegressionTests.ps1`

**Interfaces:**
- Consumes: the hardened sources and existing public package names.
- Produces: exact-count manifest transformations, deterministic ZIP/XPI creation, version-consistent binaries/manifests, concise public documentation, release notes, and store-review notes.

- [ ] **Step 1: Write failing package assertions**

Add checks for exactly one occurrence of each store transformation target, stable archive entry order/timestamps, forbidden development files/manifest values, desktop version `0.1.15`, extension version `0.1.6`, and unchanged published extension IDs.

- [ ] **Step 2: Run package checks and verify version/determinism failures**

Run: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\Run-RegressionTests.ps1`

Expected: failures report the old versions and non-deterministic release/payload archive paths.

- [ ] **Step 3: Centralize deterministic archive creation**

Move the existing stable ZIP logic into `Build-Archive.ps1` with a PowerShell 5.1-compatible function used by store, payload, and GitHub release builds. Make regex transformations count matches first and throw unless the count is exactly one.

- [ ] **Step 4: Update versions and focused documentation**

Update every desktop/native/installer version contract to `0.1.15` and both extension manifests to `0.1.6`. Keep README changes limited to user-visible behavior that changed. Update the walkthrough for new ownership, ordering, and installer flow; add release/store reviewer notes without internal audit history.

- [ ] **Step 5: Prove deterministic extension outputs**

Run `Build-Store-Packages.ps1` twice from unchanged source and compare SHA-256 hashes for Chrome ZIP, Firefox ZIP, and Firefox XPI.

Expected: each pair of hashes is identical.

- [ ] **Step 6: Run the complete local release build**

Run: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File Build-Installer.ps1`

Expected: tray, native host, installer, packages, release archive, and checksums build with zero warnings/errors and the release assets use the existing public filenames.

- [ ] **Step 7: Commit**

```text
git add Build-Archive.ps1 Build-Store-Packages.ps1 Build-Installer.ps1 src native-host-src installer-src extensions README.md docs tests
git commit -m "build: prepare Monitor Audio Router 0.1.15"
```

### Task 7: Whole-release review, clean build, and GitHub publication

**Files:**
- Review: all changes since `138fcf0`
- Verify: `dist/*`, `packages/browser-store/*`, Git history, and GitHub release assets

**Interfaces:**
- Consumes: completed Tasks 1-6 and authenticated `git`/`gh` access.
- Produces: reviewed commit history, tag `v0.1.15`, pushed `main`, GitHub release `v0.1.15`, and verified remote asset hashes.

- [ ] **Step 1: Run a fresh whole-branch code review**

Have a reviewer inspect the complete diff for routing regressions, manual-route violations, installer data loss, update trust overclaims, browser privacy, readability, and missing tests. Resolve every confirmed blocking or high-severity finding with a failing regression test first.

- [ ] **Step 2: Build from a disposable clean Git archive**

Export `HEAD` to a temporary directory outside the working tree, run `tests\Run-RegressionTests.ps1`, then run `Build-Installer.ps1` there.

Expected: every test and build passes with no dependency on ignored local files.

- [ ] **Step 3: Verify release contents and local hashes**

Inspect ZIP/XPI entry lists, parse packaged manifests, run `node --check` on packaged scripts, verify PowerShell parsing, confirm all expected assets exist, and recompute every line in `dist\SHA256SUMS.txt`.

- [ ] **Step 4: Verify repository state before publication**

Run: `git diff --check`, `git status --short`, `git log --oneline --decorate -8`, and `git tag --list v0.1.15`.

Expected: no uncommitted source changes, no whitespace errors, and no pre-existing conflicting tag.

- [ ] **Step 5: Push source and tag**

Push `main`, create annotated tag `v0.1.15` at the verified release commit, and push the tag. Stop if the remote branch changed unexpectedly; do not force-push.

- [ ] **Step 6: Publish the GitHub release and upload all assets**

Create release `v0.1.15` from `docs/RELEASE_NOTES_0.1.15.md` and upload `MonitorAudioRouterSetup.exe`, `MonitorAudioRouter-0.1.15-github-release.zip`, `SHA256SUMS.txt`, Chrome ZIP, Firefox ZIP, and Firefox XPI.

- [ ] **Step 7: Independently verify remote publication**

Fetch the release metadata with `gh`, compare every reported digest and asset size to the local files, verify the release URL and latest-release status, and confirm `origin/main` and `v0.1.15` resolve to the intended commit.

- [ ] **Step 8: Report release and store-upload paths**

Return the GitHub release URL, commit/tag, validation summary, and absolute local Chrome ZIP and Firefox XPI paths. State that browser store upload/review remains manual.
