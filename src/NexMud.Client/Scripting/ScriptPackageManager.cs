using NexMud.Client.Paths;
using NexMud.Scripting.Tooling;

namespace NexMud.Client.Scripting;

public sealed record ScriptPackageOperationResult(
    bool Success,
    string Code,
    string Message,
    int? ExitCode = null,
    string StandardOutput = "",
    string StandardError = "")
{
    public static ScriptPackageOperationResult Failed(string code, string message) =>
        new(false, code, message);
}

public interface IScriptPackageManager
{
    Task<ScriptPackageOperationResult> RestoreAsync(string profileId, CancellationToken cancellationToken = default);
    Task<ScriptPackageOperationResult> InstallAsync(string profileId, CancellationToken cancellationToken = default);
    Task<ScriptPackageOperationResult> UpdateAsync(
        string profileId,
        string? packageId = null,
        CancellationToken cancellationToken = default);
    Task<ScriptPackageOperationResult> CleanAsync(
        string profileId,
        string? packageId = null,
        CancellationToken cancellationToken = default);
}

public sealed class ScriptPackageManager : IScriptPackageManager
{
    private readonly IToolchainLocator _toolchain;
    private readonly IToolProcessRunner _processRunner;
    private readonly Func<string, string> _workspaceRootResolver;
    private readonly Func<string, string> _storeRootResolver;

    public ScriptPackageManager(
        IToolchainLocator? toolchain = null,
        IToolProcessRunner? processRunner = null,
        Func<string, string>? workspaceRootResolver = null,
        Func<string, string>? storeRootResolver = null)
    {
        _toolchain = toolchain ?? new ToolchainLocator();
        _processRunner = processRunner ?? new ToolProcessRunner();
        _workspaceRootResolver = workspaceRootResolver ?? ResolveWorkspaceRoot;
        _storeRootResolver = storeRootResolver ?? ResolveStoreRoot;
    }

    public async Task<ScriptPackageOperationResult> RestoreAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        PreparedWorkspace prepared = await PrepareAsync(profileId, cancellationToken).ConfigureAwait(false);
        if (prepared.Failure is not null) return prepared.Failure;
        string lockfile = Path.Combine(prepared.WorkspaceRoot, ScriptPackageWorkspaceMigrator.LockfileName);
        if (!File.Exists(lockfile))
        {
            return ScriptPackageOperationResult.Failed(
                "LockfileMissing",
                "This script workspace has no pnpm-lock.yaml. Run Install to resolve dependencies and create the lockfile.");
        }

