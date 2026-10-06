using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace MonitorAudioRouter.RegressionTests;

internal static class Program
{
    private static int Main()
    {
        var runner = new RegressionTestRunner(Console.Out);
        runner.Add("Regression runner reports passing and failing checks", RegressionRunnerReportsPassAndFail);
        runner.Add("Tray application assembly loads through its project reference", TrayAssemblyLoadsThroughProjectReference);
        runner.Add("Installer assembly loads through its project reference", InstallerAssemblyLoadsThroughProjectReference);
        runner.Add("All absent role values produce Default", AllAbsentRoleValuesProduceDefault);
        runner.Add("Any untrustworthy role query produces Unavailable unless a consistent explicit endpoint is proven", UntrustworthyRoleQueryProducesUnavailableWithoutExplicitEndpoint);
        runner.Add("An explicit endpoint produces Explicit with its ID", ExplicitEndpointProducesExplicitWithItsId);
        runner.Add("Failed multi-role set followed by matching readback claims ownership", FailedMultiRoleSetWithMatchingReadbackClaimsOwnership);
        runner.Add("Unavailable readback preserves prior ownership", UnavailableReadbackPreservesPriorOwnership);
        runner.Add("A reused PID with a different executable path is never cleared", ReusedPidWithDifferentExecutablePathIsNeverCleared);
        runner.Add("The same executable under a new PID can inherit a matching dormant managed route", SameExecutableUnderNewPidCanInheritMatchingDormantRoute);
        runner.Add("An inaccessible executable identity causes no route mutation", InaccessibleExecutableIdentityCausesNoRouteMutation);
        runner.Add("Serializing unchanged state performs no file replacement", SerializingUnchangedStatePerformsNoFileReplacement);
        runner.Add("Unavailable old-process identity preserves ownership without rebinding", UnavailableOldProcessIdentityPreservesOwnershipWithoutRebinding);
        runner.Add("Target identity change before set causes no policy or state mutation", TargetIdentityChangeBeforeSetCausesNoMutation);
        runner.Add("Target identity change before direct clear causes no policy or state mutation", TargetIdentityChangeBeforeDirectClearCausesNoMutation);
        runner.Add("Legacy pathless ownership migrates only with complete matching evidence", LegacyPathlessOwnershipMigratesWithCompleteEvidence);
        runner.Add("Unavailable managed-clear readback preserves engine ownership", UnavailableManagedClearReadbackPreservesEngineOwnership);
        runner.Add("Legacy pathless ownership stays unchanged when migration identity is unavailable", LegacyPathlessOwnershipStaysUnchangedWhenIdentityUnavailable);
        runner.Add("Failed engine set with matching readback creates verified ownership", FailedEngineSetWithMatchingReadbackCreatesOwnership);
        runner.Add("Definitively exited owner transfers engine ownership to matching new PID", ExitedOwnerTransfersEngineOwnershipToMatchingNewPid);
        runner.Add("Unavailable new target identity preserves dormant ownership without rebinding", UnavailableNewTargetIdentityPreservesDormantOwnership);
        runner.Add("Reused PID mismatch prevents engine clear and preserves ownership", ReusedPidMismatchPreventsEngineClear);
        runner.Add("Failed role read with consistent explicit endpoint remains Explicit", FailedRoleReadWithConsistentExplicitEndpointRemainsExplicit);
        runner.Add("Conflicting explicit role endpoints produce Unavailable", ConflictingExplicitRoleEndpointsProduceUnavailable);
        runner.Add("Different explicit clear readback reports ownership loss", DifferentExplicitClearReadbackReportsOwnershipLoss);
        runner.Add("Verified legacy ownership migrates and clears through engine cleanup", VerifiedLegacyOwnershipMigratesAndClearsThroughEngineCleanup);
        runner.Add("Older browser hints from the same source are rejected", OlderBrowserHintsFromSameSourceAreRejected);
        runner.Add("A restarted browser hint source may begin at sequence one", RestartedBrowserHintSourceMayBeginAtOne);
        runner.Add("Browser hint source sequences remain independent", BrowserHintSourceSequencesRemainIndependent);
        runner.Add("Legacy browser hints remain accepted during rollout", LegacyBrowserHintsRemainAccepted);
        runner.Add("A Firefox hint cannot claim a Chrome PID", FirefoxHintCannotClaimChromePid);
        runner.Add("Browser title diagnostics never contain raw titles", BrowserTitleDiagnosticsNeverContainRawTitles);
        runner.Add("Oversized browser pipe lines fail before processing", OversizedBrowserPipeLinesFail);
        runner.Add("Disconnected partial browser pipe lines fail before processing", DisconnectedPartialBrowserPipeLinesFail);
        runner.Add("A timed-out partial browser pipe line changes no hint state", TimedOutPartialBrowserPipeLineChangesNoState);

        var result = runner.RunAll();
        Console.WriteLine($"C# regression tests passed: {result.Passed}/{result.Total}.");
        return result.Failed == 0 ? 0 : 1;
    }

    private static void RegressionRunnerReportsPassAndFail()
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        var runner = new RegressionTestRunner(output);
        runner.Add("sample passing check", () => { });
        runner.Add("sample failing check", () => throw new InvalidOperationException("sample failure"));

        var result = runner.RunAll();

