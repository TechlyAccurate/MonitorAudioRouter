using System.Text.Json;
using System.Text.Json.Nodes;

namespace MonitorAudioRouter.Setup;

internal static class InstallerOrchestration
{
    internal static T Execute<T>(
        string stagedInstallDirectory,
        string installDirectory,
        string backupDirectory,
        Func<InstallerStateTransaction, T> applyExternalState,
        Action? beforeCommit = null)
    {
        InstallerStateTransaction? stateTransaction = null;
        var replacementApplied = false;
        string? pendingBackupDirectory = null;
        try
        {
            var backupCreated = ReplaceInstallation(
                stagedInstallDirectory,
                installDirectory,
                backupDirectory);
            replacementApplied = true;
            pendingBackupDirectory = backupCreated ? backupDirectory : null;
            stateTransaction = new InstallerStateTransaction();

            var result = applyExternalState(stateTransaction);
            beforeCommit?.Invoke();
            stateTransaction.Commit();
            if (pendingBackupDirectory is not null)
            {
                TryDeleteDirectory(pendingBackupDirectory);
            }

            replacementApplied = false;
            return result;
        }
        catch (Exception exception)
        {
            var rollbackFailures = new List<Exception>();
            if (stateTransaction is not null)
            {
                try
                {
                    stateTransaction.RollBack();
                }
                catch (Exception rollbackException)
                {
                    rollbackFailures.Add(rollbackException);
                }
            }

            if (replacementApplied)
            {
                try
                {
                    RollBackInstallation(installDirectory, pendingBackupDirectory);
                }
                catch (Exception rollbackException)
                {
                    rollbackFailures.Add(rollbackException);
                }
            }

            if (rollbackFailures.Count == 0)
            {
                throw;
            }

            throw new AggregateException(
                "Installation failed and one or more parts of the prior state could not be restored.",
                new[] { exception }.Concat(rollbackFailures));
        }
    }

    private static bool ReplaceInstallation(
        string stagedInstallDirectory,
        string installDirectory,
        string backupDirectory)
    {
        var replacementRoot = Path.GetDirectoryName(installDirectory)
            ?? throw new InvalidOperationException("The installation directory does not have a parent directory.");
        RequireChildPath(replacementRoot, stagedInstallDirectory, "staged installation");
        RequireChildPath(replacementRoot, installDirectory, "installation directory");
        RequireChildPath(replacementRoot, backupDirectory, "backup directory");

        var backupCreated = false;
        var replacementStarted = false;
        try
        {
            if (Directory.Exists(backupDirectory))
            {
                throw new InvalidOperationException($"The backup directory already exists: {backupDirectory}");
            }

            if (Directory.Exists(installDirectory))
            {
                Directory.Move(installDirectory, backupDirectory);
                backupCreated = true;
            }

            replacementStarted = true;
            Directory.Move(stagedInstallDirectory, installDirectory);
        }
        catch (Exception replacementException)
        {
            try
            {
                if (replacementStarted && Directory.Exists(installDirectory))
                {
                    Directory.Delete(installDirectory, recursive: true);
                }

                if (backupCreated)
                {
                    Directory.Move(backupDirectory, installDirectory);
                }
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException(
                    "Installation replacement and rollback both failed.",
                    replacementException,
                    rollbackException);
            }

            throw;
        }

        return backupCreated;
    }

    private static void RollBackInstallation(string installDirectory, string? backupDirectory)
    {
        var replacementRoot = Path.GetDirectoryName(installDirectory)
            ?? throw new InvalidOperationException("The installation directory does not have a parent directory.");
        RequireChildPath(replacementRoot, installDirectory, "installation directory");
        if (Directory.Exists(installDirectory))
        {
            Directory.Delete(installDirectory, recursive: true);
        }

        if (backupDirectory is not null)
        {
            RequireChildPath(replacementRoot, backupDirectory, "backup directory");
            Directory.Move(backupDirectory, installDirectory);
        }
    }

    private static void RequireChildPath(string rootPath, string candidatePath, string description)
    {
        if (!InstallDecisions.IsPathWithinRoot(rootPath, candidatePath))
        {
            throw new InvalidOperationException($"The {description} is outside its bounded root: {candidatePath}");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) &&
                !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // A completed backup is safer to leave behind than to retry destructively.
        }
    }
}

internal static class InstallerExternalFileMutation
{
    internal static void Apply(
        InstallerStateTransaction stateTransaction,
        InstallerMutationKind mutationKind,
        string path,
        Action write)
    {
        var snapshot = FileStateSnapshot.Capture(path);
        stateTransaction.Apply(
            mutationKind,
            () =>
            {
                snapshot.EnsureParentDirectory();
                write();
                snapshot.RecordWrittenState();
            },
            snapshot.RollBack);
    }
}

internal sealed class FileStateSnapshot
{
    private readonly string path;
    private readonly string parentDirectory;
    private readonly FileContentSnapshot prior;
    private FileContentSnapshot? written;
    private bool parentDirectoryCreatedByInstaller;

