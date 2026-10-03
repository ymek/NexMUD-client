using System.Text.Json;

namespace NexMud.Scripting.Tooling;

public interface IToolchainLocator
{
    string Root { get; }
    ToolchainManifest Manifest { get; }
    ToolchainComponentLocation ResolveRequired(string componentName);
}

public sealed class ToolchainLocator : IToolchainLocator
{
    public const string ToolchainRootEnvironmentVariable = "NEXMUD_TOOLCHAIN_ROOT";
    private const string ManifestFileName = "manifest.json";
    private const int SupportedSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string? _explicitRoot;
    private readonly Lazy<(string Root, ToolchainManifest Manifest)> _state;

    public ToolchainLocator(string? root = null)
    {
        _explicitRoot = string.IsNullOrWhiteSpace(root) ? null : Path.GetFullPath(root);
        _state = new Lazy<(string Root, ToolchainManifest Manifest)>(Load, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string Root => _state.Value.Root;
    public ToolchainManifest Manifest => _state.Value.Manifest;

    public ToolchainComponentLocation ResolveRequired(string componentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(componentName);
        (string root, ToolchainManifest manifest) = _state.Value;
        ToolchainComponentManifest component = manifest.Components.FirstOrDefault(candidate =>
            candidate.Name.Equals(componentName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ToolchainUnavailableException(
                $"Bundled scripting toolchain component '{componentName}' is missing from the manifest.");

        string fullPath = CombineInside(root, component.RelativePath);
        if (!File.Exists(fullPath))
            throw new ToolchainUnavailableException(
                $"Bundled scripting toolchain component '{componentName}' was not found at '{fullPath}'.");

        return new ToolchainComponentLocation(
            component.Name,
            component.Version,
            fullPath,
            component.Sha256,
            component.Executable);
    }

    private (string Root, ToolchainManifest Manifest) Load()
    {
        string? root = _explicitRoot ?? FindDefaultRoot();
        if (string.IsNullOrWhiteSpace(root))
            throw new ToolchainUnavailableException(
                "Bundled scripting toolchain was not found. Build/package NexMUD with scripts/prepare-scripting-toolchain.sh first.");

        root = Path.GetFullPath(root);
        string manifestPath = Path.Combine(root, ManifestFileName);
        if (!File.Exists(manifestPath))
            throw new ToolchainUnavailableException($"Bundled scripting toolchain manifest was not found at '{manifestPath}'.");

        ToolchainManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ToolchainManifest>(File.ReadAllText(manifestPath), JsonOptions)
                ?? throw new ToolchainUnavailableException("Bundled scripting toolchain manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new ToolchainUnavailableException("Bundled scripting toolchain manifest is invalid JSON.", exception);
        }

        if (manifest.SchemaVersion != SupportedSchemaVersion)
            throw new ToolchainUnavailableException(
                $"Unsupported scripting toolchain manifest schema '{manifest.SchemaVersion}'. Expected '{SupportedSchemaVersion}'.");
        if (!manifest.Platform.Equals(ToolchainPlatform.CurrentPlatform, StringComparison.OrdinalIgnoreCase) ||
            !manifest.Architecture.Equals(ToolchainPlatform.CurrentArchitecture, StringComparison.OrdinalIgnoreCase))
            throw new ToolchainUnavailableException(
                $"Bundled scripting toolchain targets {manifest.Platform}/{manifest.Architecture}, " +
                $"but this process is {ToolchainPlatform.CurrentPlatform}/{ToolchainPlatform.CurrentArchitecture}.");
        if (manifest.Components.Count == 0)
            throw new ToolchainUnavailableException("Bundled scripting toolchain manifest contains no components.");

        return (root, manifest);
    }

    private static string? FindDefaultRoot()
    {
        string? configured = Environment.GetEnvironmentVariable(ToolchainRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);

        foreach (string candidate in AppOwnedCandidates())
        {
            if (File.Exists(Path.Combine(candidate, ManifestFileName))) return Path.GetFullPath(candidate);
        }
        return null;
    }

    private static IEnumerable<string> AppOwnedCandidates()
    {
        if (OperatingSystem.IsMacOS())
        {
            yield return Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "Resources",
                "AutomationStudio",
                "tooling"));
        }

        yield return Path.Combine(AppContext.BaseDirectory, "AutomationStudio", "tooling");

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        for (int depth = 0; directory is not null && depth < 10; depth++, directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "NexMud.slnx"))) continue;
            yield return Path.Combine(
                directory.FullName,
                ".artifacts",
                "scripting-toolchain",
                ToolchainPlatform.CurrentRid);
            yield break;
        }
    }

    internal static string CombineInside(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
            throw new ToolchainUnavailableException("Toolchain component paths must be relative to the toolchain root.");

        string canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(Path.Combine(
            canonicalRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!candidate.StartsWith(canonicalRoot, comparison))
            throw new ToolchainUnavailableException("Toolchain component path escaped the toolchain root.");
        return candidate;
    }
}
