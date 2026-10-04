using NexMud.Client.Scripting;
using NexMud.Scripting.Compilation;
using NexMud.Scripting.Permissions;
using NexMud.Scripting.Runtime;
using NexMud.Scripting.TypeScript.Compiler;

namespace NexMud.Tests;

internal static class Phase7PackageCompilerTests
{
    public static async Task BundlesPackageDependenciesAndEntrypointExports()
    {
        using TemporaryDirectory temporary = new();
        PackageFixture fixture = await PackageFixture.CreateAsync(temporary.Path).ConfigureAwait(false);
        ScriptCompileResult result = await new TypeScriptCompiler().CompileAsync(
            fixture.Request(fixture.MainSource)).ConfigureAwait(false);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        CompiledScriptPackage package = result.Package!;
        Assert.Equal("src/index.js", package.Manifest.Entrypoint);
        Assert.Equal(1, package.Modules.Count);
        CompiledScriptModule module = package.Modules["src/index.js"];
        Assert.True(module.JavaScript.Contains("function double", StringComparison.Ordinal), "The local npm dependency must be bundled.");
        Assert.True(
            !module.JavaScript.Contains("from \"tiny-dependency\"", StringComparison.Ordinal) &&
            !module.JavaScript.Contains("from 'tiny-dependency'", StringComparison.Ordinal),
            "The bundle must not retain a dependency import.");
        Assert.True(!string.IsNullOrWhiteSpace(module.SourceMap), "The package bundle must include a source map.");
        int functionOffset = module.JavaScript.IndexOf("function activate", StringComparison.Ordinal);
        Assert.True(functionOffset >= 0, "The bundle must retain the package entrypoint function.");
        int generatedLine = module.JavaScript[..functionOffset].Count(character => character == '\n') + 1;
        int generatedColumn = functionOffset - module.JavaScript.LastIndexOf('\n', functionOffset) - 1;
        Assert.True(
            ScriptSourceMaps.TryMap(module, generatedLine, generatedColumn, out var location),
            "The bundle source map must map generated code back to the package source.");
        Assert.Equal("src/internal.ts", location.SourceFile);
        Assert.Equal<int?>(3, location.Line);
        Assert.Equal(2, package.ExportedFunctions.Count);
        ExportedScriptFunction run = package.ExportedFunctions.Single(function => function.ExportName == "run");
        Assert.Equal("src/index.ts", run.FunctionRef.ModulePath);
        Assert.Equal("src/internal.ts", run.SourceLocation.SourceFile);

        ExportedScriptFunction activate = package.ExportedFunctions.Single(function => function.ExportName == "activate");
        Assert.Equal("activate", activate.ExportName);
        await Program.AssertCompiledPackageLoadsInJintAsync(package).ConfigureAwait(false);
    }

    public static async Task DiscoversEntrypointExportsFromLocalImports()
    {
        using TemporaryDirectory temporary = new();
        PackageFixture fixture = await PackageFixture.CreateAsync(temporary.Path).ConfigureAwait(false);
        ScriptCompileResult result = await new TypeScriptCompiler().CompileAsync(
            fixture.Request("import { activate } from './internal.js';\nexport { activate };")).ConfigureAwait(false);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        ExportedScriptFunction activate = result.Package!.ExportedFunctions.Single();
        Assert.Equal("activate", activate.ExportName);
        Assert.Equal("src/index.ts", activate.FunctionRef.ModulePath);
        Assert.Equal("src/internal.ts", activate.SourceLocation.SourceFile);
    }