        return await RunPnpmAsync(
            prepared,
            ["install", "--recursive", "--frozen-lockfile", "--ignore-scripts"],
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ScriptPackageOperationResult> InstallAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        PreparedWorkspace prepared = await PrepareAsync(profileId, cancellationToken).ConfigureAwait(false);
        if (prepared.Failure is not null) return prepared.Failure;
        return await RunPnpmAsync(
            prepared,
            ["install", "--recursive", "--no-frozen-lockfile", "--ignore-scripts"],
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ScriptPackageOperationResult> UpdateAsync(
        string profileId,
        string? packageId = null,
        CancellationToken cancellationToken = default)
    {
        PreparedWorkspace prepared = await PrepareAsync(profileId, cancellationToken).ConfigureAwait(false);
        if (prepared.Failure is not null) return prepared.Failure;

        IReadOnlyList<string> arguments;
        if (string.IsNullOrWhiteSpace(packageId))
        {
            arguments = ["update", "--recursive", "--ignore-scripts"];
        }
        else
        {
            ScriptPackageCatalogEntry? package = prepared.Migration.Packages.FirstOrDefault(candidate =>
                candidate.Document.NexMud.Id.Equals(packageId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (package is null)
                return ScriptPackageOperationResult.Failed("PackageNotFound", $"Script package '{packageId}' was not found.");
            arguments = ["--filter", package.Document.Name, "update", "--ignore-scripts"];
        }

        return await RunPnpmAsync(prepared, arguments, cancellationToken).ConfigureAwait(false);
    }

    public Task<ScriptPackageOperationResult> CleanAsync(
        string profileId,
        string? packageId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string root = Path.GetFullPath(_workspaceRootResolver(NormalizeProfileId(profileId)));
        if (!Directory.Exists(root))
            return Task.FromResult(new ScriptPackageOperationResult(true, "Clean", "Script workspace is already clean."));

        if (string.IsNullOrWhiteSpace(packageId))
        {
            DeleteDirectory(Path.Combine(root, "node_modules"));
            DeleteDirectory(Path.Combine(root, ScriptPackageWorkspaceMigrator.NexMudDirectoryName, "build"));
            foreach (string directory in Directory.EnumerateDirectories(root))
            {
                string name = Path.GetFileName(directory);
                if (name.Equals(ScriptPackageWorkspaceMigrator.NexMudDirectoryName, StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
                    continue;
                DeleteDirectory(Path.Combine(directory, "node_modules"));
            }
            return Task.FromResult(new ScriptPackageOperationResult(true, "Clean", "Removed generated dependency and build artifacts."));
        }

        string normalizedPackageId = packageId.Trim();
        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            string packageJson = Path.Combine(directory, ScriptPackageWorkspaceMigrator.PackageJsonFileName);
            if (!File.Exists(packageJson)) continue;
            ScriptPackageDocument document;
            try
            {
                document = ScriptPackageDocument.Parse(File.ReadAllText(packageJson));
            }
            catch
            {
                continue;
            }
            if (!document.NexMud.Id.Equals(normalizedPackageId, StringComparison.OrdinalIgnoreCase)) continue;
            DeleteDirectory(Path.Combine(directory, "node_modules"));
            DeleteDirectory(Path.Combine(
                root,
                ScriptPackageWorkspaceMigrator.NexMudDirectoryName,
                "build",
                normalizedPackageId));
            return Task.FromResult(new ScriptPackageOperationResult(true, "Clean", $"Cleaned script package '{document.Name}'."));
        }

        return Task.FromResult(ScriptPackageOperationResult.Failed(
            "PackageNotFound",
            $"Script package '{normalizedPackageId}' was not found."));
    }

    private async Task<PreparedWorkspace> PrepareAsync(string profileId, CancellationToken cancellationToken)
    {
        profileId = NormalizeProfileId(profileId);
        ToolchainComponentLocation pnpm;
        ToolchainComponentLocation node;
        try
        {
            pnpm = _toolchain.ResolveRequired(ToolchainComponentNames.Pnpm);
            node = _toolchain.ResolveRequired(ToolchainComponentNames.Node);
        }
        catch (Exception exception) when (exception is ToolchainUnavailableException or FileNotFoundException or DirectoryNotFoundException or PlatformNotSupportedException)
        {
            return PreparedWorkspace.FromFailure(
                ScriptPackageOperationResult.Failed("ToolchainUnavailable", exception.Message));
        }

        string workspaceRoot = Path.GetFullPath(_workspaceRootResolver(profileId));
        ScriptPackageWorkspaceMigrationResult migration;
        try
        {
            migration = await ScriptPackageWorkspaceMigrator.EnsureAsync(
                workspaceRoot,
                pnpm.Version,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return PreparedWorkspace.FromFailure(
                ScriptPackageOperationResult.Failed("WorkspaceInvalid", exception.Message));
        }

        ScriptPackageMigrationIssue[] migrationErrors = migration.Issues
            .Where(issue => issue.Severity == ScriptPackageMigrationIssueSeverity.Error)
            .ToArray();
        if (migrationErrors.Length > 0)
        {
            return PreparedWorkspace.FromFailure(ScriptPackageOperationResult.Failed(
                "WorkspaceMigrationFailed",
                string.Join(Environment.NewLine, migrationErrors.Select(issue => $"{issue.PackageDirectory}: {issue.Message}"))));
        }

        List<string> dependencyErrors = [];
        foreach (ScriptPackageCatalogEntry package in migration.Packages)
        {
            foreach (ScriptDependencySourceIssue issue in ScriptPackageDependencyValidator.Validate(package.Document))
                dependencyErrors.Add($"{package.Document.Name} -> {issue.DependencyName}@{issue.Specifier}: {issue.Message}");
        }
        if (dependencyErrors.Count > 0)
        {
            return PreparedWorkspace.FromFailure(ScriptPackageOperationResult.Failed(
                "UnsupportedDependencySource",
                string.Join(Environment.NewLine, dependencyErrors)));
        }

        string storeRoot = Path.GetFullPath(_storeRootResolver(profileId));
        Directory.CreateDirectory(storeRoot);
        string pnpmHome = Path.Combine(Path.GetDirectoryName(storeRoot) ?? storeRoot, "pnpm-home");
        Directory.CreateDirectory(pnpmHome);
        return new PreparedWorkspace(workspaceRoot, storeRoot, pnpmHome, pnpm, node, migration, null);
    }

    private async Task<ScriptPackageOperationResult> RunPnpmAsync(
        PreparedWorkspace prepared,
        IReadOnlyList<string> operationArguments,
        CancellationToken cancellationToken)
    {
        List<string> arguments = [.. operationArguments, "--reporter", "append-only", "--store-dir", prepared.StoreRoot];
        IReadOnlyList<string> pathEntries =
        [
            Path.GetDirectoryName(prepared.Pnpm.FullPath) ?? prepared.WorkspaceRoot,
            Path.GetDirectoryName(prepared.Node.FullPath) ?? prepared.WorkspaceRoot
        ];
        ToolProcessResult process;
        try
        {
            process = await _processRunner.RunAsync(
                new ToolProcessRequest(
                    prepared.Pnpm.FullPath,
                    arguments,
                    prepared.WorkspaceRoot,
                    pathEntries,
                    new Dictionary<string, string?>
                    {
                        ["PNPM_HOME"] = prepared.PnpmHome,
                        ["NPM_CONFIG_IGNORE_SCRIPTS"] = "true",
                        ["NPM_CONFIG_STORE_DIR"] = prepared.StoreRoot
                    }),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ScriptPackageOperationResult.Failed("PackageManagerFailed", exception.Message);
        }

        string message = process.ExitCode == 0
            ? "pnpm operation completed successfully."
            : "pnpm operation failed. See package-manager output for details.";
        return new ScriptPackageOperationResult(
            process.ExitCode == 0,
            process.ExitCode == 0 ? "Completed" : "PnpmFailed",
            message,
            process.ExitCode,
            process.StandardOutput,
            process.StandardError);
    }

    private static string NormalizeProfileId(string profileId) =>
        ScriptWorkspacePath.NormalizeIdentifier(profileId, nameof(profileId));

    private static string ResolveWorkspaceRoot(string profileId)
    {
        string dataRoot = ResolveDataRoot();
        return Path.Combine(dataRoot, "profiles", NormalizeProfileId(profileId), "scripts");
    }

    private static string ResolveStoreRoot(string profileId)
    {
        _ = NormalizeProfileId(profileId);
        return Path.Combine(ResolveDataRoot(), "tooling", "pnpm-store");
    }

    private static string ResolveDataRoot() =>
        Path.GetDirectoryName(NexMudDataPaths.GetFilePath("settings.json"))
        ?? throw new InvalidOperationException("Unable to resolve the NexMUD application data directory.");

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private sealed record PreparedWorkspace(
        string WorkspaceRoot,
        string StoreRoot,
        string PnpmHome,
        ToolchainComponentLocation Pnpm,
        ToolchainComponentLocation Node,
        ScriptPackageWorkspaceMigrationResult Migration,
        ScriptPackageOperationResult? Failure)
    {
        public static PreparedWorkspace FromFailure(ScriptPackageOperationResult failure) =>
            new(
                string.Empty,
                string.Empty,
                string.Empty,
                new ToolchainComponentLocation("pnpm", string.Empty, string.Empty, null, true),
                new ToolchainComponentLocation("node", string.Empty, string.Empty, null, true),
                new ScriptPackageWorkspaceMigrationResult([], [], 0),
                failure);
    }
}