        RegressionAssert.Equal(1, result.Passed, "The inner runner should count one passing check.");
        RegressionAssert.Equal(1, result.Failed, "The inner runner should count one failing check.");
        RegressionAssert.Contains("[PASS] sample passing check", output.ToString(), "The pass result should be reported.");
        RegressionAssert.Contains("[FAIL] sample failing check: sample failure", output.ToString(), "The failure should be reported.");
    }

    private static void TrayAssemblyLoadsThroughProjectReference()
    {
        var assembly = typeof(global::MonitorAudioRouter.RouterSettings).Assembly;

        RegressionAssert.Equal("MonitorAudioRouter", assembly.GetName().Name, "The tray assembly name should be preserved.");
        RegressionAssert.True(!assembly.IsDynamic, "The tray assembly should load from a compiled project reference.");
    }

    private static void InstallerAssemblyLoadsThroughProjectReference()
    {
        var assembly = typeof(global::InstallerOptions).Assembly;

        RegressionAssert.Equal("MonitorAudioRouterSetup", assembly.GetName().Name, "The installer assembly name should be preserved.");
        RegressionAssert.True(!assembly.IsDynamic, "The installer assembly should load from a compiled project reference.");
    }

    private static void AllAbsentRoleValuesProduceDefault()
    {
        using var policy = new global::MonitorAudioRouter.AppAudioPolicy(
            new StubAudioPolicyConfigFactory((_, _, _) => (0, null)));

        var persistedEndpoint = policy.GetPersistedEndpoint(41);

        RegressionAssert.Equal(
            global::MonitorAudioRouter.PersistedEndpointStatus.Default,
            persistedEndpoint.Status,
            "Successful absent reads for every role should prove the process uses Default.");
        RegressionAssert.True(persistedEndpoint.IsDefault, "The Default result should expose IsDefault.");
    }

    private static void UntrustworthyRoleQueryProducesUnavailableWithoutExplicitEndpoint()
    {
        using var policy = new global::MonitorAudioRouter.AppAudioPolicy(
            new StubAudioPolicyConfigFactory((_, _, role) =>
                role == global::MonitorAudioRouter.ERole.eConsole ? (unchecked((int)0x80004005), null) : (0, null)));

        var persistedEndpoint = policy.GetPersistedEndpoint(42);

        RegressionAssert.Equal(
            global::MonitorAudioRouter.PersistedEndpointStatus.Unavailable,
            persistedEndpoint.Status,
            "A failed role read without a proven explicit endpoint must remain unavailable.");
        RegressionAssert.True(!persistedEndpoint.IsDefault, "Unavailable must never be exposed as Default.");
    }

    private static void ExplicitEndpointProducesExplicitWithItsId()
    {
        const string endpointId = "{0.0.0.00000000}.{AUDIO-ENDPOINT}";
        const string packedEndpointId = @"\\?\SWD#MMDEVAPI#" + endpointId + "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
        var rolesRead = new List<global::MonitorAudioRouter.ERole>();
        using var policy = new global::MonitorAudioRouter.AppAudioPolicy(
            new StubAudioPolicyConfigFactory((_, _, role) =>
            {
                rolesRead.Add(role);
                return (0, packedEndpointId);
            }));

        var persistedEndpoint = policy.GetPersistedEndpoint(43);

        RegressionAssert.Equal(
            global::MonitorAudioRouter.PersistedEndpointStatus.Explicit,
            persistedEndpoint.Status,
            "A consistent explicit endpoint should be reported as Explicit.");
        RegressionAssert.Equal(endpointId, persistedEndpoint.EndpointId, "The policy endpoint ID should be unpacked.");
        RegressionAssert.Equal(3, rolesRead.Count, "Every managed role must be read before the explicit endpoint is trusted.");
    }

    private static void FailedMultiRoleSetWithMatchingReadbackClaimsOwnership()
    {
        const string requestedEndpointId = "endpoint-a";

        var shouldClaimOwnership = global::MonitorAudioRouter.RouteOwnershipDecisions.ShouldClaimAfterSet(
            writeSucceeded: false,
            requestedEndpointId,
            global::MonitorAudioRouter.PersistedEndpoint.Explicit(requestedEndpointId));

        RegressionAssert.True(
            shouldClaimOwnership,
            "Matching explicit readback should claim ownership even when one role write reported failure.");
    }

    private static void UnavailableReadbackPreservesPriorOwnership()
    {
        var outcome = global::MonitorAudioRouter.RouteOwnershipDecisions.EvaluateClearReadback(
            global::MonitorAudioRouter.PersistedEndpoint.Unavailable,
            "endpoint-a");

        RegressionAssert.Equal(
            global::MonitorAudioRouter.ManagedRouteClearOutcome.ReadbackUnavailable,
            outcome,
            "Unavailable clear readback must preserve ownership for a later retry.");
    }

    private static void ReusedPidWithDifferentExecutablePathIsNeverCleared()
    {
        var route = new global::MonitorAudioRouter.ManagedRoute
        {
            ProcessId = 44,
            ExecutablePath = @"C:\Apps\Owned\player.exe",
            EndpointId = "endpoint-a"
        };

        var canClear = global::MonitorAudioRouter.RouteOwnershipDecisions.CanClearManagedRoute(
            route,
            currentProcessId: 44,
            currentExecutablePath: @"C:\Apps\Other\player.exe");

        RegressionAssert.True(!canClear, "A PID reused by a different executable must not receive the saved route's clear.");
    }

    private static void SameExecutableUnderNewPidCanInheritMatchingDormantRoute()
    {
        var route = new global::MonitorAudioRouter.ManagedRoute
        {
            ProcessId = 44,
            ExecutablePath = @"C:\Apps\Player\player.exe",
            EndpointId = "endpoint-a"
        };

        var canRebind = global::MonitorAudioRouter.RouteOwnershipDecisions.CanRebindDormantRoute(
            route,
            newProcessId: 45,
            newExecutablePath: @"C:\Apps\Player\.\player.exe",
            global::MonitorAudioRouter.PersistedEndpoint.Explicit("ENDPOINT-A"),
            global::MonitorAudioRouter.OwnedProcessIdentityStatus.Dormant);

        RegressionAssert.True(
            canRebind,
            "The same normalized executable and explicit endpoint should permit dormant ownership transfer.");
    }

    private static void InaccessibleExecutableIdentityCausesNoRouteMutation()
    {
        var route = new global::MonitorAudioRouter.ManagedRoute
        {
            ProcessId = 46,
            ExecutablePath = @"C:\Apps\Player\player.exe",
            EndpointId = "endpoint-a"
        };

        var identityStatus = global::MonitorAudioRouter.RouteOwnershipDecisions.EvaluateProcessIdentity(
            route,
            currentProcessId: 46,
            currentExecutablePath: null);

        RegressionAssert.Equal(
            global::MonitorAudioRouter.ManagedRouteIdentityStatus.Unavailable,
            identityStatus,
            "An inaccessible executable path must be represented as unavailable, not a match.");
        RegressionAssert.True(
            !global::MonitorAudioRouter.RouteOwnershipDecisions.CanClearManagedRoute(route, 46, null),
            "Unavailable identity must not authorize a policy mutation.");
    }

    private static void SerializingUnchangedStatePerformsNoFileReplacement()
    {
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "MonitorAudioRouter.RegressionTests", Guid.NewGuid().ToString("N"));
        var statePath = Path.Combine(temporaryDirectory, "state.json");
        var state = new global::MonitorAudioRouter.RouterState();

        try
        {
            global::MonitorAudioRouter.StateStore.Save(statePath, state);
            using (var heldStateFile = File.Open(statePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                global::MonitorAudioRouter.StateStore.Save(statePath, state);

                RegressionAssert.True(heldStateFile.Length > 0, "The original state file should remain readable and unchanged.");
            }
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    private static void UnavailableOldProcessIdentityPreservesOwnershipWithoutRebinding()
    {
        var oldRoute = new global::MonitorAudioRouter.ManagedRoute
        {
            ProcessId = 44,
            ProcessName = "player.exe",
            ProcessStartUtcTicks = DateTimeOffset.Parse("2026-10-05T12:00:00Z").UtcTicks,
            ExecutablePath = @"C:\Apps\Player\player.exe",
            EndpointId = "endpoint-a",
            EndpointName = "Speakers"
        };
        var state = new global::MonitorAudioRouter.RouterState();
        state.Managed["44"] = oldRoute;
        var policy = new RecordingAudioRoutingPolicy();
        var processIdentities = new StubProcessIdentityProvider();
        processIdentities.Enqueue(44, global::MonitorAudioRouter.ProcessIdentityRead.Unavailable);
        using var engine = new global::MonitorAudioRouter.RoutingEngine(
            new global::MonitorAudioRouter.RouterSettings(),
            policy,
            state,
            processIdentities);
        var target = CreateTarget(45, "player.exe", "2026-10-05T12:05:00Z", "endpoint-a");
        var targetIdentity = CreateIdentity(45, "player.exe", "2026-10-05T12:05:00Z", @"C:\Apps\Player\player.exe");

        var reboundRoute = engine.TryRebindDormantManagedRoute(
            target,
            targetIdentity,
            global::MonitorAudioRouter.PersistedEndpoint.Explicit("endpoint-a"));

        RegressionAssert.Equal<global::MonitorAudioRouter.ManagedRoute?>(null, reboundRoute, "Unavailable old identity must not produce a rebound route.");
        RegressionAssert.Equal(1, state.Managed.Count, "Unavailable old identity must leave the managed dictionary unchanged.");
        RegressionAssert.True(state.Managed.TryGetValue("44", out var preserved) && ReferenceEquals(oldRoute, preserved), "The original ownership record must be preserved.");
        RegressionAssert.True(!state.Managed.ContainsKey("45"), "The new PID must not receive ownership.");
        RegressionAssert.Equal(0, policy.MutationCount, "Rebinding must not call Windows policy mutations.");
    }

    private static void TargetIdentityChangeBeforeSetCausesNoMutation()
    {
        var state = new global::MonitorAudioRouter.RouterState();
        var policy = new RecordingAudioRoutingPolicy();
        var processIdentities = new StubProcessIdentityProvider();
        var expectedIdentity = CreateIdentity(51, "player.exe", "2026-10-05T13:00:00Z", @"C:\Apps\Player\player.exe");
        processIdentities.Enqueue(
            51,
            global::MonitorAudioRouter.ProcessIdentityRead.Available(
                CreateIdentity(51, "replacement.exe", "2026-10-05T13:01:00Z", @"C:\Apps\Other\replacement.exe")));
        using var engine = new global::MonitorAudioRouter.RoutingEngine(
            new global::MonitorAudioRouter.RouterSettings(),
            policy,
            state,
            processIdentities);
        var target = CreateTarget(51, "player.exe", "2026-10-05T13:00:00Z", "endpoint-a");

        var claimedOwnership = engine.SetOwnedRouteWithReadback(
            target,
            expectedIdentity,
            new List<global::MonitorAudioRouter.AudioEndpoint> { target.Endpoint! });

        RegressionAssert.True(!claimedOwnership, "A replacement process must abort the route set.");
        RegressionAssert.Equal(0, policy.SetCalls, "Identity mismatch must be detected before SetPersistedEndpoint.");
        RegressionAssert.Equal(0, state.Managed.Count, "Identity mismatch must not create managed ownership.");
    }

    private static void TargetIdentityChangeBeforeDirectClearCausesNoMutation()
    {
        var state = new global::MonitorAudioRouter.RouterState();
        var preservedRoute = new global::MonitorAudioRouter.ManagedRoute { ProcessId = 99, EndpointId = "endpoint-z" };
        state.Managed["99"] = preservedRoute;
        var policy = new RecordingAudioRoutingPolicy();
        var processIdentities = new StubProcessIdentityProvider();
        var expectedIdentity = CreateIdentity(52, "player.exe", "2026-10-05T14:00:00Z", @"C:\Apps\Player\player.exe");
        processIdentities.Enqueue(
            52,
            global::MonitorAudioRouter.ProcessIdentityRead.Available(
                CreateIdentity(52, "replacement.exe", "2026-10-05T14:01:00Z", @"C:\Apps\Other\replacement.exe")));
        using var engine = new global::MonitorAudioRouter.RoutingEngine(
            new global::MonitorAudioRouter.RouterSettings(),
            policy,
            state,
            processIdentities);
        var target = CreateTarget(52, "player.exe", "2026-10-05T14:00:00Z", "endpoint-default");

        var clearedToDefault = engine.ClearUnownedTargetWithReadback(target, expectedIdentity);

        RegressionAssert.True(!clearedToDefault, "A replacement process must abort the direct clear.");
        RegressionAssert.Equal(0, policy.ClearCalls, "Identity mismatch must be detected before ClearPersistedEndpoint.");
        RegressionAssert.Equal(1, state.Managed.Count, "Identity mismatch must leave unrelated route state unchanged.");
        RegressionAssert.True(ReferenceEquals(preservedRoute, state.Managed["99"]), "The preexisting route object must be preserved.");
    }

    private static void LegacyPathlessOwnershipMigratesWithCompleteEvidence()
    {
        var startUtc = DateTimeOffset.Parse("2026-10-05T15:00:00Z");
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "MonitorAudioRouter.RegressionTests", Guid.NewGuid().ToString("N"));
        var statePath = Path.Combine(temporaryDirectory, "legacy-state.json");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            File.WriteAllText(
                statePath,
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    Managed = new Dictionary<string, object>
                    {
                        ["61"] = new
                        {
                            ProcessId = 61,
                            ProcessName = "player.exe",
                            ProcessStartUtcTicks = startUtc.UtcTicks,
                            EndpointId = "endpoint-a",
                            EndpointName = "Speakers",
                            LastSetUtc = startUtc
                        }
                    }
                }));
            var state = global::MonitorAudioRouter.StateStore.Load(statePath);
            var policy = new RecordingAudioRoutingPolicy();
            var processIdentities = new StubProcessIdentityProvider();
            var identity = CreateIdentity(61, "player.exe", "2026-10-05T15:00:00Z", @"C:\Apps\Player\.\player.exe");
            processIdentities.Enqueue(61, global::MonitorAudioRouter.ProcessIdentityRead.Available(identity));
            using var engine = new global::MonitorAudioRouter.RoutingEngine(
                new global::MonitorAudioRouter.RouterSettings(),
                policy,
                state,
                processIdentities);
            var target = new global::MonitorAudioRouter.ProcessRouteTarget(
                61,
                "player.exe",
                null,
                new global::MonitorAudioRouter.MonitorInfo("DISPLAY1", "Display", "display-id", global::System.Drawing.Rectangle.Empty, true),
                new global::MonitorAudioRouter.AudioEndpoint("endpoint-a", "Speakers", false));
            var route = state.Managed["61"];

            var migrated = engine.TryMigrateLegacyManagedRoute(
                route,
                target,
                identity,
                global::MonitorAudioRouter.PersistedEndpoint.Explicit("ENDPOINT-A"));

            RegressionAssert.True(migrated, "Complete matching evidence should migrate the legacy route.");
            RegressionAssert.Equal(@"C:\Apps\Player\player.exe", route.ExecutablePath, "Migration should persist the normalized executable path.");
            RegressionAssert.Equal(1, state.Managed.Count, "Migration must not add or remove ownership records.");
            RegressionAssert.Equal(0, policy.MutationCount, "Legacy migration must not mutate Windows policy.");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static void UnavailableManagedClearReadbackPreservesEngineOwnership()
    {
        var identity = CreateIdentity(62, "player.exe", "2026-10-05T16:00:00Z", @"C:\Apps\Player\player.exe");
        var route = new global::MonitorAudioRouter.ManagedRoute
        {
            ProcessId = identity.ProcessId,
            ProcessName = identity.ProcessName,
            ProcessStartUtcTicks = identity.StartUtc.UtcTicks,
            ExecutablePath = identity.ExecutablePath,
            EndpointId = "endpoint-a",
            EndpointName = "Speakers"
        };
        var state = new global::MonitorAudioRouter.RouterState();
        state.Managed["62"] = route;
        var policy = new RecordingAudioRoutingPolicy();
        policy.Readbacks.Enqueue(global::MonitorAudioRouter.PersistedEndpoint.Unavailable);
        var processIdentities = new StubProcessIdentityProvider();
        processIdentities.Enqueue(62, global::MonitorAudioRouter.ProcessIdentityRead.Available(identity));
        using var engine = new global::MonitorAudioRouter.RoutingEngine(
            new global::MonitorAudioRouter.RouterSettings(),
            policy,
            state,
            processIdentities);

        var outcome = engine.ClearOwnedRouteWithReadback(route, "regression test");

        RegressionAssert.Equal(global::MonitorAudioRouter.ManagedRouteClearOutcome.ReadbackUnavailable, outcome, "Unavailable readback should request a later retry.");
        RegressionAssert.Equal(1, policy.ClearCalls, "Verified ownership should issue exactly one clear call.");
        RegressionAssert.True(state.Managed.TryGetValue("62", out var preserved) && ReferenceEquals(route, preserved), "Unavailable readback must preserve the ownership record.");
    }

    private static void LegacyPathlessOwnershipStaysUnchangedWhenIdentityUnavailable()
    {
        var startUtc = DateTimeOffset.Parse("2026-10-05T17:00:00Z");
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "MonitorAudioRouter.RegressionTests", Guid.NewGuid().ToString("N"));
        var statePath = Path.Combine(temporaryDirectory, "legacy-state.json");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            File.WriteAllText(
                statePath,
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    Managed = new Dictionary<string, object>
                    {
                        ["63"] = new
                        {
                            ProcessId = 63,
                            ProcessName = "player.exe",
                            ProcessStartUtcTicks = startUtc.UtcTicks,
                            EndpointId = "endpoint-a",
                            EndpointName = "Speakers",
                            LastSetUtc = startUtc
                        }
                    }
                }));
            var state = global::MonitorAudioRouter.StateStore.Load(statePath);
            var policy = new RecordingAudioRoutingPolicy();
            var processIdentities = new StubProcessIdentityProvider();
            processIdentities.Enqueue(63, global::MonitorAudioRouter.ProcessIdentityRead.Unavailable);
            using var engine = new global::MonitorAudioRouter.RoutingEngine(
                new global::MonitorAudioRouter.RouterSettings(),
                policy,
                state,
                processIdentities);
            var target = CreateTarget(63, "player.exe", "2026-10-05T17:00:00Z", "endpoint-a");
            var earlierIdentity = CreateIdentity(63, "player.exe", "2026-10-05T17:00:00Z", @"C:\Apps\Player\player.exe");
            var route = state.Managed["63"];

            var migrated = engine.TryMigrateLegacyManagedRoute(
                route,
                target,
                earlierIdentity,
                global::MonitorAudioRouter.PersistedEndpoint.Explicit("endpoint-a"));

            RegressionAssert.True(!migrated, "Unavailable current identity must abort legacy migration.");
            RegressionAssert.Equal<string?>(null, route.ExecutablePath, "Failed migration must preserve the pathless record.");
            RegressionAssert.Equal(1, state.Managed.Count, "Failed migration must not add or remove ownership records.");
            RegressionAssert.Equal(0, policy.MutationCount, "Failed migration must not mutate Windows policy.");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static void FailedEngineSetWithMatchingReadbackCreatesOwnership()
    {
        var state = new global::MonitorAudioRouter.RouterState();
        var policy = new RecordingAudioRoutingPolicy { SetResult = false };
        policy.Readbacks.Enqueue(global::MonitorAudioRouter.PersistedEndpoint.Explicit("endpoint-a"));
        var processIdentities = new StubProcessIdentityProvider();
        var identity = CreateIdentity(64, "player.exe", "2026-10-05T18:00:00Z", @"C:\Apps\Player\.\player.exe");
        processIdentities.Enqueue(64, global::MonitorAudioRouter.ProcessIdentityRead.Available(identity));
        using var engine = new global::MonitorAudioRouter.RoutingEngine(
            new global::MonitorAudioRouter.RouterSettings(),
            policy,
            state,
            processIdentities);
        var target = CreateTarget(64, "player.exe", "2026-10-05T18:00:00Z", "endpoint-a");

        var claimedOwnership = engine.SetOwnedRouteWithReadback(
            target,
            identity,
            new List<global::MonitorAudioRouter.AudioEndpoint> { target.Endpoint! });

        RegressionAssert.True(claimedOwnership, "Matching readback should claim ownership after aggregate set failure.");
        RegressionAssert.Equal(1, policy.SetCalls, "The engine should issue exactly one set call.");
        RegressionAssert.True(state.Managed.TryGetValue("64", out var route), "Matching readback should create managed ownership.");
        RegressionAssert.Equal("endpoint-a", route!.EndpointId, "The owned endpoint should match readback.");
        RegressionAssert.Equal(@"C:\Apps\Player\player.exe", route.ExecutablePath, "The owned route should persist the normalized verified path.");
    }

    private static void ExitedOwnerTransfersEngineOwnershipToMatchingNewPid()
    {
        var oldRoute = new global::MonitorAudioRouter.ManagedRoute
        {
            ProcessId = 65,
            ProcessName = "player.exe",
            ProcessStartUtcTicks = DateTimeOffset.Parse("2026-10-05T19:00:00Z").UtcTicks,
            ExecutablePath = @"C:\Apps\Player\player.exe",
            EndpointId = "endpoint-a",
            EndpointName = "Speakers"
        };
        var state = new global::MonitorAudioRouter.RouterState();
        state.Managed["65"] = oldRoute;
        var policy = new RecordingAudioRoutingPolicy();
        var processIdentities = new StubProcessIdentityProvider();
        processIdentities.Enqueue(65, global::MonitorAudioRouter.ProcessIdentityRead.Exited);
        processIdentities.Enqueue(
            66,
            global::MonitorAudioRouter.ProcessIdentityRead.Available(
                CreateIdentity(66, "player.exe", "2026-10-05T19:05:00Z", @"C:\Apps\Player\player.exe")));
        using var engine = new global::MonitorAudioRouter.RoutingEngine(
            new global::MonitorAudioRouter.RouterSettings(),
            policy,
            state,
            processIdentities);
        var target = CreateTarget(66, "player.exe", "2026-10-05T19:05:00Z", "endpoint-a");
        var targetIdentity = CreateIdentity(66, "player.exe", "2026-10-05T19:05:00Z", @"C:\Apps\Player\player.exe");

        var rebound = engine.TryRebindDormantManagedRoute(
            target,
            targetIdentity,
            global::MonitorAudioRouter.PersistedEndpoint.Explicit("endpoint-a"));

        RegressionAssert.True(rebound is not null, "A definitively exited owner should transfer to the matching new PID.");
        RegressionAssert.True(!state.Managed.ContainsKey("65"), "The dormant PID record should be removed after transfer.");
        RegressionAssert.True(state.Managed.TryGetValue("66", out var transferred) && ReferenceEquals(rebound, transferred), "The new PID should receive the transferred record.");
        RegressionAssert.Equal("endpoint-a", transferred!.EndpointId, "Transfer must preserve the managed endpoint.");
        RegressionAssert.Equal(0, policy.MutationCount, "Ownership transfer must not write Windows policy.");
    }

    private static void UnavailableNewTargetIdentityPreservesDormantOwnership()
    {
        var oldRoute = new global::MonitorAudioRouter.ManagedRoute
        {
            ProcessId = 70,
            ProcessName = "player.exe",
            ProcessStartUtcTicks = DateTimeOffset.Parse("2026-10-05T22:00:00Z").UtcTicks,
            ExecutablePath = @"C:\Apps\Player\player.exe",
            EndpointId = "endpoint-a",
            EndpointName = "Speakers"
        };
        var state = new global::MonitorAudioRouter.RouterState();
        state.Managed["70"] = oldRoute;
        var policy = new RecordingAudioRoutingPolicy();
        var processIdentities = new StubProcessIdentityProvider();
        processIdentities.Enqueue(70, global::MonitorAudioRouter.ProcessIdentityRead.Exited);
        processIdentities.Enqueue(71, global::MonitorAudioRouter.ProcessIdentityRead.Unavailable);
        using var engine = new global::MonitorAudioRouter.RoutingEngine(
            new global::MonitorAudioRouter.RouterSettings(),
            policy,
            state,
            processIdentities);
        var target = CreateTarget(71, "player.exe", "2026-10-05T22:05:00Z", "endpoint-a");
        var targetIdentity = CreateIdentity(71, "player.exe", "2026-10-05T22:05:00Z", @"C:\Apps\Player\player.exe");

        var rebound = engine.TryRebindDormantManagedRoute(
            target,
            targetIdentity,
            global::MonitorAudioRouter.PersistedEndpoint.Explicit("endpoint-a"));

        RegressionAssert.Equal<global::MonitorAudioRouter.ManagedRoute?>(null, rebound, "An unavailable new identity must abort dormant transfer.");
        RegressionAssert.Equal(1, state.Managed.Count, "An unavailable new identity must leave ownership unchanged.");
        RegressionAssert.True(state.Managed.TryGetValue("70", out var preserved) && ReferenceEquals(oldRoute, preserved), "The dormant ownership record must remain intact.");
        RegressionAssert.True(!state.Managed.ContainsKey("71"), "The unavailable new PID must not receive ownership.");
        RegressionAssert.Equal(0, policy.MutationCount, "An aborted transfer must not write Windows policy.");
    }

    private static void ReusedPidMismatchPreventsEngineClear()
    {
        var route = new global::MonitorAudioRouter.ManagedRoute
        {
            ProcessId = 67,
            ProcessName = "player.exe",
            ProcessStartUtcTicks = DateTimeOffset.Parse("2026-10-05T20:00:00Z").UtcTicks,
            ExecutablePath = @"C:\Apps\Player\player.exe",
            EndpointId = "endpoint-a",
            EndpointName = "Speakers"
        };
        var state = new global::MonitorAudioRouter.RouterState();
        state.Managed["67"] = route;
        var policy = new RecordingAudioRoutingPolicy();
        var processIdentities = new StubProcessIdentityProvider();
        processIdentities.Enqueue(
            67,
            global::MonitorAudioRouter.ProcessIdentityRead.Available(
                CreateIdentity(67, "replacement.exe", "2026-10-05T20:01:00Z", @"C:\Apps\Other\replacement.exe")));
        using var engine = new global::MonitorAudioRouter.RoutingEngine(
            new global::MonitorAudioRouter.RouterSettings(),
            policy,
            state,
            processIdentities);

        var outcome = engine.ClearOwnedRouteWithReadback(route, "regression test");

        RegressionAssert.Equal(global::MonitorAudioRouter.ManagedRouteClearOutcome.IdentityUnverified, outcome, "A reused PID must fail identity verification.");
        RegressionAssert.Equal(0, policy.ClearCalls, "A reused PID must not receive the old owner's clear.");
        RegressionAssert.True(state.Managed.TryGetValue("67", out var preserved) && ReferenceEquals(route, preserved), "The dormant ownership record should remain available for reconciliation.");
    }

    private static void FailedRoleReadWithConsistentExplicitEndpointRemainsExplicit()
    {
        const string endpointId = "{0.0.0.00000000}.{CONSISTENT-ENDPOINT}";
        const string packedEndpointId = @"\\?\SWD#MMDEVAPI#" + endpointId + "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
        using var policy = new global::MonitorAudioRouter.AppAudioPolicy(
            new StubAudioPolicyConfigFactory((_, _, role) =>
                role == global::MonitorAudioRouter.ERole.eConsole
                    ? (unchecked((int)0x80004005), null)
                    : (0, packedEndpointId)));

        var persistedEndpoint = policy.GetPersistedEndpoint(68);

        RegressionAssert.Equal(global::MonitorAudioRouter.PersistedEndpointStatus.Explicit, persistedEndpoint.Status, "Consistent successful explicit reads should remain authoritative.");
        RegressionAssert.Equal(endpointId, persistedEndpoint.EndpointId, "The consistent endpoint ID should be unpacked.");
    }

    private static void ConflictingExplicitRoleEndpointsProduceUnavailable()
    {
        const string endpointA = @"\\?\SWD#MMDEVAPI#{A}#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
        const string endpointB = @"\\?\SWD#MMDEVAPI#{B}#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
        using var policy = new global::MonitorAudioRouter.AppAudioPolicy(
            new StubAudioPolicyConfigFactory((_, _, role) =>
                role == global::MonitorAudioRouter.ERole.eMultimedia ? (0, endpointA) : (0, endpointB)));

        var persistedEndpoint = policy.GetPersistedEndpoint(69);

        RegressionAssert.Equal(global::MonitorAudioRouter.PersistedEndpointStatus.Unavailable, persistedEndpoint.Status, "Conflicting explicit role values must not authorize ownership decisions.");
    }

    private static void DifferentExplicitClearReadbackReportsOwnershipLoss()
    {
        var outcome = global::MonitorAudioRouter.RouteOwnershipDecisions.EvaluateClearReadback(
            global::MonitorAudioRouter.PersistedEndpoint.Explicit("endpoint-b"),
            "endpoint-a");

        RegressionAssert.Equal(global::MonitorAudioRouter.ManagedRouteClearOutcome.OwnershipLost, outcome, "A different explicit endpoint should be treated as user or Windows ownership.");
    }

    private static void VerifiedLegacyOwnershipMigratesAndClearsThroughEngineCleanup()
    {
        var startUtc = DateTimeOffset.Parse("2026-10-05T21:00:00Z");
        var route = new global::MonitorAudioRouter.ManagedRoute
        {
            ProcessId = 70,
            ProcessName = "player.exe",
            ProcessStartUtcTicks = startUtc.UtcTicks,
            EndpointId = "endpoint-a",
            EndpointName = "Speakers"
        };
        var state = new global::MonitorAudioRouter.RouterState();
        state.Managed["70"] = route;
        var policy = new RecordingAudioRoutingPolicy();
        policy.Readbacks.Enqueue(global::MonitorAudioRouter.PersistedEndpoint.Explicit("endpoint-a"));
        policy.Readbacks.Enqueue(global::MonitorAudioRouter.PersistedEndpoint.Default);
        var identity = CreateIdentity(70, "player.exe", "2026-10-05T21:00:00Z", @"C:\Apps\Player\player.exe");
        var processIdentities = new StubProcessIdentityProvider();
        processIdentities.Enqueue(70, global::MonitorAudioRouter.ProcessIdentityRead.Available(identity));
        processIdentities.Enqueue(70, global::MonitorAudioRouter.ProcessIdentityRead.Available(identity));
        processIdentities.Enqueue(70, global::MonitorAudioRouter.ProcessIdentityRead.Available(identity));
        using var engine = new global::MonitorAudioRouter.RoutingEngine(
            new global::MonitorAudioRouter.RouterSettings(),
            policy,
            state,
            processIdentities);

        var result = engine.ClearManagedRoutes();

        RegressionAssert.True(result.Success, "Verified legacy ownership should clear successfully.");
        RegressionAssert.Equal(1, result.Changed, "Verified legacy ownership should report one clear.");
        RegressionAssert.Equal(1, policy.ClearCalls, "Verified legacy ownership should issue one policy clear.");
        RegressionAssert.Equal(0, state.Managed.Count, "Verified default readback should remove the migrated ownership record.");
    }

    private static void OlderBrowserHintsFromSameSourceAreRejected()
    {
        var sourceInstanceId = "ordering-" + Guid.NewGuid().ToString("N");
        var newestAccepted = global::MonitorAudioRouter.BrowserHintStore.ApplyJson(
            CreateBrowserHintJson("chrome", sourceInstanceId, 2, windowId: 301, left: 200));
        var staleAccepted = global::MonitorAudioRouter.BrowserHintStore.ApplyJson(
            CreateBrowserHintJson("chrome", sourceInstanceId, 1, windowId: 301, left: 100));
        var snapshot = global::MonitorAudioRouter.BrowserHintStore.GetSnapshot();

        RegressionAssert.True(newestAccepted, "The newest source sequence should be accepted.");
        RegressionAssert.True(!staleAccepted, "An older sequence from the same source must be rejected.");
        RegressionAssert.Equal(200, snapshot["chrome.exe"].Windows.Single().Bounds.Left, "A stale hint must not replace accepted state.");
    }

    private static void RestartedBrowserHintSourceMayBeginAtOne()
    {
        RegressionAssert.True(
            typeof(global::MonitorAudioRouter.BrowserHintUpdate).GetProperty("SourceInstanceId") is not null &&
            typeof(global::MonitorAudioRouter.BrowserHintUpdate).GetProperty("Sequence") is not null,
            "The compatible browser hint contract should expose source ordering fields.");

        var firstSource = "restart-old-" + Guid.NewGuid().ToString("N");
        var restartedSource = "restart-new-" + Guid.NewGuid().ToString("N");
        global::MonitorAudioRouter.BrowserHintStore.ApplyJson(
            CreateBrowserHintJson("edge", firstSource, 99, windowId: 302, left: 990));

        var restartedAccepted = global::MonitorAudioRouter.BrowserHintStore.ApplyJson(
            CreateBrowserHintJson("edge", restartedSource, 1, windowId: 302, left: 10));
        var snapshot = global::MonitorAudioRouter.BrowserHintStore.GetSnapshot();

        RegressionAssert.True(restartedAccepted, "A new source instance should be allowed to restart at sequence one.");
        RegressionAssert.Equal(10, snapshot["msedge.exe"].Windows.Single().Bounds.Left, "The restarted source should update browser hints.");
    }

    private static void BrowserHintSourceSequencesRemainIndependent()
    {
        var firstSource = "independent-a-" + Guid.NewGuid().ToString("N");
        var secondSource = "independent-b-" + Guid.NewGuid().ToString("N");
        global::MonitorAudioRouter.BrowserHintStore.ApplyJson(
            CreateBrowserHintJson("firefox", firstSource, 5, windowId: 303, left: 50));

        var secondAccepted = global::MonitorAudioRouter.BrowserHintStore.ApplyJson(
            CreateBrowserHintJson("firefox", secondSource, 1, windowId: 303, left: 10));
        var staleFirstAccepted = global::MonitorAudioRouter.BrowserHintStore.ApplyJson(
            CreateBrowserHintJson("firefox", firstSource, 4, windowId: 303, left: 40));
        var snapshot = global::MonitorAudioRouter.BrowserHintStore.GetSnapshot();

        RegressionAssert.True(secondAccepted, "A second source's sequence one should be independent of the first source.");
        RegressionAssert.True(!staleFirstAccepted, "The first source must still reject its own older sequence.");
        RegressionAssert.Equal(10, snapshot["firefox.exe"].Windows.Single().Bounds.Left, "Rejecting one source must preserve the independently accepted state.");
    }

    private static void LegacyBrowserHintsRemainAccepted()
    {
        var accepted = global::MonitorAudioRouter.BrowserHintStore.ApplyJson(
            CreateBrowserHintJson("chrome", sourceInstanceId: null, sequence: null, windowId: 304, left: 304));
        var snapshot = global::MonitorAudioRouter.BrowserHintStore.GetSnapshot();

        RegressionAssert.True(accepted, "A legacy hint without ordering fields should remain accepted during store rollout.");
        RegressionAssert.Equal(304, snapshot["chrome.exe"].Windows.Single().Bounds.Left, "The legacy hint should update browser state.");
    }

    private static void FirefoxHintCannotClaimChromePid()
    {
        var method = RequireStaticMethod(
            typeof(global::MonitorAudioRouter.BrowserHintStore),
            "IsAdvisoryProcessMatch");
        var matches = (bool)method.Invoke(null, new object[] { "firefox.exe", "chrome.exe", true })!;

        RegressionAssert.True(!matches, "An active Chrome audio-session PID must not satisfy a Firefox hint.");
    }

    private static void BrowserTitleDiagnosticsNeverContainRawTitles()
    {
        const string privateTitle = "Private diagnostic title";
        var method = RequireStaticMethod(
            typeof(global::MonitorAudioRouter.BrowserHintStore),
            "CreateTitleDiagnostic");
        var diagnostic = (string)method.Invoke(null, new object[] { new[] { privateTitle } })!;
        var firstCaseOrder = (string)method.Invoke(null, new object[] { new[] { "Case title", "case title" } })!;
        var secondCaseOrder = (string)method.Invoke(null, new object[] { new[] { "case title", "Case title" } })!;

        RegressionAssert.Equal("titleCount=1 titleHash=999f6f0562", diagnostic, "Title diagnostics should use a short deterministic SHA-256 signature.");
        RegressionAssert.True(!diagnostic.Contains(privateTitle, StringComparison.Ordinal), "Diagnostics must never contain a raw browser title.");
        RegressionAssert.Equal(firstCaseOrder, secondCaseOrder, "Case-insensitive duplicate titles should have an order-independent signature.");
    }

    private static void OversizedBrowserPipeLinesFail()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("123456789\n"));
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var exception = RegressionAssert.Throws<InvalidDataException>(() =>
            InvokeBoundedBrowserLineRead(reader, maximumCharacters: 8, TimeSpan.FromSeconds(1)));

        RegressionAssert.Contains("maximum", exception.Message, "The oversized-line failure should identify the enforced boundary.");
    }

    private static void DisconnectedPartialBrowserPipeLinesFail()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("partial"));
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var exception = RegressionAssert.Throws<EndOfStreamException>(() =>
            InvokeBoundedBrowserLineRead(reader, maximumCharacters: 128, TimeSpan.FromSeconds(1)));

        RegressionAssert.Contains("terminator", exception.Message, "The incomplete-line failure should identify the missing terminator.");
    }

    private static void TimedOutPartialBrowserPipeLineChangesNoState()
    {
        const string browser = "edge";
        var sourceInstanceId = "partial-line-" + Guid.NewGuid().ToString("N");
        global::MonitorAudioRouter.BrowserHintStore.ApplyJson(
            CreateBrowserHintJson(browser, sourceInstanceId, 1, windowId: 305, left: 305));
        var before = global::MonitorAudioRouter.BrowserHintStore.GetSnapshot()["msedge.exe"];
        using var stream = new PartialThenBlockingStream(Encoding.UTF8.GetBytes("{\"type\":\"audibleWindows\""));
        using var reader = new StreamReader(stream, Encoding.UTF8);

        RegressionAssert.Throws<TimeoutException>(() =>
            InvokeBoundedBrowserLineRead(reader, maximumCharacters: 128, TimeSpan.FromMilliseconds(25)));
        var after = global::MonitorAudioRouter.BrowserHintStore.GetSnapshot()["msedge.exe"];

        RegressionAssert.True(ReferenceEquals(before, after), "A partial timed-out line must not replace browser hint state.");
    }

    private static string CreateBrowserHintJson(
        string browser,
        string? sourceInstanceId,
        long? sequence,
        int windowId,
        int left)
    {
        return JsonSerializer.Serialize(new
        {
            type = "audibleWindows",
            browser,
            sourceInstanceId,
            sequence,
            windows = new[]
            {
                new
                {
                    windowId,
                    left,
                    top = 0,
                    width = 800,
                    height = 600,
                    processIds = Array.Empty<int>(),
                    titles = new[] { $"hint-{windowId}-{left}" },
                    windowTitles = Array.Empty<string>()
                }
            }
        });
    }

    private static MethodInfo RequireStaticMethod(Type type, string methodName)
    {
        var method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (method is null)
        {
            throw new RegressionAssertionException($"Expected {type.Name}.{methodName} to exist.");
        }

        return method;
    }

    private static string? InvokeBoundedBrowserLineRead(
        StreamReader reader,
        int maximumCharacters,
        TimeSpan timeout)
    {
        var method = RequireStaticMethod(
            typeof(global::MonitorAudioRouter.BrowserHintServer),
            "ReadBoundedLineAsync");
        var task = (Task<string?>)method.Invoke(
            null,
            new object[] { reader, maximumCharacters, timeout, CancellationToken.None })!;
        return task.GetAwaiter().GetResult();
    }

    private static global::MonitorAudioRouter.ProcessRouteTarget CreateTarget(
        int processId,
        string processName,
        string startUtc,
        string endpointId)
    {
        return new global::MonitorAudioRouter.ProcessRouteTarget(
            processId,
            processName,
            DateTimeOffset.Parse(startUtc),
            new global::MonitorAudioRouter.MonitorInfo("DISPLAY1", "Display", "display-id", global::System.Drawing.Rectangle.Empty, true),
            new global::MonitorAudioRouter.AudioEndpoint(endpointId, "Speakers", false));
    }

    private static global::MonitorAudioRouter.ProcessIdentitySnapshot CreateIdentity(
        int processId,
        string processName,
        string startUtc,
        string executablePath)
    {
        return new global::MonitorAudioRouter.ProcessIdentitySnapshot(
            processId,
            processName,
            DateTimeOffset.Parse(startUtc),
            executablePath);
    }
}

