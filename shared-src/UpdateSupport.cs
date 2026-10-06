using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace MonitorAudioRouter.UpdateSupport;

internal sealed record PreservedInstallChoices(
    bool InstallBrowserExtensions,
    bool Autostart,
    bool EnablePrivateBrowsing,
    string ChromeExtensionId,
    string ChromeUpdateUrl,
    string EdgeExtensionId,
    string EdgeUpdateUrl,
    string FirefoxExtensionId,
    string FirefoxInstallUrl);

internal static class InstallChoiceDefaults
{
    internal const string ChromeExtensionId = "jnjminkakfohjeffdpeamngcnfneckog";
    internal const string ChromeUpdateUrl = "https://clients2.google.com/service/update2/crx";
    internal const string EdgeExtensionId = "";
    internal const string EdgeUpdateUrl = "https://edge.microsoft.com/extensionwebstorebase/v1/crx";
    internal const string FirefoxExtensionId = "monitor-audio-router@example.local";
    internal const string FirefoxInstallUrl = "https://addons.mozilla.org/firefox/downloads/latest/monitor-audio-router-bridge/latest.xpi";

    internal static PreservedInstallChoices Create(bool autostart = true) =>
        new(
            InstallBrowserExtensions: true,
            Autostart: autostart,
            EnablePrivateBrowsing: false,
            ChromeExtensionId,
            ChromeUpdateUrl,
            EdgeExtensionId,
            EdgeUpdateUrl,
            FirefoxExtensionId,
            FirefoxInstallUrl);
}

