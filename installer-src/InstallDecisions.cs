using System.Text.Json;

namespace MonitorAudioRouter.Setup;

internal sealed record InstalledOptions(
    bool InstallBrowserExtensions,
    bool Autostart,
    bool EnablePrivateBrowsing,
    string ChromeExtensionId,
    string ChromeUpdateUrl,
    string EdgeExtensionId,
    string EdgeUpdateUrl,
    string FirefoxExtensionId,
    string FirefoxInstallUrl);

internal sealed class InstallInfo
{
    public int SchemaVersion { get; set; }
    public string[] ChromeExtensionIds { get; set; } = [];
    public string[] EdgeExtensionIds { get; set; } = [];
    public string[] FirefoxExtensionIds { get; set; } = [];
    public bool PrivateBrowsingEnabled { get; set; }
    public bool? InstallBrowserExtensions { get; set; }
    public bool? Autostart { get; set; }
    public string? ChromeExtensionId { get; set; }
    public string? ChromeUpdateUrl { get; set; }
    public string? EdgeExtensionId { get; set; }
    public string? EdgeUpdateUrl { get; set; }
    public string? FirefoxExtensionId { get; set; }
    public string? FirefoxInstallUrl { get; set; }
    public bool LegacyBrowserCleanupRequired { get; set; }
    public List<RegistryValueOwnership> RegistryValues { get; set; } = [];

    internal bool RequiresLegacyBrowserCleanup => LegacyBrowserCleanupRequired || SchemaVersion < 2;

    internal static InstallInfo Deserialize(string json) =>
        JsonSerializer.Deserialize<InstallInfo>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new InstallInfo();

    internal static InstallInfo FromOptions(
        InstalledOptions options,
        IEnumerable<RegistryValueOwnership> registryValues,
        bool legacyBrowserCleanupRequired = false,
        InstallInfo? previousInstallInfo = null)
    {
        var legacyChromeIds = legacyBrowserCleanupRequired ? previousInstallInfo?.ChromeExtensionIds : null;
        var legacyEdgeIds = legacyBrowserCleanupRequired ? previousInstallInfo?.EdgeExtensionIds : null;
        var legacyFirefoxIds = legacyBrowserCleanupRequired ? previousInstallInfo?.FirefoxExtensionIds : null;
        return new InstallInfo
        {
            SchemaVersion = 2,
            ChromeExtensionIds = MergeIds(legacyChromeIds, options.ChromeExtensionId),
            EdgeExtensionIds = MergeIds(legacyEdgeIds, options.EdgeExtensionId),
            FirefoxExtensionIds = MergeIds(legacyFirefoxIds, options.FirefoxExtensionId),
            PrivateBrowsingEnabled = options.EnablePrivateBrowsing,
            InstallBrowserExtensions = options.InstallBrowserExtensions,
            Autostart = options.Autostart,
            ChromeExtensionId = options.ChromeExtensionId,
            ChromeUpdateUrl = options.ChromeUpdateUrl,
            EdgeExtensionId = options.EdgeExtensionId,
            EdgeUpdateUrl = options.EdgeUpdateUrl,
            FirefoxExtensionId = options.FirefoxExtensionId,
            FirefoxInstallUrl = options.FirefoxInstallUrl,
            LegacyBrowserCleanupRequired = legacyBrowserCleanupRequired,
            RegistryValues = registryValues.ToList()
        };
    }

    internal InstalledOptions ResolveOptions(InstalledOptions fallback) =>
        new(
            InstallBrowserExtensions ?? fallback.InstallBrowserExtensions,
            Autostart ?? fallback.Autostart,
            PrivateBrowsingEnabled,
            ResolveValue(ChromeExtensionId, ChromeExtensionIds, fallback.ChromeExtensionId),
            ChromeUpdateUrl ?? fallback.ChromeUpdateUrl,
            ResolveValue(EdgeExtensionId, EdgeExtensionIds, fallback.EdgeExtensionId),
            EdgeUpdateUrl ?? fallback.EdgeUpdateUrl,
            ResolveValue(FirefoxExtensionId, FirefoxExtensionIds, fallback.FirefoxExtensionId),
            FirefoxInstallUrl ?? fallback.FirefoxInstallUrl);

    internal string Serialize() =>
        JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

    private static string ResolveValue(string? currentValue, string[]? legacyValues, string fallback)
    {
        if (currentValue is not null)
        {
            return currentValue;
        }

        return legacyValues?.FirstOrDefault(HasText) ?? fallback;
    }