    public static async Task PreservesProjectConfiguredTypeLibraries()
    {
        using TemporaryDirectory temporary = new();
        PackageFixture fixture = await PackageFixture.CreateAsync(temporary.Path).ConfigureAwait(false);
        string globalTypes = Path.Combine(fixture.PackageRoot, "node_modules", "@types", "fixture-globals");
        Directory.CreateDirectory(globalTypes);
        await File.WriteAllTextAsync(Path.Combine(globalTypes, "index.d.ts"), "declare const fixtureGlobal: string;").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(fixture.PackageRoot, "tsconfig.json"), """
            {
              "extends": "../.nexmud/tsconfig.base.json",
              "compilerOptions": { "lib": ["ES2022", "DOM"], "types": ["fixture-globals"] },
              "include": ["**/*.ts", "**/*.js"],
              "exclude": ["node_modules", "dist", ".nexmud"]
            }
            """).ConfigureAwait(false);

        ScriptCompileResult result = await new TypeScriptCompiler().CompileAsync(
            fixture.Request("export function activate(): string { return `${document.title}:${fixtureGlobal}`; }"))
            .ConfigureAwait(false);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    public static async Task RejectsNodeBuiltins()
    {
        using TemporaryDirectory temporary = new();
        PackageFixture fixture = await PackageFixture.CreateAsync(temporary.Path).ConfigureAwait(false);
        string source = "// @ts-nocheck\nimport { readFileSync } from 'node:fs';\nexport function activate(): string { return readFileSync('package.json', 'utf8'); }";
        ScriptCompileResult result = await new TypeScriptCompiler().CompileAsync(fixture.Request(source)).ConfigureAwait(false);

        Assert.True(!result.Success, "Node built-in imports must be rejected before the runtime loads the package.");
        Assert.True(result.Diagnostics.Any(diagnostic =>
            diagnostic.Code == "NEXTS0009" && diagnostic.Message.Contains("node:fs", StringComparison.Ordinal)),
            "The unsupported Node built-in diagnostic must identify node:fs.");
    }

    public static Task DependencySourcesAreValidatedAcrossManifestSections()
    {
        string packageJson = $$"""
            {
              "name": "test-package",
              "version": "1.0.0",
              "type": "module",
              "optionalDependencies": { "optional-package": "^1.2.3" },
              "peerDependencies": { "workspace-package": "workspace:*" },
              "nexmud": {
                "id": "test-package",
                "apiVersion": "{{ScriptApiVersion.Current}}",
                "entry": "./src/index.ts",
                "permissions": []
              }
            }
            """;
        ScriptPackageDocument package = ScriptPackageDocument.Parse(packageJson);
        Assert.Equal(1, package.OptionalDependencies.Count);
        Assert.Equal(1, package.PeerDependencies.Count);
        Assert.Equal(0, ScriptPackageDependencyValidator.Validate(package).Count);

        ScriptPackageDocument unsupported = ScriptPackageDocument.Parse(
            packageJson.Replace("^1.2.3", "git+ssh://example.test/optional.git", StringComparison.Ordinal));
        Assert.Equal(1, ScriptPackageDependencyValidator.Validate(unsupported).Count);
        return Task.CompletedTask;
    }

    public static Task CacheKeyIncludesDependencyGraph()
    {
        ScriptManifest manifest = new(
            new ScriptModuleId("test.package"),
            "Test Package",
            "1.0.0",
            ScriptApiVersion.Current,
            "src/index.ts",
            ScriptCapability.None);
        ScriptCompileRequest first = new(manifest, ScriptSourceLanguage.TypeScript, [new ScriptSourceFile("src/index.ts", "export const value = 1;")])
        {
            PackageContext = new ScriptCompilePackageContext("/tmp/package", "/tmp/package/tsconfig.json", "graph-a")
        };
        ScriptCompileRequest second = first with
        {
            PackageContext = new ScriptCompilePackageContext("/tmp/package", "/tmp/package/tsconfig.json", "graph-b")
        };

        Assert.True(
            ScriptCompileCacheKey.Compute(first, "compiler", ScriptApiVersion.Current) !=
            ScriptCompileCacheKey.Compute(second, "compiler", ScriptApiVersion.Current),
            "Changing the dependency graph must invalidate the compiled package cache key.");
        return Task.CompletedTask;
    }

