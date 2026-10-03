using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NexMud.Scripting.Permissions;
using NexMud.Scripting.Runtime;

namespace NexMud.Client.Scripting;

public sealed record ScriptPackageNexMudMetadata(
    string Id,
    string ApiVersion,
    string Entry,
    IReadOnlyList<string> Permissions,
    string? DisplayName = null);

public sealed record ScriptPackageRuntimeState(bool Enabled = false);

public sealed record ScriptPackageWorkspaceState(
    int SchemaVersion,
    Dictionary<string, ScriptPackageRuntimeState> Packages)
{
    public static ScriptPackageWorkspaceState Empty { get; } =
        new(1, new Dictionary<string, ScriptPackageRuntimeState>(StringComparer.OrdinalIgnoreCase));
}

public enum ScriptPackageMigrationIssueSeverity
{
    Warning,
    Error
}

public sealed record ScriptPackageMigrationIssue(
    ScriptPackageMigrationIssueSeverity Severity,
    string PackageDirectory,
    string Message);

public sealed record ScriptPackageCatalogEntry(
    string DirectoryName,
    string PackageRoot,
    ScriptPackageDocument Document,
    bool Enabled);

public sealed record ScriptPackageWorkspaceMigrationResult(
    IReadOnlyList<ScriptPackageCatalogEntry> Packages,
    IReadOnlyList<ScriptPackageMigrationIssue> Issues,
    int MigratedPackageCount)
{
    public bool Success => Issues.All(issue => issue.Severity != ScriptPackageMigrationIssueSeverity.Error);
}

