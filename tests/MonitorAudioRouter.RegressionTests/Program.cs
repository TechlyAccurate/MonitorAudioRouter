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
