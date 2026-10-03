using NexMud.Client.Scripting;

namespace NexMud.Tests;

internal static class Phase6ProjectProjectionTests
{
    public static async Task MaterializesSdkAndProjectConfig()
    {
        string root = Path.Combine(Path.GetTempPath(), "nexmud-phase6-project-" + Guid.NewGuid().ToString("N"));
        try
        {
            string packageRoot = Path.Combine(root, "tools");
            Directory.CreateDirectory(Path.Combine(packageRoot, "src"));
            ScriptPackageDocument package = ScriptPackageDocument.CreateNew(
                "tools",
                "tools",
                ScriptPackageWorkspaceMigrator.ManagedPnpmVersion);
            await File.WriteAllTextAsync(
                Path.Combine(packageRoot, ScriptPackageWorkspaceMigrator.PackageJsonFileName),
                package.ToJson());

            ScriptPackageWorkspaceMigrationResult migration = await ScriptPackageWorkspaceMigrator.EnsureAsync(
                root,
                ScriptPackageWorkspaceMigrator.ManagedPnpmVersion);
            if (!migration.Success || migration.Packages.Count != 1)
                throw new InvalidOperationException("TypeScript project projection requires a valid package catalog.");

            string sdkRoot = Path.Combine(root, ".nexmud", "sdk", "@nexmud", "api");
            string sdkPackage = Path.Combine(sdkRoot, "package.json");
            string sdkDeclarations = Path.Combine(sdkRoot, "index.d.ts");
            string baseConfig = Path.Combine(root, ".nexmud", ScriptLanguageProjectProjection.BaseConfigFileName);
            string packageConfig = Path.Combine(packageRoot, ScriptLanguageProjectProjection.PackageConfigFileName);
            if (!File.Exists(sdkPackage) || !File.Exists(sdkDeclarations) || !File.Exists(baseConfig) || !File.Exists(packageConfig))
                throw new InvalidOperationException("TypeScript language-service projection files were not materialized.");

            string declarations = await File.ReadAllTextAsync(sdkDeclarations);
            if (!declarations.Contains("declare module \"@nexmud/api\"", StringComparison.Ordinal))
                throw new InvalidOperationException("The generated SDK projection does not expose @nexmud/api.");

            string config = await File.ReadAllTextAsync(baseConfig);
            if (!config.Contains(".nexmud/sdk/@nexmud/api/index.d.ts", StringComparison.Ordinal))
                throw new InvalidOperationException("The TypeScript base config does not resolve the NexMUD SDK projection.");

            string canonical = ScriptLanguageProjectProjection.ToCanonicalFileUri(Path.Combine(packageRoot, "src", "main.ts"));
            if (!canonical.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Script document identity must use a canonical file URI.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
