using System.Globalization;

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
            ownedProcessStillMatches: false);

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
}

internal sealed class RegressionAssertionException : Exception
{
    internal RegressionAssertionException(string message)
        : base(message)
    {
    }
}