internal static class InstallChoiceContract
{
    internal static PreservedInstallChoices Resolve(
        string installInfoJson,
        PreservedInstallChoices fallback)
    {
        var persisted = JsonSerializer.Deserialize<PersistedInstallChoices>(
            installInfoJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("The installed option metadata is empty.");

        return new PreservedInstallChoices(
            persisted.InstallBrowserExtensions ?? fallback.InstallBrowserExtensions,
            persisted.Autostart ?? fallback.Autostart,
            persisted.PrivateBrowsingEnabled,
            ResolveValue(persisted.ChromeExtensionId, persisted.ChromeExtensionIds, fallback.ChromeExtensionId),
            persisted.ChromeUpdateUrl ?? fallback.ChromeUpdateUrl,
            ResolveValue(persisted.EdgeExtensionId, persisted.EdgeExtensionIds, fallback.EdgeExtensionId),
            persisted.EdgeUpdateUrl ?? fallback.EdgeUpdateUrl,
            ResolveValue(persisted.FirefoxExtensionId, persisted.FirefoxExtensionIds, fallback.FirefoxExtensionId),
            persisted.FirefoxInstallUrl ?? fallback.FirefoxInstallUrl);
    }

    internal static bool HasLegacyBrowserIdentifiers(string installInfoJson)
    {
        var persisted = JsonSerializer.Deserialize<PersistedInstallChoices>(
            installInfoJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("The installed option metadata is empty.");

        return HasAnyText(persisted.ChromeExtensionIds) ||
               HasAnyText(persisted.EdgeExtensionIds) ||
               HasAnyText(persisted.FirefoxExtensionIds);
    }

    internal static IReadOnlyList<string> BuildForwardedArguments(PreservedInstallChoices choices)
    {
        var arguments = new List<string>
        {
            choices.InstallBrowserExtensions ? "/browserextensions" : "/nobrowserextensions",
            choices.Autostart ? "/autostart" : "/noautostart",
            choices.EnablePrivateBrowsing ? "/enableprivatebrowsing" : "/disableprivatebrowsing"
        };

        AddOption(arguments, "ChromeExtensionId", choices.ChromeExtensionId);
        AddOption(arguments, "ChromeUpdateUrl", choices.ChromeUpdateUrl);
        AddOption(arguments, "EdgeExtensionId", choices.EdgeExtensionId);
        AddOption(arguments, "EdgeUpdateUrl", choices.EdgeUpdateUrl);
        AddOption(arguments, "FirefoxExtensionId", choices.FirefoxExtensionId);
        AddOption(arguments, "FirefoxInstallUrl", choices.FirefoxInstallUrl);
        return arguments;
    }

    private static string ResolveValue(string? currentValue, string[]? legacyValues, string fallback)
    {
        if (currentValue is not null)
        {
            return currentValue;
        }

        return legacyValues?.FirstOrDefault(HasText) ?? fallback;
    }

    private static bool HasAnyText(string[]? values) => values?.Any(HasText) == true;

    private static bool HasText(string value) => !string.IsNullOrWhiteSpace(value);

    private static void AddOption(List<string> arguments, string name, string value)
    {
        arguments.Add($"/{name}={value}");
    }

    private sealed class PersistedInstallChoices
    {
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
    }
}

internal sealed record UpdateReleaseInfo(
    string TagName,
    string SetupDownloadUrl,
    string ChecksumsDownloadUrl);

internal static class UpdatePackage
{
    internal const string LatestReleaseApiUrl = "https://api.github.com/repos/TechlyAccurate/MonitorAudioRouter/releases/latest";
    private const string ReleaseApiHost = "api.github.com";
    private const string ReleaseAssetHost = "github.com";
    private const string RedirectedReleaseAssetHost = "release-assets.githubusercontent.com";
    private const string ReleaseApiPath = "/repos/TechlyAccurate/MonitorAudioRouter/releases/latest";
    private const string ReleaseAssetPathPrefix = "/TechlyAccurate/MonitorAudioRouter/releases/download/";

    internal static async Task<UpdateReleaseInfo> GetLatestReleaseAsync(
        HttpClient httpClient,
        string setupAssetName,
        string checksumsAssetName)
    {
        RequireAllowedReleaseUri(LatestReleaseApiUrl, isApiRequest: true);
        using var response = await httpClient.GetAsync(LatestReleaseApiUrl);
        response.EnsureSuccessStatusCode();
        RequireAllowedFinalUri(response, isApiRequest: true);

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);
        var root = document.RootElement;
        var tagName = root.GetProperty("tag_name").GetString();
        if (string.IsNullOrWhiteSpace(tagName))
        {
            throw new InvalidOperationException("GitHub did not return a release tag.");
        }

        var setupUrl = FindAssetDownloadUrl(root, setupAssetName);
        var checksumsUrl = FindAssetDownloadUrl(root, checksumsAssetName);
        if (setupUrl is null || checksumsUrl is null)
        {
            throw new InvalidOperationException("The latest GitHub release is missing the installer or checksum asset.");
        }

        RequireAllowedReleaseUri(setupUrl, isApiRequest: false);
        RequireAllowedReleaseUri(checksumsUrl, isApiRequest: false);
        return new UpdateReleaseInfo(tagName, setupUrl, checksumsUrl);
    }

    internal static async Task DownloadFileAsync(
        HttpClient httpClient,
        string url,
        string updateDirectory,
        string destinationPath)
    {
        RequireAllowedReleaseUri(url, isApiRequest: false);
        var updateRoot = Directory.GetParent(Path.GetFullPath(updateDirectory))?.FullName
            ?? throw new InvalidOperationException("The update directory does not have a parent directory.");
        ValidateRestrictedUpdateDirectory(updateRoot, updateDirectory);
        RequireBoundedNonReparsePath(updateDirectory, destinationPath, mustExist: false, requireDirectory: false);

        var temporaryPath = destinationPath + ".download";
        RequireBoundedNonReparsePath(updateDirectory, temporaryPath, mustExist: false, requireDirectory: false);
        if (File.Exists(destinationPath) || File.Exists(temporaryPath))
        {
            throw new InvalidOperationException("The update destination already exists.");
        }

        try
        {
            using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            RequireAllowedFinalUri(response, isApiRequest: false);

            await using (var input = await response.Content.ReadAsStreamAsync())
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                await input.CopyToAsync(output);
            }

            RequireBoundedNonReparsePath(updateDirectory, temporaryPath, mustExist: true, requireDirectory: false);
            File.Move(temporaryPath, destinationPath);
            RequireBoundedNonReparsePath(updateDirectory, destinationPath, mustExist: true, requireDirectory: false);
        }
        catch
        {
            TryDeleteBoundedFile(updateDirectory, temporaryPath);
            throw;
        }
    }

    internal static string CreateRestrictedUpdateDirectory(string updateRoot, string namePrefix)
    {
        Directory.CreateDirectory(updateRoot);
        EnsureNoReparsePoint(updateRoot);
        var updateDirectory = Path.Combine(updateRoot, namePrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(updateDirectory);

        try
        {
            var currentUser = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.SetOwner(currentUser);
            var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(
                currentUser,
                FileSystemRights.FullControl,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
            FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(updateDirectory), security);

            ValidateRestrictedUpdateDirectory(updateRoot, updateDirectory);
            return updateDirectory;
        }
        catch
        {
            TryDeleteRestrictedUpdateDirectory(updateRoot, updateDirectory);
            throw;
        }
    }

    internal static void ValidateRestrictedUpdateDirectory(string updateRoot, string updateDirectory)
    {
        RequireBoundedNonReparsePath(updateRoot, updateDirectory, mustExist: true, requireDirectory: true);
        var security = FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(updateDirectory));
        if (!security.AreAccessRulesProtected)
        {
            throw new InvalidOperationException("The update directory inherits an unrestricted access control list.");
        }
    }

    internal static void ValidateInstallerPath(
        string updateRoot,
        string updateDirectory,
        string setupPath)
    {
        ValidateRestrictedUpdateDirectory(updateRoot, updateDirectory);
        RequireBoundedNonReparsePath(updateDirectory, setupPath, mustExist: true, requireDirectory: false);
    }