internal sealed class RecordingAudioRoutingPolicy : global::MonitorAudioRouter.IAudioRoutingPolicy
{
    internal bool SetResult { get; set; } = true;
    internal int MutationCount => SetCalls + ClearCalls;
    internal int SetCalls { get; private set; }
    internal int ClearCalls { get; private set; }
    internal Queue<global::MonitorAudioRouter.PersistedEndpoint> Readbacks { get; } = new();

    public bool IsAvailable => true;

    public global::MonitorAudioRouter.PersistedEndpoint GetPersistedEndpoint(int processId)
    {
        return Readbacks.Count == 0
            ? global::MonitorAudioRouter.PersistedEndpoint.Unavailable
            : Readbacks.Dequeue();
    }

    public bool SetPersistedEndpoint(int processId, string endpointId)
    {
        SetCalls++;
        return SetResult;
    }

    public bool ClearPersistedEndpoint(int processId)
    {
        ClearCalls++;
        return true;
    }

    public void Dispose()
    {
    }
}

internal sealed class StubProcessIdentityProvider : global::MonitorAudioRouter.IProcessIdentityProvider
{
    private readonly Dictionary<int, Queue<global::MonitorAudioRouter.ProcessIdentityRead>> identities = new();

    internal void Enqueue(int processId, global::MonitorAudioRouter.ProcessIdentityRead identity)
    {
        if (!identities.TryGetValue(processId, out var queued))
        {
            queued = new Queue<global::MonitorAudioRouter.ProcessIdentityRead>();
            identities[processId] = queued;
        }

        queued.Enqueue(identity);
    }

