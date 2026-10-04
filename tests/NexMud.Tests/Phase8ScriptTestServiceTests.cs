using System.Text.RegularExpressions;
using NexMud.Client.Scripting;
using NexMud.Scripting.Tooling;
using Assert = NexMud.Tests.Program.Assert;

namespace NexMud.Tests;

internal static class Phase8ScriptTestServiceTests
{
    public static async Task DiscoversSupportedPackageTests()
    {
        using TemporaryDirectory temporary = new();
        string root = Path.Combine(temporary.Path, "package");
        Directory.CreateDirectory(Path.Combine(root, "src", "nested"));
        Directory.CreateDirectory(Path.Combine(root, "test"));
        Directory.CreateDirectory(Path.Combine(root, ".nexmud", "src"));
        await File.WriteAllTextAsync(Path.Combine(root, "src", "nested", "combat.test.ts"), "");
        await File.WriteAllTextAsync(Path.Combine(root, "test", "routes.spec.ts"), "");
        await File.WriteAllTextAsync(Path.Combine(root, "src", "not-a-test.ts"), "");
        await File.WriteAllTextAsync(Path.Combine(root, ".nexmud", "src", "generated.test.ts"), "");

        ScriptTestService service = new(packageRootResolver: (_, _) => root);
        IReadOnlyList<ScriptTestCase> tests = await service.DiscoverAsync("profile", "package");

        Assert.Equal(2, tests.Count);
        Assert.Equal("src/nested/combat.test.ts", tests[0].FilePath);
        Assert.Equal("test/routes.spec.ts", tests[1].FilePath);
    }

    public static async Task RunsBundledVitestAndMapsAssertionLocations()
    {
        using TemporaryDirectory temporary = new();
        string root = Path.Combine(temporary.Path, "package");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        string testFile = Path.Combine(root, "src", "sample.test.ts");
        await File.WriteAllTextAsync(testFile, "test('sample', () => {})");
        RecordingRunner runner = new();
        ScriptTestService service = new(new FakeToolchain(temporary.Path), runner, (_, _) => root, new PassThroughSandboxProvider());

        ScriptTestRunResult result = await service.RunAsync(new ScriptTestSelection("profile", "package"));

        Assert.True(!result.Success);
        Assert.Equal(1, result.Passed);
        Assert.Equal(1, result.Failed);
        Assert.Equal("src/sample.test.ts", result.Assertions[1].FilePath);
        Assert.Equal(7, result.Assertions[1].Line);
        Assert.True(result.Diagnostics[0].Message.Contains("expected true", StringComparison.Ordinal));
        ToolProcessRequest request = runner.Request ?? throw new InvalidOperationException("Vitest process was not requested.");
        Assert.True(request.Arguments.Contains("--permission"));
        Assert.True(request.Arguments.Contains("--reporter=json"));
        Assert.True(request.Arguments.Any(argument => argument.Contains("nexmud-vitest.config.mjs", StringComparison.Ordinal)));
        Assert.True(runner.ShimSource?.Contains("Unavailable in test shim.", StringComparison.Ordinal) == true);
        Assert.True(runner.ConfigSource?.Contains("@nexmud/api", StringComparison.Ordinal) == true);
        Assert.True(File.Exists(testFile), "Tests execute from a temporary copy; package source must remain in place.");
    }