    internal static bool TryDeleteRestrictedUpdateDirectory(string updateRoot, string updateDirectory)
    {
        try
        {
            ValidateRestrictedUpdateDirectory(updateRoot, updateDirectory);
            var pendingDirectories = new Stack<string>();
            pendingDirectories.Push(updateDirectory);
            while (pendingDirectories.Count > 0)
            {
                var currentDirectory = pendingDirectories.Pop();
                foreach (var entry in Directory.EnumerateFileSystemEntries(currentDirectory))
                {
                    var attributes = File.GetAttributes(entry);
                    if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        return false;
                    }

                    if (attributes.HasFlag(FileAttributes.Directory))
                    {
                        pendingDirectories.Push(entry);
                    }
                }
            }

            Directory.Delete(updateDirectory, recursive: true);
            return !Directory.Exists(updateDirectory);
        }
        catch
        {
            return false;
        }
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

        return uri.Host.Equals(RedirectedReleaseAssetHost, StringComparison.OrdinalIgnoreCase) &&
               string.IsNullOrEmpty(uri.UserInfo);
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

    internal static string? ReadExpectedHash(string checksumsPath, string assetName)
    {
        string? expectedHash = null;
        foreach (var line in File.ReadLines(checksumsPath))
        {
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !string.Equals(parts[1], assetName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (expectedHash is not null)
            {
                throw new InvalidDataException($"The checksum file contains more than one entry for {assetName}.");
            }

            expectedHash = parts[0];
        }

        return expectedHash;
    }

    internal static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string? FindAssetDownloadUrl(JsonElement releaseRoot, string assetName)
    {
        if (!releaseRoot.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameProperty) ? nameProperty.GetString() : null;
            if (!string.Equals(name, assetName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var url = asset.TryGetProperty("browser_download_url", out var urlProperty) ? urlProperty.GetString() : null;
            return string.IsNullOrWhiteSpace(url) ? null : url;
        }

        return null;
    }

    private static void RequireAllowedReleaseUri(string value, bool isApiRequest)
    {
        if (!IsAllowedReleaseUri(value, isApiRequest))
        {
            throw new InvalidOperationException(isApiRequest
                ? "The release API URL is not trusted."
                : "A release asset URL is not trusted.");
        }
    }

    private static void RequireAllowedFinalUri(HttpResponseMessage response, bool isApiRequest)
    {
        if (response.RequestMessage?.RequestUri is not Uri finalUri ||
            !IsAllowedReleaseUri(finalUri.AbsoluteUri, isApiRequest))
        {
            throw new InvalidOperationException(isApiRequest
                ? "The release API redirected to an untrusted URL."
                : "A release asset redirected to an untrusted URL.");
        }
    }

    private static void RequireBoundedNonReparsePath(
        string rootPath,
        string candidatePath,
        bool mustExist,
        bool requireDirectory)
    {
        if (!IsPathWithinRoot(rootPath, candidatePath))
        {
            throw new InvalidOperationException("An update path is outside its bounded directory.");
        }

        EnsureNoReparsePoint(rootPath);
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var normalizedCandidate = Path.GetFullPath(candidatePath);
        var relativePath = Path.GetRelativePath(normalizedRoot, normalizedCandidate);
        var currentPath = normalizedRoot;
        foreach (var part in relativePath.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, part);
            if (!File.Exists(currentPath) && !Directory.Exists(currentPath))
            {
                continue;
            }

            EnsureNoReparsePoint(currentPath);
        }

        if (mustExist && !File.Exists(normalizedCandidate) && !Directory.Exists(normalizedCandidate))
        {
            throw new InvalidOperationException("A required update path does not exist.");
        }

        if (mustExist)
        {
            var isDirectory = File.GetAttributes(normalizedCandidate).HasFlag(FileAttributes.Directory);
            if (isDirectory != requireDirectory)
            {
                throw new InvalidOperationException(requireDirectory
                    ? "A required update directory is not a directory."
                    : "A required update file is not a file.");
            }
        }
    }

    private static void EnsureNoReparsePoint(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("An update path contains a reparse point.");
        }
    }

    private static void TryDeleteBoundedFile(string rootPath, string path)
    {
        try
        {
            if (File.Exists(path))
            {
                RequireBoundedNonReparsePath(rootPath, path, mustExist: true, requireDirectory: false);
                File.Delete(path);
            }
        }
        catch
        {
            // A later safe update-directory cleanup can remove a held temporary file.
        }
    }

    private static bool IsSha256(string value)
    {
        return value.Length == 64 && value.All(character =>
            (character >= '0' && character <= '9') ||
            (character >= 'a' && character <= 'f') ||
            (character >= 'A' && character <= 'F'));
    }
}