    private FileStateSnapshot(
        string path,
        string parentDirectory,
        FileContentSnapshot prior)
    {
        this.path = path;
        this.parentDirectory = parentDirectory;
        this.prior = prior;
    }

    internal static FileStateSnapshot Capture(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var parentDirectory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException($"The file path has no parent directory: {path}");
        return new FileStateSnapshot(fullPath, parentDirectory, FileContentSnapshot.Capture(fullPath));
    }

    internal void EnsureParentDirectory()
    {
        if (Directory.Exists(parentDirectory))
        {
            return;
        }

        Directory.CreateDirectory(parentDirectory);
        parentDirectoryCreatedByInstaller = true;
    }

    internal void RecordWrittenState()
    {
        written = FileContentSnapshot.Capture(path);
    }

    internal void RollBack()
    {
        var current = FileContentSnapshot.Capture(path);
        if (written is not null && !current.HasSameContent(written))
        {
            return;
        }

        if (prior.Exists)
        {
            Directory.CreateDirectory(parentDirectory);
            File.WriteAllBytes(path, prior.Contents ?? []);
        }
        else
        {
            File.Delete(path);
        }

        if (parentDirectoryCreatedByInstaller &&
            Directory.Exists(parentDirectory) &&
            !Directory.EnumerateFileSystemEntries(parentDirectory).Any())
        {
            Directory.Delete(parentDirectory);
        }
    }

    private sealed record FileContentSnapshot(bool Exists, byte[]? Contents)
    {
        internal static FileContentSnapshot Capture(string path) =>
            File.Exists(path)
                ? new FileContentSnapshot(true, File.ReadAllBytes(path))
                : new FileContentSnapshot(false, null);

        internal bool HasSameContent(FileContentSnapshot other) =>
            Exists == other.Exists &&
            (!Exists || (Contents ?? []).SequenceEqual(other.Contents ?? []));
    }
}

internal enum JsonPropertyRollbackAction
{
    RetainCurrent,
    SetValue,
    DeleteValue
}

internal sealed record JsonPropertyRollbackDecision(JsonPropertyRollbackAction Action, string? Value);

internal sealed class FirefoxExtensionSettingsMutation
{
    private static readonly JsonSerializerOptions CompactJson = new() { WriteIndented = false };

    private FirefoxExtensionSettingsMutation(
        string extensionId,
        RegistryValueSnapshot prior,
        RegistryValueSnapshot written,
        string writtenValue)
    {
        ExtensionId = extensionId;
        Prior = prior;
        Written = written;
        WrittenValue = writtenValue;
    }

    internal string ExtensionId { get; }
    internal RegistryValueSnapshot Prior { get; }
    internal RegistryValueSnapshot Written { get; }
    internal string WrittenValue { get; }

    internal static FirefoxExtensionSettingsMutation Create(
        string? currentValue,
        string extensionId,
        JsonNode extensionPolicy)
    {
        var settings = ParseSettings(currentValue);
        var priorPolicy = settings[extensionId];
        var prior = new RegistryValueSnapshot(
            priorPolicy is not null,
            priorPolicy?.ToJsonString(CompactJson),
            "Json");
        var writtenPolicy = extensionPolicy.DeepClone();
        var written = new RegistryValueSnapshot(
            true,
            writtenPolicy.ToJsonString(CompactJson),
            "Json");
        settings[extensionId] = writtenPolicy;
        return new FirefoxExtensionSettingsMutation(
            extensionId,
            prior,
            written,
            settings.ToJsonString(CompactJson));
    }

    internal JsonPropertyRollbackDecision DecideRollback(string? currentValue)
    {
        JsonObject settings;
        try
        {
            settings = ParseSettings(currentValue);
        }
        catch
        {
            return new JsonPropertyRollbackDecision(JsonPropertyRollbackAction.RetainCurrent, null);
        }

        var currentPolicy = settings[ExtensionId];
        var current = new RegistryValueSnapshot(
            currentPolicy is not null,
            currentPolicy?.ToJsonString(CompactJson),
            "Json");
        var ownership = new RegistryValueOwnership(
            string.Empty,
            string.Empty,
            string.Empty,
            Prior,
            Written,
            ExtensionId);
        var decision = InstallDecisions.DecideRegistryRemoval(ownership, current);
        if (decision.Action == RegistryRemovalAction.RetainCurrent)
        {
            return new JsonPropertyRollbackDecision(JsonPropertyRollbackAction.RetainCurrent, null);
        }

        if (Prior.Exists)
        {
            settings[ExtensionId] = JsonNode.Parse(Prior.Value!);
        }
        else
        {
            settings.Remove(ExtensionId);
        }

        return settings.Count == 0
            ? new JsonPropertyRollbackDecision(JsonPropertyRollbackAction.DeleteValue, null)
            : new JsonPropertyRollbackDecision(
                JsonPropertyRollbackAction.SetValue,
                settings.ToJsonString(CompactJson));
    }

    private static JsonObject ParseSettings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new JsonObject();
        }

        return JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidOperationException(
                "The existing Firefox ExtensionSettings value is not a JSON object.");
    }
}