    public static async Task RunsRealBundledVitestAgainstOfflineApiShim()
    {
        if (!OperatingSystem.IsMacOS()) return;

        ToolchainLocator toolchain = new();
        ToolchainComponentLocation node = toolchain.ResolveRequired(ToolchainComponentNames.Node);
        ToolchainComponentLocation vitest = toolchain.ResolveRequired(ToolchainComponentNames.Vitest);
        Assert.True(File.Exists(node.FullPath), "Bundled Node must be available for the Vitest integration test.");
        Assert.True(File.Exists(vitest.FullPath), "Bundled Vitest must be available for the integration test.");

        using TemporaryDirectory temporary = new();
        string root = Path.Combine(temporary.Path, "package");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        await File.WriteAllTextAsync(Path.Combine(root, "package.json"), "{\"name\":\"nexmud-test-fixture\",\"version\":\"1.0.0\"}");
        string dependency = Path.Combine(root, "node_modules", "offline-fixture");
        Directory.CreateDirectory(dependency);
        await File.WriteAllTextAsync(Path.Combine(dependency, "package.json"), "{\"name\":\"offline-fixture\",\"type\":\"module\",\"exports\":\"./index.js\"}");
        await File.WriteAllTextAsync(Path.Combine(dependency, "index.js"), "export const fixtureValue = 42;");
        await File.WriteAllTextAsync(Path.Combine(root, "src", "offline.test.ts"), """
            import { expect, it } from 'vitest';
            import { nex } from '@nexmud/api';
            import { fixtureValue } from 'offline-fixture';

            it('uses the disconnected test API shim and package dependencies', () => {
              expect(nex.state.connected).toBe(false);
              expect(fixtureValue).toBe(42);
            });

            it('reports a failing assertion location', () => {
              expect(1).toBe(2);
            });
            """);
        ScriptTestService service = new(toolchain: toolchain, packageRootResolver: (_, _) => root);

        ScriptTestRunResult result = await service.RunAsync(new ScriptTestSelection("profile", "package"));

        Assert.True(!result.Success);
        Assert.Equal(1, result.Passed, string.Join(Environment.NewLine, result.Diagnostics.Select(item => item.Message)) + Environment.NewLine + result.Output);
        Assert.Equal(1, result.Failed);
        Assert.Equal("src/offline.test.ts", result.Assertions[1].FilePath);
        Assert.Equal(10, result.Assertions[1].Line);
        Assert.Equal("src/offline.test.ts", result.Diagnostics[0].FilePath);
        Assert.True(File.Exists(Path.Combine(root, "src", "offline.test.ts")), "Tests execute from a temporary copy.");
    }

    public static async Task AppliesSelectedTestNamesAsEscapedVitestArguments()
    {
        using TemporaryDirectory temporary = new();
        string root = Path.Combine(temporary.Path, "package");
        Directory.CreateDirectory(Path.Combine(root, "test"));
        await File.WriteAllTextAsync(Path.Combine(root, "test", "selected.test.ts"), "test('sample', () => {})");
        RecordingRunner runner = new();
        ScriptTestService service = new(new FakeToolchain(temporary.Path), runner, (_, _) => root, new PassThroughSandboxProvider());
        string testName = "combat [boss] (phase 2)";

        await service.RunAsync(new ScriptTestSelection("profile", "package", ["test/selected.test.ts"], [testName]));

        ToolProcessRequest request = runner.Request ?? throw new InvalidOperationException("Vitest process was not requested.");
        int patternIndex = Array.IndexOf(request.Arguments.ToArray(), "--testNamePattern");
        Assert.True(patternIndex >= 0);
        Assert.Equal("^(?:" + Regex.Escape(testName) + ")$", request.Arguments[patternIndex + 1]);
        Assert.True(request.Arguments.Contains("test/selected.test.ts"));
    }

    public static async Task RejectsOversizedVitestReport()
    {
        using TemporaryDirectory temporary = new();
        string root = Path.Combine(temporary.Path, "package");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        await File.WriteAllTextAsync(Path.Combine(root, "src", "sample.test.ts"), "test('sample', () => {})");
        RecordingRunner runner = new() { ReportContents = new string(' ', 8 * 1024 * 1024 + 1) };
        ScriptTestService service = new(new FakeToolchain(temporary.Path), runner, (_, _) => root, new PassThroughSandboxProvider());

        ScriptTestRunResult result = await service.RunAsync(new ScriptTestSelection("profile", "package"));

        Assert.False(result.Success);
        Assert.True(result.Diagnostics[0].Message.Contains("8 MiB safety limit", StringComparison.Ordinal));
    }