    private sealed class PackageFixture
    {
        private PackageFixture(string packageRoot)
        {
            PackageRoot = packageRoot;
            MainSource = "export { activate } from './internal.js';\nexport { activate as run } from './internal.js';";
        }

        public string PackageRoot { get; }
        public string MainSource { get; }

        public ScriptCompileRequest Request(string mainSource)
        {
            ScriptManifest manifest = new(
                new ScriptModuleId("test.package"),
                "Test Package",
                "1.0.0",
                ScriptApiVersion.Current,
                "src/index.ts",
                ScriptCapability.None);
            return new ScriptCompileRequest(
                manifest,
                ScriptSourceLanguage.TypeScript,
                [
                    new ScriptSourceFile("src/index.ts", mainSource),
                    new ScriptSourceFile(
                        "src/internal.ts",
                        "import { double } from 'tiny-dependency';\n\nexport function activate(): void {\n  if (double(privateHelper()) !== 10) throw new Error('dependency bundling failed');\n}\nfunction privateHelper(): number { return 5; }")
                ])
            {
                PackageContext = new ScriptCompilePackageContext(
                    PackageRoot,
                    Path.Combine(PackageRoot, "tsconfig.json"),
                    "fixture-dependency-graph")
            };
        }

        public static async Task<PackageFixture> CreateAsync(string root)
        {
            string workspaceRoot = Path.Combine(root, "Scripts");
            string packageRoot = Path.Combine(workspaceRoot, "test-package");
            string sdkPath = Path.Combine(workspaceRoot, ".nexmud", "sdk", "@nexmud", "api", "index.d.ts");
            string baseConfigPath = Path.Combine(workspaceRoot, ".nexmud", "tsconfig.base.json");
            string packageConfigPath = Path.Combine(packageRoot, "tsconfig.json");
            string dependencyRoot = Path.Combine(packageRoot, "node_modules", "tiny-dependency");
            await WriteAsync(sdkPath, "export declare const state: { turn: number };\n").ConfigureAwait(false);
            await WriteAsync(baseConfigPath, """
                {
                  "compilerOptions": {
                    "target": "ES2022",
                    "module": "ESNext",
                    "moduleResolution": "Bundler",
                    "strict": true,
                    "noEmit": true,
                    "allowJs": true,
                    "checkJs": true,
                    "resolveJsonModule": true,
                    "skipLibCheck": true,
                    "paths": { "@nexmud/api": ["./sdk/@nexmud/api/index.d.ts"] }
                  }
                }
                """).ConfigureAwait(false);
            await WriteAsync(packageConfigPath, """
                {
                  "extends": "../.nexmud/tsconfig.base.json",
                  "include": ["**/*.ts", "**/*.js"],
                  "exclude": ["node_modules", "dist", ".nexmud"]
                }
                """).ConfigureAwait(false);
            await WriteAsync(Path.Combine(packageRoot, "package.json"), """
                { "name": "test-package", "version": "1.0.0", "type": "module" }
                """).ConfigureAwait(false);
            await WriteAsync(Path.Combine(dependencyRoot, "package.json"), """
                {
                  "name": "tiny-dependency",
                  "version": "1.0.0",
                  "type": "module",
                  "exports": { ".": { "types": "./index.d.ts", "import": "./index.js" } }
                }
                """).ConfigureAwait(false);
            await WriteAsync(Path.Combine(dependencyRoot, "index.d.ts"), "export declare function double(value: number): number;\n").ConfigureAwait(false);
            await WriteAsync(Path.Combine(dependencyRoot, "index.js"), "export function double(value) { return value * 2; }\n").ConfigureAwait(false);
            return new PackageFixture(packageRoot);
        }

        private static async Task WriteAsync(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content).ConfigureAwait(false);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"nexmud-package-compiler-test-{Guid.NewGuid():N}");
        public string Path { get; }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }

    private static class Assert
    {
        public static void True(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        public static void Equal<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }
}