    private static string[] MergeIds(IEnumerable<string>? previousIds, string currentId) =>
        (previousIds ?? [])
        .Append(currentId)
        .Where(HasText)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static bool HasText(string value) => !string.IsNullOrWhiteSpace(value);
}

internal sealed record RegistryValueSnapshot(bool Exists, string? Value, string? Kind);

internal sealed record RegistryValueOwnership(
    string Hive,
    string SubKey,
    string ValueName,
    RegistryValueSnapshot Prior,
    RegistryValueSnapshot Written,
    string? JsonPropertyName);

internal sealed class RegistryOwnershipRecorder
{
    private readonly List<RegistryValueOwnership> values;

    internal RegistryOwnershipRecorder(IEnumerable<RegistryValueOwnership>? previousValues)
    {
        values = previousValues?.ToList() ?? [];
    }

    internal IReadOnlyList<RegistryValueOwnership> Values => values;

    internal void Record(
        string hive,
        string subKey,
        string valueName,
        RegistryValueSnapshot current,
        RegistryValueSnapshot written,
        string? jsonPropertyName)
    {
        var previous = values.LastOrDefault(value => SameIdentity(
            value,
            hive,
            subKey,
            valueName,
            jsonPropertyName));
        var prior = previous is not null && previous.Written == current
            ? previous.Prior
            : current;

        values.RemoveAll(value => SameIdentity(value, hive, subKey, valueName, jsonPropertyName));
        values.Add(new RegistryValueOwnership(
            hive,
            subKey,
            valueName,
            prior,
            written,
            jsonPropertyName));
    }

    private static bool SameIdentity(
        RegistryValueOwnership value,
        string hive,
        string subKey,
        string valueName,
        string? jsonPropertyName) =>
        value.Hive.Equals(hive, StringComparison.OrdinalIgnoreCase) &&
        value.SubKey.Equals(subKey, StringComparison.OrdinalIgnoreCase) &&
        value.ValueName.Equals(valueName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(value.JsonPropertyName, jsonPropertyName, StringComparison.OrdinalIgnoreCase);
}

internal enum RegistryRemovalAction
{
    RetainCurrent,
    RestorePrior,
    Delete
}

internal sealed record RegistryRemovalDecision(RegistryRemovalAction Action, RegistryValueSnapshot? Value);

internal enum InstallerMutationKind
{
    Registry,
    StartMenuShortcut,
    UserConfiguration
}

internal sealed class InstallerStateTransaction : IDisposable
{
    private readonly List<Action> rollbackActions = [];
    private readonly Action<InstallerMutationKind, int>? afterMutation;
    private bool completed;
    private int mutationCount;

    internal InstallerStateTransaction(Action<InstallerMutationKind, int>? afterMutation = null)
    {
        this.afterMutation = afterMutation;
    }

    internal void Apply(InstallerMutationKind kind, Action mutation, Action rollback)
    {
        ObjectDisposedException.ThrowIf(completed, this);
        rollbackActions.Add(rollback);
        try
        {
            mutation();
            mutationCount++;
            afterMutation?.Invoke(kind, mutationCount);
        }
        catch (Exception mutationException)
        {
            rollbackActions.RemoveAt(rollbackActions.Count - 1);
            try
            {
                rollback();
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException(
                    "An installer mutation failed and its prior state could not be restored.",
                    mutationException,
                    rollbackException);
            }

            throw;
        }
    }

    internal void Commit()
    {
        ObjectDisposedException.ThrowIf(completed, this);
        rollbackActions.Clear();
        completed = true;
    }

    internal void RollBack()
    {
        if (completed)
        {
            return;
        }

        var failures = new List<Exception>();
        for (var index = rollbackActions.Count - 1; index >= 0; index--)
        {
            try
            {
                rollbackActions[index]();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        rollbackActions.Clear();
        completed = true;
        if (failures.Count > 0)
        {
            throw new AggregateException("One or more installer state mutations could not be rolled back.", failures);
        }
    }

    public void Dispose()
    {
        RollBack();
    }
}

internal sealed record FileStateSnapshot(bool Exists, byte[]? Contents)
{
    internal static FileStateSnapshot Capture(string path) =>
        File.Exists(path)
            ? new FileStateSnapshot(true, File.ReadAllBytes(path))
            : new FileStateSnapshot(false, null);

    internal void Restore(string path)
    {
        if (!Exists)
        {
            File.Delete(path);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"The file path has no parent directory: {path}"));
        File.WriteAllBytes(path, Contents ?? []);
    }
}

internal static class InstallDecisions
{
    private static readonly string[] InstalledProcessNames =
        ["MonitorAudioRouter", "MonitorAudioRouterNativeHost"];
    private const string ReleaseApiHost = "api.github.com";
    private const string ReleaseAssetHost = "github.com";
    private const string RedirectedReleaseAssetHost = "release-assets.githubusercontent.com";
    private const string ReleaseApiPath = "/repos/TechlyAccurate/MonitorAudioRouter/releases/latest";
    private const string ReleaseAssetPathPrefix = "/TechlyAccurate/MonitorAudioRouter/releases/download/";

