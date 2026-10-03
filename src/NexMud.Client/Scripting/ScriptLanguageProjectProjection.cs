using System.Text.Json;
using System.Text.Json.Nodes;

namespace NexMud.Client.Scripting;

public sealed record ScriptLanguageProjectProjectionResult(
    string WorkspaceRoot,
    string BaseConfigPath,
    string SdkPackageRoot,
    string SdkDeclarationPath,
    IReadOnlyList<string> PackageConfigPaths);

/// <summary>
/// Materializes the filesystem project shape consumed by the TypeScript language service.
/// NexMUD owns the shared base configuration and SDK projection, but preserves package-local
/// tsconfig.json files when users choose to provide their own project configuration.
/// </summary>
public static class ScriptLanguageProjectProjection
{
    public const string BaseConfigFileName = "tsconfig.base.json";
    public const string PackageConfigFileName = "tsconfig.json";
    public const string SdkPackageName = "@nexmud/api";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static async Task<ScriptLanguageProjectProjectionResult> EnsureAsync(
        string workspaceRoot,
        IReadOnlyList<ScriptPackageCatalogEntry> packages,
        string sdkDeclarations,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentNullException.ThrowIfNull(sdkDeclarations);

        string root = Path.GetFullPath(workspaceRoot);
        string nexMudRoot = Path.Combine(root, ScriptPackageWorkspaceMigrator.NexMudDirectoryName);
        string sdkRoot = Path.Combine(nexMudRoot, "sdk", "@nexmud", "api");
        string sdkDeclarationPath = Path.Combine(sdkRoot, "index.d.ts");
        string baseConfigPath = Path.Combine(nexMudRoot, BaseConfigFileName);

        Directory.CreateDirectory(sdkRoot);
        await WriteIfDifferentAsync(
            Path.Combine(sdkRoot, "package.json"),
            CreateSdkPackageJson(),
            cancellationToken).ConfigureAwait(false);
        await WriteIfDifferentAsync(
            sdkDeclarationPath,
            sdkDeclarations.TrimEnd() + Environment.NewLine,
            cancellationToken).ConfigureAwait(false);
        await WriteIfDifferentAsync(
            baseConfigPath,
            CreateBaseConfigJson(),
            cancellationToken).ConfigureAwait(false);

        List<string> packageConfigs = [];
        foreach (ScriptPackageCatalogEntry package in packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string configPath = Path.Combine(package.PackageRoot, PackageConfigFileName);
            if (!File.Exists(configPath))
            {
                await AtomicWriteAsync(
                    configPath,
                    CreatePackageConfigJson(),
                    cancellationToken).ConfigureAwait(false);
            }
            packageConfigs.Add(configPath);
        }

        return new ScriptLanguageProjectProjectionResult(
            root,
            baseConfigPath,
            sdkRoot,
            sdkDeclarationPath,
            packageConfigs);
    }

    public static string ToCanonicalFileUri(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string normalized = fullPath.Replace('\\', '/');
        if (OperatingSystem.IsWindows() && !normalized.StartsWith('/')) normalized = "/" + normalized;
        return new UriBuilder(Uri.UriSchemeFile, string.Empty) { Path = normalized }.Uri.AbsoluteUri;
    }

    private static string CreateSdkPackageJson()
    {
        JsonObject root = new()
        {
            ["name"] = SdkPackageName,
            ["version"] = "1.0.0",
            ["private"] = true,
            ["types"] = "./index.d.ts",
            ["exports"] = new JsonObject
            {
                ["."] = new JsonObject
                {
                    ["types"] = "./index.d.ts"
                }
            }
        };
        return root.ToJsonString(JsonOptions) + Environment.NewLine;
    }

    private static string CreateBaseConfigJson()
    {
        JsonObject root = new()
        {
            ["compilerOptions"] = new JsonObject
            {
                ["target"] = "ES2022",
                ["module"] = "ESNext",
                ["moduleResolution"] = "Bundler",
                ["strict"] = true,
                ["noEmit"] = true,
                ["allowJs"] = true,
                ["checkJs"] = true,
                ["resolveJsonModule"] = true,
                ["skipLibCheck"] = true,
                ["baseUrl"] = "..",
                ["paths"] = new JsonObject
                {
                    [SdkPackageName] = new JsonArray(".nexmud/sdk/@nexmud/api/index.d.ts")
                }
            }
        };
        return root.ToJsonString(JsonOptions) + Environment.NewLine;
    }

    private static string CreatePackageConfigJson()
    {
        JsonObject root = new()
        {
            ["extends"] = "../.nexmud/tsconfig.base.json",
            ["include"] = new JsonArray("**/*.ts", "**/*.tsx", "**/*.js", "**/*.jsx", "**/*.d.ts"),
            ["exclude"] = new JsonArray("node_modules", "dist", ".nexmud")
        };
        return root.ToJsonString(JsonOptions) + Environment.NewLine;
    }

    private static async Task WriteIfDifferentAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        if (File.Exists(path) &&
            string.Equals(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), content, StringComparison.Ordinal))
        {
            return;
        }
        await AtomicWriteAsync(path, content, cancellationToken).ConfigureAwait(false);
    }

    private static async Task AtomicWriteAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
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