public sealed partial class ScriptPackageDocument
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly JsonObject _root;

    private ScriptPackageDocument(JsonObject root)
    {
        _root = root;
        Name = RequireString(root, "name");
        Version = RequireString(root, "version");

        if (root["type"] is JsonNode typeNode &&
            (typeNode is not JsonValue typeValue ||
             !typeValue.TryGetValue<string>(out string? type) ||
             !string.Equals(type, "module", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("NexMUD script packages must use package.json type 'module'.");
        }

        if (root["packageManager"] is JsonNode packageManagerNode &&
            (packageManagerNode is not JsonValue packageManagerValue ||
             !packageManagerValue.TryGetValue<string>(out string? packageManager) ||
             string.IsNullOrWhiteSpace(packageManager) ||
             !packageManager.StartsWith("pnpm@", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("NexMUD script packages must declare pnpm as packageManager.");
        }

        JsonObject nexml = root["nexmud"] as JsonObject
            ?? throw new InvalidOperationException("package.json is missing the NexMUD 'nexmud' object.");
        string id = ScriptWorkspacePath.NormalizeIdentifier(RequireString(nexml, "id"), "nexmud.id");
        string apiVersion = RequireString(nexml, "apiVersion");
        if (!ScriptApiVersion.IsCompatible(apiVersion))
            throw new InvalidOperationException($"Unsupported NexMUD script API version '{apiVersion}'.");
        string entry = NormalizeEntry(RequireString(nexml, "entry"));
        IReadOnlyList<string> permissions = ReadStringArray(nexml, "permissions");
        _ = ScriptPackagePermissionCodec.Decode(permissions);
        string? displayName = ReadOptionalString(nexml, "displayName");
        NexMud = new ScriptPackageNexMudMetadata(id, apiVersion, entry, permissions, displayName);

        Dependencies = ReadDependencyMap(root, "dependencies");
        DevDependencies = ReadDependencyMap(root, "devDependencies");
    }

    public string Name { get; }
    public string Version { get; }
    public ScriptPackageNexMudMetadata NexMud { get; }
    public IReadOnlyDictionary<string, string> Dependencies { get; }
    public IReadOnlyDictionary<string, string> DevDependencies { get; }
    public JsonObject Root => (JsonObject)_root.DeepClone();

    public static ScriptPackageDocument Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        JsonObject root = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidOperationException("package.json must contain a JSON object.");
        ValidatePackageName(RequireString(root, "name"));
        return new ScriptPackageDocument(root);
    }

    public static async Task<ScriptPackageDocument> LoadAsync(
        string packageJsonPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageJsonPath);
        return Parse(await File.ReadAllTextAsync(packageJsonPath, cancellationToken).ConfigureAwait(false));
    }

    public static ScriptPackageDocument CreateNew(
        string packageName,
        string packageId,
        string bundledPnpmVersion,
        string entry = "./src/main.ts",
        ScriptCapability capabilities = ScriptCapability.ReadState |
                                                ScriptCapability.SubscribeEvents |
                                                ScriptCapability.SendCommands |
                                                ScriptCapability.CreateTimers |
                                                ScriptCapability.ReadScriptStorage |
                                                ScriptCapability.WriteScriptStorage |
                                                ScriptCapability.Log,
        string? displayName = null)
    {
        ValidatePackageName(packageName);
        packageId = ScriptWorkspacePath.NormalizeIdentifier(packageId, nameof(packageId));
        ArgumentException.ThrowIfNullOrWhiteSpace(bundledPnpmVersion);
        string normalizedEntry = NormalizeEntry(entry);
        string[] permissions = ScriptPackagePermissionCodec.Encode(capabilities).ToArray();
        JsonObject root = new()
        {
            ["name"] = packageName,
            ["version"] = "1.0.0",
            ["private"] = true,
            ["type"] = "module",
            ["packageManager"] = $"pnpm@{bundledPnpmVersion.Trim()}",
            ["dependencies"] = new JsonObject(),
            ["devDependencies"] = new JsonObject(),
            ["nexmud"] = new JsonObject
            {
                ["id"] = packageId,
                ["apiVersion"] = ScriptApiVersion.Current,
                ["entry"] = normalizedEntry,
                ["permissions"] = CreatePermissionArray(permissions)
            }
        };
        if (!string.IsNullOrWhiteSpace(displayName))
            ((JsonObject)root["nexmud"]!)["displayName"] = displayName.Trim();
        return new ScriptPackageDocument(root);
    }

    public static ScriptPackageDocument FromLegacy(
        ScriptPackageDefinition legacy,
        string bundledPnpmVersion)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        ScriptPackageDefinition normalized = legacy.Normalize();
        string name = NormalizeLegacyPackageName(normalized.Name, normalized.PackageId);
        return CreateNew(
            name,
            normalized.PackageId,
            bundledPnpmVersion,
            "./" + normalized.Entrypoint,
            normalized.Capabilities,
            normalized.Name).WithVersion(normalized.Version);
    }

    public ScriptPackageDefinition ToRuntimeDefinition(bool enabled) =>
        new(
            NexMud.Id,
            NexMud.DisplayName ?? Name,
            Version,
            NexMud.Entry[2..],
            ScriptPackagePermissionCodec.Decode(NexMud.Permissions),
            enabled).Normalize();

    public string ToJson() => _root.ToJsonString(JsonOptions) + Environment.NewLine;

    private ScriptPackageDocument WithVersion(string version)
    {
        _root["version"] = string.IsNullOrWhiteSpace(version) ? "1.0.0" : version.Trim();
        return new ScriptPackageDocument((JsonObject)_root.DeepClone());
    }

    private static string RequireString(JsonObject root, string propertyName)
    {
        if (root[propertyName] is not JsonValue value ||
            !value.TryGetValue<string>(out string? text) ||
            string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException($"package.json property '{propertyName}' must be a non-empty string.");
        }
        return text.Trim();
    }

    private static string? ReadOptionalString(JsonObject root, string propertyName)
    {
        if (root[propertyName] is null) return null;
        if (root[propertyName] is not JsonValue value ||
            !value.TryGetValue<string>(out string? text) ||
            string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException($"package.json property '{propertyName}' must be a non-empty string when present.");
        }
        return text.Trim();
    }

    private static IReadOnlyList<string> ReadStringArray(JsonObject root, string propertyName)
    {
        if (root[propertyName] is not JsonArray array)
            throw new InvalidOperationException($"package.json property '{propertyName}' must be an array.");
        List<string> values = [];
        foreach (JsonNode? node in array)
        {
            if (node is not JsonValue value || !value.TryGetValue<string>(out string? text) || string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException($"package.json property '{propertyName}' must contain only strings.");
            values.Add(text.Trim());
        }
        return values;
    }

    private static IReadOnlyDictionary<string, string> ReadDependencyMap(JsonObject root, string propertyName)
    {
        if (root[propertyName] is null)
            return new Dictionary<string, string>(StringComparer.Ordinal);
        if (root[propertyName] is not JsonObject dependencies)
            throw new InvalidOperationException($"package.json property '{propertyName}' must be an object.");
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        foreach ((string name, JsonNode? node) in dependencies)
        {
            if (node is not JsonValue value || !value.TryGetValue<string>(out string? spec) || string.IsNullOrWhiteSpace(spec))
                throw new InvalidOperationException($"Dependency '{name}' in '{propertyName}' must use a string version specifier.");
            result[name] = spec.Trim();
        }
        return result;
    }

    private static string NormalizeEntry(string entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entry);
        string candidate = entry.Replace('\\', '/').Trim();
        if (candidate.StartsWith("./", StringComparison.Ordinal)) candidate = candidate[2..];
        string normalized = ScriptWorkspacePath.NormalizeRelativePath(candidate);
        return "./" + normalized;
    }

    private static JsonArray CreatePermissionArray(IEnumerable<string> permissions)
    {
        JsonArray result = [];
        foreach (string permission in permissions) result.Add(permission);
        return result;
    }

    private static void ValidatePackageName(string name)
    {
        if (name.Length > 214 || !PackageNamePattern().IsMatch(name))
            throw new InvalidOperationException($"'{name}' is not a supported npm package name.");
    }

    private static string NormalizeLegacyPackageName(string name, string packageId)
    {
        string candidate = new(name.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-'
                ? character
                : '-')
            .ToArray());
        candidate = string.Join('-', candidate.Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (candidate.Length > 214) candidate = candidate[..214].TrimEnd('-');
        if (!string.IsNullOrWhiteSpace(candidate) && PackageNamePattern().IsMatch(candidate)) return candidate;

        string fallback = packageId.Trim().ToLowerInvariant();
        if (PackageNamePattern().IsMatch(fallback)) return fallback;
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(packageId)))
            .ToLowerInvariant();
        return "nexmud-script-" + digest[..12];
    }

    [GeneratedRegex("^(?:@[a-z0-9][a-z0-9._-]*/)?[a-z0-9][a-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageNamePattern();
}

public static class ScriptPackagePermissionCodec
{
    private static readonly (string Name, ScriptCapability Capability)[] Mappings =
    [
        ("state.read", ScriptCapability.ReadState),
        ("events.subscribe", ScriptCapability.SubscribeEvents),
        ("commands.send", ScriptCapability.SendCommands),
        ("timers.create", ScriptCapability.CreateTimers),
        ("storage.read", ScriptCapability.ReadScriptStorage),
        ("storage.write", ScriptCapability.WriteScriptStorage),
        ("log.write", ScriptCapability.Log),
        ("mapper.read", ScriptCapability.ReadMapper),
        ("mapper.pathfind", ScriptCapability.MapperPathfind),
        ("mapper.move", ScriptCapability.MapperMove),
        ("mapper.route.observe", ScriptCapability.MapperRouteObserve),
        ("codex.read", ScriptCapability.ReadCodex)
    ];

    private static readonly ScriptCapability SupportedCapabilities =
        Mappings.Aggregate(ScriptCapability.None, (current, mapping) => current | mapping.Capability);
    private static readonly IReadOnlyDictionary<string, ScriptCapability> DecodeMappings =
        Mappings.ToDictionary(mapping => mapping.Name, mapping => mapping.Capability, StringComparer.Ordinal);

    public static IReadOnlyList<string> Encode(ScriptCapability capabilities)
    {
        ScriptCapability unsupported = capabilities & ~SupportedCapabilities;
        if (unsupported != ScriptCapability.None)
        {
            throw new InvalidOperationException(
                $"Legacy script capabilities cannot be migrated safely without an approved package permission mapping: {unsupported}.");
        }
        return Mappings
            .Where(mapping => (capabilities & mapping.Capability) == mapping.Capability)
            .Select(mapping => mapping.Name)
            .ToArray();
    }

    public static ScriptCapability Decode(IEnumerable<string> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        ScriptCapability capabilities = ScriptCapability.None;
        foreach (string permission in permissions)
        {
            if (!DecodeMappings.TryGetValue(permission, out ScriptCapability capability))
                throw new InvalidOperationException($"Unsupported NexMUD package permission '{permission}'.");
            capabilities |= capability;
        }
        return capabilities;
    }
}

public sealed record ScriptDependencySourceIssue(string DependencyName, string Specifier, string Message);

public static partial class ScriptPackageDependencyValidator
{
    public static IReadOnlyList<ScriptDependencySourceIssue> Validate(ScriptPackageDocument package)
    {
        ArgumentNullException.ThrowIfNull(package);
        List<ScriptDependencySourceIssue> issues = [];
        Validate(package.Dependencies, issues);
        Validate(package.DevDependencies, issues);
        return issues;
    }

    public static bool IsSupportedSpecifier(string specifier)
    {
        if (string.IsNullOrWhiteSpace(specifier)) return false;
        string value = specifier.Trim();
        if (value.Equals("workspace:*", StringComparison.Ordinal)) return true;
        if (value.StartsWith("git", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("ssh:", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("link:", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("github:", StringComparison.OrdinalIgnoreCase) ||
            value.Contains('/') || value.Contains('\\') || value.Contains('@'))
        {
            return false;
        }
        return RegistrySemverPattern().IsMatch(value) && value.Any(character => char.IsDigit(character) || character is '*' or 'x' or 'X');
    }

    private static void Validate(
        IReadOnlyDictionary<string, string> dependencies,
        ICollection<ScriptDependencySourceIssue> issues)
    {
        foreach ((string name, string specifier) in dependencies)
        {
            if (IsSupportedSpecifier(specifier)) continue;
            issues.Add(new ScriptDependencySourceIssue(
                name,
                specifier,
                "Only npm-registry semver dependencies and profile-local workspace:* dependencies are supported."));
        }
    }

    [GeneratedRegex("^[0-9A-Za-zxX*<>=~^|.\\-+\\s]+$", RegexOptions.CultureInvariant)]
    private static partial Regex RegistrySemverPattern();
}

public static class ScriptPackageWorkspaceMigrator
{
    public const string ManagedPnpmVersion = "12.8.1";
    public const string PackageJsonFileName = "package.json";
    public const string LegacyManifestFileName = "manifest.json";
    public const string WorkspaceFileName = "pnpm-workspace.yaml";
    public const string NpmRcFileName = ".npmrc";
    public const string LockfileName = "pnpm-lock.yaml";
    public const string NexMudDirectoryName = ".nexmud";
    public const string WorkspaceStateFileName = "workspace.json";

    private const string WorkspaceYaml = "packages:\n  - \"*\"\n  - \"!.nexmud\"\n  - \"!node_modules\"\n";
    private const string ControlledNpmRc = """
ignore-scripts=true
shared-workspace-lockfile=true
link-workspace-packages=true
prefer-workspace-packages=true
audit=false
fund=false
update-notifier=false
""";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<ScriptPackageWorkspaceMigrationResult> EnsureAsync(
        string scriptsRoot,
        string bundledPnpmVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptsRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(bundledPnpmVersion);
        string root = Path.GetFullPath(scriptsRoot);
        Directory.CreateDirectory(root);
        await WriteIfDifferentAsync(Path.Combine(root, WorkspaceFileName), WorkspaceYaml, cancellationToken).ConfigureAwait(false);
        await WriteIfDifferentAsync(Path.Combine(root, NpmRcFileName), ControlledNpmRc + Environment.NewLine, cancellationToken).ConfigureAwait(false);

        string statePath = Path.Combine(root, NexMudDirectoryName, WorkspaceStateFileName);
        (ScriptPackageWorkspaceState state, bool stateExisted) = await LoadStateAsync(statePath, cancellationToken).ConfigureAwait(false);
        Dictionary<string, ScriptPackageRuntimeState> packageStates = new(state.Packages, StringComparer.OrdinalIgnoreCase);
        bool stateChanged = !stateExisted;
        int migrated = 0;
        List<ScriptPackageMigrationIssue> issues = [];
        List<ScriptPackageCatalogEntry> packages = [];
        HashSet<string> packageIds = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> packageNames = new(StringComparer.Ordinal);

        foreach (string directory in Directory.EnumerateDirectories(root).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directoryName = Path.GetFileName(directory);
            if (directoryName.Equals(NexMudDirectoryName, StringComparison.OrdinalIgnoreCase) ||
                directoryName.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
                continue;

            string packageJsonPath = Path.Combine(directory, PackageJsonFileName);
            string legacyPath = Path.Combine(directory, LegacyManifestFileName);
            if (!File.Exists(packageJsonPath) && !File.Exists(legacyPath)) continue;

            try
            {
                ScriptPackageDefinition? legacy = File.Exists(legacyPath)
                    ? await ReadLegacyAsync(legacyPath, cancellationToken).ConfigureAwait(false)
                    : null;
                ScriptPackageDocument document;
                bool writeMigratedPackageJson = false;
                if (File.Exists(packageJsonPath))
                {
                    document = await ScriptPackageDocument.LoadAsync(packageJsonPath, cancellationToken).ConfigureAwait(false);
                    if (legacy is not null && !document.NexMud.Id.Equals(legacy.PackageId, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"package.json nexmud.id '{document.NexMud.Id}' does not match legacy package id '{legacy.PackageId}'.");
                    }
                }
                else
                {
                    if (legacy is null)
                        throw new InvalidOperationException("Legacy package manifest is missing or invalid.");
                    document = ScriptPackageDocument.FromLegacy(legacy, bundledPnpmVersion);
                    writeMigratedPackageJson = true;
                }

                if (!packageIds.Add(document.NexMud.Id))
                    throw new InvalidOperationException($"Duplicate NexMUD package id '{document.NexMud.Id}'.");
                if (!packageNames.Add(document.Name))
                    throw new InvalidOperationException($"Duplicate npm package name '{document.Name}'.");

                if (writeMigratedPackageJson)
                {
                    await AtomicWriteAsync(packageJsonPath, document.ToJson(), cancellationToken).ConfigureAwait(false);
                    migrated++;
                }

                if (!packageStates.TryGetValue(document.NexMud.Id, out ScriptPackageRuntimeState? runtimeState))
                {
                    runtimeState = new ScriptPackageRuntimeState(legacy?.Enabled ?? false);
                    packageStates[document.NexMud.Id] = runtimeState;
                    stateChanged = true;
                }

                packages.Add(new ScriptPackageCatalogEntry(
                    directoryName,
                    directory,
                    document,
                    runtimeState.Enabled));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                issues.Add(new ScriptPackageMigrationIssue(
                    ScriptPackageMigrationIssueSeverity.Error,
                    directoryName,
                    exception.Message));
            }
        }

        if (stateChanged)
        {
            ScriptPackageWorkspaceState updated = new(1, packageStates);
            await AtomicWriteAsync(
                statePath,
                JsonSerializer.Serialize(updated, JsonOptions) + Environment.NewLine,
                cancellationToken).ConfigureAwait(false);
        }

        return new ScriptPackageWorkspaceMigrationResult(packages, issues, migrated);
    }

    public static async Task<ScriptPackageWorkspaceState> ReadStateAsync(
        string scriptsRoot,
        CancellationToken cancellationToken = default)
    {
        string statePath = Path.Combine(Path.GetFullPath(scriptsRoot), NexMudDirectoryName, WorkspaceStateFileName);
        return (await LoadStateAsync(statePath, cancellationToken).ConfigureAwait(false)).State;
    }

    public static async Task WriteRuntimeStateAsync(
        string scriptsRoot,
        string packageId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        packageId = ScriptWorkspacePath.NormalizeIdentifier(packageId, nameof(packageId));
        string statePath = Path.Combine(Path.GetFullPath(scriptsRoot), NexMudDirectoryName, WorkspaceStateFileName);
        (ScriptPackageWorkspaceState state, _) = await LoadStateAsync(statePath, cancellationToken).ConfigureAwait(false);
        Dictionary<string, ScriptPackageRuntimeState> packages = new(state.Packages, StringComparer.OrdinalIgnoreCase)
        {
            [packageId] = new ScriptPackageRuntimeState(enabled)
        };
        await AtomicWriteAsync(
            statePath,
            JsonSerializer.Serialize(new ScriptPackageWorkspaceState(1, packages), JsonOptions) + Environment.NewLine,
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task RemoveRuntimeStateAsync(
        string scriptsRoot,
        string packageId,
        CancellationToken cancellationToken = default)
    {
        packageId = ScriptWorkspacePath.NormalizeIdentifier(packageId, nameof(packageId));
        string statePath = Path.Combine(Path.GetFullPath(scriptsRoot), NexMudDirectoryName, WorkspaceStateFileName);
        (ScriptPackageWorkspaceState state, bool exists) = await LoadStateAsync(statePath, cancellationToken).ConfigureAwait(false);
        if (!exists) return;
        Dictionary<string, ScriptPackageRuntimeState> packages = new(state.Packages, StringComparer.OrdinalIgnoreCase);
        if (!packages.Remove(packageId)) return;
        await AtomicWriteAsync(
            statePath,
            JsonSerializer.Serialize(new ScriptPackageWorkspaceState(1, packages), JsonOptions) + Environment.NewLine,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(ScriptPackageWorkspaceState State, bool Exists)> LoadStateAsync(
        string statePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(statePath)) return (ScriptPackageWorkspaceState.Empty, false);
        try
        {
            string json = await File.ReadAllTextAsync(statePath, cancellationToken).ConfigureAwait(false);
            ScriptPackageWorkspaceState? state = JsonSerializer.Deserialize<ScriptPackageWorkspaceState>(json, JsonOptions);
            if (state is null || state.SchemaVersion != 1)
                throw new InvalidOperationException("Unsupported or empty NexMUD script workspace state.");
            return (state with
            {
                Packages = new Dictionary<string, ScriptPackageRuntimeState>(state.Packages, StringComparer.OrdinalIgnoreCase)
            }, true);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("NexMUD script workspace state is invalid JSON.", exception);
        }
    }

    private static async Task<ScriptPackageDefinition?> ReadLegacyAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<ScriptPackageDefinition>(json, JsonOptions)?.Normalize();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Legacy script manifest is invalid JSON: {path}", exception);
        }
    }

    private static async Task WriteIfDifferentAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        if (File.Exists(path) && string.Equals(
                await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false),
                content,
                StringComparison.Ordinal))
            return;
        await AtomicWriteAsync(path, content, cancellationToken).ConfigureAwait(false);
    }

    private static async Task AtomicWriteAsync(string path, string content, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(temporary, content, cancellationToken).ConfigureAwait(false);
        try
        {
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temporary); } catch { }
            throw;
        }
    }
}