    internal static IReadOnlyList<string> BuildForwardedArguments(InstalledOptions options)
    {
        var arguments = new List<string>
        {
            options.InstallBrowserExtensions ? "/browserextensions" : "/nobrowserextensions",
            options.Autostart ? "/autostart" : "/noautostart"
        };

        if (options.EnablePrivateBrowsing)
        {
            arguments.Add("/enableprivatebrowsing");
        }

        AddOption(arguments, "ChromeExtensionId", options.ChromeExtensionId);
        AddOption(arguments, "ChromeUpdateUrl", options.ChromeUpdateUrl);
        AddOption(arguments, "EdgeExtensionId", options.EdgeExtensionId);
        AddOption(arguments, "EdgeUpdateUrl", options.EdgeUpdateUrl);
        AddOption(arguments, "FirefoxExtensionId", options.FirefoxExtensionId);
        AddOption(arguments, "FirefoxInstallUrl", options.FirefoxInstallUrl);
        return arguments;
    }

    private static void AddOption(List<string> arguments, string name, string value)
    {
        arguments.Add($"/{name}={value}");
    }

    internal static bool IsExactExecutablePath(string candidatePath, string expectedPath)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(candidatePath),
                Path.GetFullPath(expectedPath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    internal static IEnumerable<T> EnumerateInstalledProcessCandidates<T>(
        Func<string, IEnumerable<T>> getProcessesByName) =>
        InstalledProcessNames.SelectMany(getProcessesByName);

    internal static RegistryRemovalDecision DecideRegistryRemoval(
        RegistryValueOwnership ownership,
        RegistryValueSnapshot current)
    {
        if (current != ownership.Written)
        {
            return new RegistryRemovalDecision(RegistryRemovalAction.RetainCurrent, null);
        }

        return ownership.Prior.Exists
            ? new RegistryRemovalDecision(RegistryRemovalAction.RestorePrior, ownership.Prior)
            : new RegistryRemovalDecision(RegistryRemovalAction.Delete, null);
    }

    internal static bool IsPathWithinRoot(string rootPath, string candidatePath)
    {
        try
        {
            var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
            var normalizedCandidate = Path.GetFullPath(candidatePath);
            var rootedPrefix = normalizedRoot + Path.DirectorySeparatorChar;
            return normalizedCandidate.StartsWith(rootedPrefix, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    internal static bool IsAllowedReleaseUri(string value, bool isApiRequest)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        if (isApiRequest)
        {
            return uri.Host.Equals(ReleaseApiHost, StringComparison.OrdinalIgnoreCase) &&
                   uri.AbsolutePath.Equals(ReleaseApiPath, StringComparison.Ordinal) &&
                   string.IsNullOrEmpty(uri.Query);
        }

        if (uri.Host.Equals(ReleaseAssetHost, StringComparison.OrdinalIgnoreCase))
        {
            return uri.AbsolutePath.StartsWith(ReleaseAssetPathPrefix, StringComparison.Ordinal);
        }

        return uri.Host.Equals(RedirectedReleaseAssetHost, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsSafeUpdateDirectory(FileAttributes attributes) =>
        attributes.HasFlag(FileAttributes.Directory) &&
        !attributes.HasFlag(FileAttributes.ReparsePoint);

    internal static bool CanLaunchVerifiedInstaller(
        string expectedHash,
        string downloadedHash,
        string immediatePreLaunchHash,
        bool updateDirectoryIsSafe)
    {
        return updateDirectoryIsSafe &&
               IsSha256(expectedHash) &&
               string.Equals(expectedHash, downloadedHash, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(expectedHash, immediatePreLaunchHash, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSha256(string value)
    {
        return value.Length == 64 && value.All(character =>
            (character >= '0' && character <= '9') ||
            (character >= 'a' && character <= 'f') ||
            (character >= 'A' && character <= 'F'));
    }
}