    public global::MonitorAudioRouter.ProcessIdentityRead Read(int processId)
    {
        return identities.TryGetValue(processId, out var queued) && queued.Count > 0
            ? queued.Dequeue()
            : global::MonitorAudioRouter.ProcessIdentityRead.Unavailable;
    }
}

internal sealed class StubAudioPolicyConfigFactory : global::MonitorAudioRouter.IAudioPolicyConfigFactory
{
    private readonly Func<uint, global::MonitorAudioRouter.EDataFlow, global::MonitorAudioRouter.ERole, (int HResult, string? Endpoint)> getEndpoint;

    internal StubAudioPolicyConfigFactory(
        Func<uint, global::MonitorAudioRouter.EDataFlow, global::MonitorAudioRouter.ERole, (int HResult, string? Endpoint)> getEndpoint)
    {
        this.getEndpoint = getEndpoint;
    }

    public int SetPersistedDefaultAudioEndpoint(
        uint processId,
        global::MonitorAudioRouter.EDataFlow flow,
        global::MonitorAudioRouter.ERole role,
        string? deviceId)
    {
        return 0;
    }

    public int GetPersistedDefaultAudioEndpoint(
        uint processId,
        global::MonitorAudioRouter.EDataFlow flow,
        global::MonitorAudioRouter.ERole role,
        out string? deviceId)
    {
        var result = getEndpoint(processId, flow, role);
        deviceId = result.Endpoint;
        return result.HResult;
    }