    public static async Task ReportsMalformedVitestReport()
    {
        using TemporaryDirectory temporary = new();
        string root = Path.Combine(temporary.Path, "package");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        await File.WriteAllTextAsync(Path.Combine(root, "src", "sample.test.ts"), "test('sample', () => {})");
        RecordingRunner runner = new()
        {
            ReportContents = """{"testResults":[{"name":"src/sample.test.ts","status":"failed","assertionResults":{}}]}"""
        };
        ScriptTestService service = new(new FakeToolchain(temporary.Path), runner, (_, _) => root, new PassThroughSandboxProvider());

        ScriptTestRunResult result = await service.RunAsync(new ScriptTestSelection("profile", "package"));

        Assert.True(!result.Success);
        Assert.Equal(1, result.ExitCode);
        Assert.True(result.Diagnostics[0].Message.Contains("invalid JSON report", StringComparison.Ordinal));
    }

    public static async Task IgnoresLinkedTestDirectories()
    {
        using TemporaryDirectory temporary = new();
        string root = Path.Combine(temporary.Path, "package");
        string outside = Path.Combine(temporary.Path, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "external.test.ts"), "test('external', () => {})");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "tests"), outside);
        }
        catch (Exception exception) when (OperatingSystem.IsWindows() && exception is (UnauthorizedAccessException or IOException or PlatformNotSupportedException))
        {
            return;
        }

        ScriptTestService service = new(packageRootResolver: (_, _) => root);
        IReadOnlyList<ScriptTestCase> tests = await service.DiscoverAsync("profile", "package");

        Assert.Equal(0, tests.Count);
    }

    public static async Task PropagatesTestCancellation()
    {
        using TemporaryDirectory temporary = new();
        string root = Path.Combine(temporary.Path, "package");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        await File.WriteAllTextAsync(Path.Combine(root, "src", "sample.test.ts"), "test('sample', () => {})");
        using CancellationTokenSource cancellation = new();
        ScriptTestService service = new(new FakeToolchain(temporary.Path), new CancelingRunner(cancellation), (_, _) => root, new PassThroughSandboxProvider());

        bool cancelled = false;
        try
        {
            await service.RunAsync(new ScriptTestSelection("profile", "package"), cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            cancelled = true;
        }
        Assert.True(cancelled);
    }

    public static async Task ReportsEmptySuiteWithoutStartingProcess()
    {
        using TemporaryDirectory temporary = new();
        string root = Path.Combine(temporary.Path, "package");
        Directory.CreateDirectory(root);
        RecordingRunner runner = new();
        ScriptTestService service = new(processRunner: runner, packageRootResolver: (_, _) => root, sandboxProvider: new PassThroughSandboxProvider());

        ScriptTestRunResult result = await service.RunAsync(new ScriptTestSelection("profile", "package"));

        Assert.True(!result.Success);
        Assert.True(result.Diagnostics[0].Message.Contains("No matching test files", StringComparison.Ordinal));
        Assert.True(runner.Request is null);
    }

    public static Task SandboxProfileQuotesPathsAndAllowsOnlyRequestedRoots()
    {
        using TemporaryDirectory temporary = new();
        string sandbox = Path.Combine(temporary.Path, "test \"root");
        string toolchain = Path.Combine(temporary.Path, "toolchain");
        string dependencies = Path.Combine(temporary.Path, "dependencies");
        Directory.CreateDirectory(sandbox);
        Directory.CreateDirectory(toolchain);
        Directory.CreateDirectory(dependencies);
        string node = Path.Combine(toolchain, "node");
        File.WriteAllText(node, "");

        string physicalSandbox = ResolveTestPhysicalPath(sandbox);
        string physicalToolchain = ResolveTestPhysicalPath(toolchain);
        string physicalDependencies = ResolveTestPhysicalPath(dependencies);
        string physicalNode = ResolveTestPhysicalPath(node);
        string profile = MacOsScriptTestSandboxProvider.CreateProfile(node, [sandbox, toolchain, dependencies], sandbox);
        Assert.True(profile.Contains("(import \"system.sb\")", StringComparison.Ordinal), "system.sb is imported.");
        Assert.Equal(1, profile.Split("(allow process-exec", StringSplitOptions.None).Length - 1);
        Assert.True(profile.Contains("(allow process-exec (literal \"" + physicalNode + "\"))", StringComparison.Ordinal), profile);
        Assert.True(profile.Contains("(allow file-read* (subpath \"" + physicalSandbox.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"))", StringComparison.Ordinal), profile);
        Assert.Equal(3, profile.Split("(allow file-read*", StringSplitOptions.None).Length - 1);
        Assert.True(profile.Contains("(allow file-read* (subpath \"" + physicalToolchain + "\"))", StringComparison.Ordinal), profile);
        Assert.True(profile.Contains("(allow file-read* (subpath \"" + physicalDependencies + "\"))", StringComparison.Ordinal), profile);
        Assert.True(profile.Contains("(allow file-write* (subpath \"" + physicalSandbox.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"))", StringComparison.Ordinal), profile);
        Assert.True(profile.Contains("(deny network*)", StringComparison.Ordinal), "Network is denied.");
        Assert.True(!profile.Contains("(allow file-read*)\n(allow", StringComparison.Ordinal), "No broad file-read rule exists.");

        bool rejected = false;
        try { _ = MacOsScriptTestSandboxProvider.CreateProfile(node, [sandbox + "\n(allow file-read*)"], sandbox); }
        catch (Exception) { rejected = true; }
        Assert.True(rejected, "Newline-bearing paths must not be inserted into the profile.");
        return Task.CompletedTask;
    }

    public static async Task SandboxProviderFailsClosedWhenUnavailable()
    {
        using TemporaryDirectory temporary = new();
        string root = Path.Combine(temporary.Path, "package");
        Directory.CreateDirectory(Path.Combine(root, "test"));
        await File.WriteAllTextAsync(Path.Combine(root, "test", "sample.test.ts"), "test('sample', () => {})");
        RecordingRunner runner = new();
        ScriptTestService service = new(new FakeToolchain(temporary.Path), runner, (_, _) => root, new UnavailableSandboxProvider());

        ScriptTestRunResult result = await service.RunAsync(new ScriptTestSelection("profile", "package"));

        Assert.False(result.Success);
        Assert.True(result.Diagnostics[0].Message.Contains("sandbox unavailable", StringComparison.Ordinal));
        Assert.True(runner.Request is null, "Unavailable sandbox must not start the test process.");
    }

    public static async Task MacOsSandboxDeniesNetworkAndOutsideFilesystem()
    {
        if (!OperatingSystem.IsMacOS()) return;
        ToolchainLocator toolchain = new();
        string node = toolchain.ResolveRequired(ToolchainComponentNames.Node).FullPath;
        using TemporaryDirectory temporary = new();
        string sandbox = Path.Combine(temporary.Path, "sandbox");
        string outside = Path.Combine(temporary.Path, "outside");
        Directory.CreateDirectory(sandbox);
        Directory.CreateDirectory(outside);
        string sentinel = Path.Combine(outside, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "outside");
        string writeTarget = Path.Combine(outside, "must-not-exist.txt");
        string script = """
            import fs from 'node:fs';
            import net from 'node:net';
            let readDenied = false, writeDenied = false;
            try { fs.readFileSync(SENTINEL); } catch (e) { readDenied = ['EPERM','EACCES'].includes(e.code); }
            try { fs.writeFileSync(WRITE_TARGET, 'escape'); } catch (e) { writeDenied = ['EPERM','EACCES'].includes(e.code); }
            const networkDenied = await new Promise(resolve => {
              const socket = net.connect(9, '127.0.0.1');
              socket.once('error', error => resolve(['EPERM','EACCES'].includes(error.code)));
              socket.setTimeout(1000, () => { socket.destroy(); resolve(false); });
            });
            console.log(JSON.stringify({ readDenied, writeDenied, networkDenied }));
            """.Replace("SENTINEL", System.Text.Json.JsonSerializer.Serialize(sentinel), StringComparison.Ordinal)
            .Replace("WRITE_TARGET", System.Text.Json.JsonSerializer.Serialize(writeTarget), StringComparison.Ordinal);
        ToolProcessRequest original = new(node, ["--input-type=module", "-e", script], sandbox, [Path.GetDirectoryName(node)!]);
        MacOsScriptTestSandboxProvider provider = new();
        Assert.True(provider.TryCreateRequest(original, [sandbox, toolchain.Root], sandbox, out ToolProcessRequest secured, out string error), error);
        ToolProcessResult result = await new ToolProcessRunner().RunAsync(secured);
        Assert.Equal(0, result.ExitCode, result.StandardError);
        using System.Text.Json.JsonDocument payload = System.Text.Json.JsonDocument.Parse(result.StandardOutput);
        Assert.True(payload.RootElement.GetProperty("readDenied").GetBoolean());
        Assert.True(payload.RootElement.GetProperty("writeDenied").GetBoolean());
        Assert.True(payload.RootElement.GetProperty("networkDenied").GetBoolean());
        Assert.Equal("outside", await File.ReadAllTextAsync(sentinel));
        Assert.False(File.Exists(writeTarget));
    }

    private static string ResolveTestPhysicalPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath)!;
        string current = root;
        foreach (string segment in fullPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            string next = Path.Combine(current, segment);
            FileSystemInfo entry = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            current = (entry.ResolveLinkTarget(true) ?? entry).FullName;
        }
        return Path.GetFullPath(current);
    }

    private sealed class PassThroughSandboxProvider : IScriptTestSandboxProvider
    {
        public bool TryCreateRequest(ToolProcessRequest request, IReadOnlyList<string> allowedReadRoots, string writeRoot,
            out ToolProcessRequest sandboxedRequest, out string error)
        {
            sandboxedRequest = request;
            error = "";
            return true;
        }
    }

    private sealed class UnavailableSandboxProvider : IScriptTestSandboxProvider
    {
        public bool TryCreateRequest(ToolProcessRequest request, IReadOnlyList<string> allowedReadRoots, string writeRoot,
            out ToolProcessRequest sandboxedRequest, out string error)
        {
            sandboxedRequest = request;
            error = "sandbox unavailable";
            return false;
        }
    }

    private sealed class RecordingRunner : IToolProcessRunner
    {
        public ToolProcessRequest? Request { get; private set; }
        public string? ShimSource { get; private set; }
        public string? ConfigSource { get; private set; }
        public string ReportContents { get; init; } = """
            {
              "numPassedTests": 1,
              "numFailedTests": 1,
              "numPendingTests": 0,
              "numTodoTests": 0,
              "testResults": [{
                "name": "src/sample.test.ts",
                "status": "failed",
                "assertionResults": [
                  { "title": "passes", "status": "passed", "location": { "line": 3, "column": 1 } },
                  { "title": "fails", "status": "failed", "failureMessages": ["expected true"], "location": { "line": 7, "column": 5 } }
                ]
              }]
            }
            """;

        public Task<ToolProcessResult> RunAsync(ToolProcessRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            ShimSource = File.ReadAllText(Path.Combine(request.WorkingDirectory, ".nexmud-test-api.mjs"));
            ConfigSource = File.ReadAllText(Path.Combine(request.WorkingDirectory, ".nexmud-vitest.config.mjs"));
            string outputPath = request.Arguments.Single(argument => argument.StartsWith("--outputFile=", StringComparison.Ordinal))[13..];
            File.WriteAllText(outputPath, ReportContents);
            return Task.FromResult(new ToolProcessResult(1, "", ""));
        }
    }

    private sealed class CancelingRunner(CancellationTokenSource cancellation) : IToolProcessRunner
    {
        public Task<ToolProcessResult> RunAsync(ToolProcessRequest request, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return Task.FromCanceled<ToolProcessResult>(cancellationToken);
        }
    }

    private sealed class FakeToolchain(string root) : IToolchainLocator
    {
        public string Root => root;
        public ToolchainManifest Manifest => new(1, ToolchainPlatform.CurrentPlatform, ToolchainPlatform.CurrentArchitecture,
            "test", []);

        public ToolchainComponentLocation ResolveRequired(string componentName) =>
            new(componentName, "1.0.0", Path.Combine(root, componentName), null, true);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nexmud-phase8-" + Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