    public int ClearAllPersistedApplicationDefaultEndpoints()
    {
        return 0;
    }

    public void Dispose()
    {
    }
}

internal sealed class RegressionTestRunner
{
    private readonly TextWriter output;
    private readonly List<(string Name, Action Check)> tests = [];

    internal RegressionTestRunner(TextWriter output)
    {
        this.output = output;
    }

    internal void Add(string name, Action check)
    {
        tests.Add((name, check));
    }

    internal RegressionTestResult RunAll()
    {
        var passed = 0;
        foreach (var test in tests)
        {
            try
            {
                test.Check();
                passed++;
                output.WriteLine($"[PASS] {test.Name}");
            }
            catch (Exception exception)
            {
                output.WriteLine($"[FAIL] {test.Name}: {exception.Message}");
            }
        }

        return new RegressionTestResult(passed, tests.Count - passed);
    }
}

internal readonly record struct RegressionTestResult(int Passed, int Failed)
{
    internal int Total => Passed + Failed;
}

internal static class RegressionAssert
{
    internal static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new RegressionAssertionException(message);
        }
    }

    internal static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new RegressionAssertionException($"{message} Expected: {expected}; actual: {actual}.");
        }
    }

    internal static void Contains(string expectedSubstring, string actual, string message)
    {
        if (!actual.Contains(expectedSubstring, StringComparison.Ordinal))
        {
            throw new RegressionAssertionException($"{message} Missing text: {expectedSubstring}.");
        }
    }

    internal static TException Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new RegressionAssertionException($"Expected {typeof(TException).Name} to be thrown.");
    }
}

internal sealed class PartialThenBlockingStream : Stream
{
    private readonly byte[] prefix;
    private int offset;

    internal PartialThenBlockingStream(byte[] prefix)
    {
        this.prefix = prefix;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int bufferOffset, int count)
    {
        return ReadPrefix(buffer.AsSpan(bufferOffset, count));
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int bufferOffset,
        int count,
        CancellationToken cancellationToken)
    {
        var bytesRead = ReadPrefix(buffer.AsSpan(bufferOffset, count));
        return bytesRead > 0
            ? Task.FromResult(bytesRead)
            : WaitForCancellationAsync(cancellationToken);
    }

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var bytesRead = ReadPrefix(buffer.Span);
        return bytesRead > 0
            ? ValueTask.FromResult(bytesRead)
            : new ValueTask<int>(WaitForCancellationAsync(cancellationToken));
    }

    private int ReadPrefix(Span<byte> destination)
    {
        var bytesRemaining = prefix.Length - offset;
        if (bytesRemaining <= 0)
        {
            return 0;
        }

        var bytesToCopy = Math.Min(bytesRemaining, destination.Length);
        prefix.AsSpan(offset, bytesToCopy).CopyTo(destination);
        offset += bytesToCopy;
        return bytesToCopy;
    }

    private static async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return 0;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class RegressionAssertionException : Exception
{
    internal RegressionAssertionException(string message)
        : base(message)
    {
    }
}
