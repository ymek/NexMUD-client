using System.Text.Json;
using System.Text.RegularExpressions;
using NexMud.Scripting.Tooling;

namespace NexMud.Client.Scripting;

public interface IScriptTestService
{
    Task<IReadOnlyList<ScriptTestCase>> DiscoverAsync(string profileId, string packageId, CancellationToken cancellationToken = default);
    Task<ScriptTestRunResult> RunAsync(ScriptTestSelection selection, CancellationToken cancellationToken = default);
}

public sealed record ScriptTestCase(string FilePath);

public sealed record ScriptTestSelection(
    string ProfileId,
    string PackageId,
    IReadOnlyList<string>? TestFiles = null,
    IReadOnlyList<string>? TestNames = null);

public sealed record ScriptTestDiagnostic(
    string FilePath,
    int? Line,
    int? Column,
    string Message,
    string Severity = "error");

public sealed record ScriptTestAssertionResult(
    string Name,
    string Status,
    string FilePath,
    int? Line,
    int? Column,
    string? Message);

public sealed record ScriptTestRunResult(
    bool Success,
    int? ExitCode,
    int Passed,
    int Failed,
    int Skipped,
    IReadOnlyList<ScriptTestAssertionResult> Assertions,
    IReadOnlyList<ScriptTestDiagnostic> Diagnostics,
    string Output,
    bool OutputTruncated = false);

/// <summary>Runs package tests in an isolated copy with the bundled Vitest and a non-live NexMUD API.</summary>
public sealed class ScriptTestService : IScriptTestService
{
    private const int MaximumReportBytes = 8 * 1024 * 1024;
    private static readonly string[] TestDirectories = ["test", "tests", "src"];
    private readonly IToolchainLocator _toolchain;
    private readonly IToolProcessRunner _processRunner;
    private readonly Func<string, string, string> _packageRootResolver;
    private readonly IScriptTestSandboxProvider _sandboxProvider;

    public ScriptTestService(
        IToolchainLocator? toolchain = null,
        IToolProcessRunner? processRunner = null,
        Func<string, string, string>? packageRootResolver = null,
        IScriptTestSandboxProvider? sandboxProvider = null)
    {
        _toolchain = toolchain ?? new ToolchainLocator();
        _processRunner = processRunner ?? new ToolProcessRunner();
        _packageRootResolver = packageRootResolver ?? ScriptWorkspaceService.ResolvePackageRoot;
        _sandboxProvider = sandboxProvider ?? new MacOsScriptTestSandboxProvider();
    }

    public Task<IReadOnlyList<ScriptTestCase>> DiscoverAsync(
        string profileId,
        string packageId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string packageRoot = _packageRootResolver(profileId, packageId);
        IReadOnlyList<ScriptTestCase> tests = Directory.Exists(packageRoot)
            ? TestDirectories
                .SelectMany(directory => EnumerateTests(Path.Combine(packageRoot, directory), packageRoot))
                .DistinctBy(test => test.FilePath, StringComparer.OrdinalIgnoreCase)
                .OrderBy(test => test.FilePath, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];
        return Task.FromResult(tests);
    }

    public async Task<ScriptTestRunResult> RunAsync(
        ScriptTestSelection selection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<ScriptTestCase> discovered = await DiscoverAsync(
            selection.ProfileId, selection.PackageId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ScriptTestCase> selected = SelectTests(discovered, selection.TestFiles);
        if (selected.Count == 0)
            return Failure("No matching test files were found.");

        string packageRoot = _packageRootResolver(selection.ProfileId, selection.PackageId);
        string sandbox = Path.Combine(Path.GetTempPath(), "nexmud-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(sandbox);
            sandbox = ResolvePhysicalPath(sandbox);
            CopyPackage(packageRoot, sandbox);
            string packageJson = Path.Combine(sandbox, "package.json");
            if (!File.Exists(packageJson))
                await File.WriteAllTextAsync(packageJson, "{\"name\":\"nexmud-test-sandbox\",\"version\":\"1.0.0\"}", cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(sandbox, "pnpm-workspace.yaml"), "packages: []\n", cancellationToken).ConfigureAwait(false);

            string packageModulesPath = Path.Combine(packageRoot, "node_modules");
            string? packageModules = Directory.Exists(packageModulesPath) ? ResolvePhysicalPath(packageModulesPath) : null;
            if (packageModules is not null)
                LinkPackageDependencies(packageModules, Path.Combine(sandbox, "node_modules"));

            ToolchainComponentLocation node = _toolchain.ResolveRequired(ToolchainComponentNames.Node);
            ToolchainComponentLocation vitest = _toolchain.ResolveRequired(ToolchainComponentNames.Vitest);
            string shimPath = Path.Combine(sandbox, ".nexmud-test-api.mjs");
            string configPath = Path.Combine(sandbox, ".nexmud-vitest.config.mjs");
            string resultPath = Path.Combine(sandbox, ".nexmud-vitest-results.json");
            await File.WriteAllTextAsync(shimPath, ApiShim, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(configPath, CreateVitestConfig(sandbox, shimPath), cancellationToken).ConfigureAwait(false);

            // Keep the failure visible if Node's permission model is incompatible with a platform/toolchain;
            // never retry without it. Vitest's thread pool preserves the parent process boundary.
            List<string> arguments =
            [
                "--permission", "--allow-addons", "--allow-worker",
                "--allow-fs-read=" + _toolchain.Root,
                "--allow-fs-read=" + sandbox,
                "--allow-fs-write=" + sandbox
            ];
            if (packageModules is not null) arguments.Add("--allow-fs-read=" + packageModules);
            arguments.Add(vitest.FullPath);
            arguments.Add("run");
            arguments.Add("--pool=threads");
            arguments.Add("--config");
            arguments.Add(configPath);
            arguments.Add("--reporter=json");
            arguments.Add("--outputFile=" + resultPath);
            if (selection.TestNames is { Count: > 0 } testNames)
            {
                arguments.Add("--testNamePattern");
                arguments.Add("^(?:" + string.Join("|", testNames.Distinct(StringComparer.Ordinal).Select(Regex.Escape)) + ")$");
            }
            arguments.AddRange(selected.Select(test => test.FilePath));

            ToolProcessRequest processRequest = new(
                node.FullPath,
                arguments,
                sandbox,
                [Path.GetDirectoryName(node.FullPath) ?? _toolchain.Root],
                OutputCharacterLimit: 1_000_000);
            if (!_sandboxProvider.TryCreateRequest(processRequest, packageModules is null
                    ? [sandbox, _toolchain.Root]
                    : [sandbox, _toolchain.Root, packageModules], sandbox,
                    out ToolProcessRequest sandboxedRequest, out string sandboxError))
                return Failure(sandboxError);

            using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(5));
            using CancellationTokenSource linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            ToolProcessResult process;
            try
            {
                process = await _processRunner.RunAsync(sandboxedRequest, linkedCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                return Failure("Script tests exceeded the 5-minute time limit.");
            }

            string output = process.Output;
            if (!File.Exists(resultPath))
                return new ScriptTestRunResult(false, process.ExitCode, 0, 0, 0, [],
                    [new ScriptTestDiagnostic("", null, null,
                        (string.IsNullOrWhiteSpace(output) ? "Vitest did not produce a JSON report." : output.Trim()) +
                        (process.OutputTruncated ? " [process output truncated]" : ""))], output, process.OutputTruncated);

            byte[]? report = await ReadBoundedReportAsync(resultPath, cancellationToken).ConfigureAwait(false);
            if (report is null)
                return new ScriptTestRunResult(false, process.ExitCode, 0, 0, 0, [],
                    [new ScriptTestDiagnostic("", null, null, "Vitest JSON report exceeded the 8 MiB safety limit.")],
                    output, process.OutputTruncated);

            try
            {
                ScriptTestRunResult parsed = ParseReport(report, sandbox, process.ExitCode, output);
                return parsed with { OutputTruncated = process.OutputTruncated };
            }
            catch (JsonException exception)
            {
                return new ScriptTestRunResult(false, process.ExitCode, 0, 0, 0, [],
                    [new ScriptTestDiagnostic("", null, null, "Vitest produced an invalid JSON report: " + exception.Message)],
                    output, process.OutputTruncated);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failure(exception.Message);
        }
        finally
        {
            try { Directory.Delete(sandbox, recursive: true); }
            catch { /* Best-effort cleanup; the test result remains authoritative. */ }
        }
    }

    private static IEnumerable<ScriptTestCase> EnumerateTests(string directory, string packageRoot)
    {
        if (!Directory.Exists(directory) || IsReparsePoint(directory)) yield break;
        Stack<string> pending = new([directory]);
        while (pending.TryPop(out string? current))
        {
            foreach (string path in Directory.EnumerateFiles(current, "*.ts"))
            {
                if (IsReparsePoint(path)) continue;
                string name = Path.GetFileName(path);
                if (!name.EndsWith(".test.ts", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith(".spec.ts", StringComparison.OrdinalIgnoreCase)) continue;
                yield return new ScriptTestCase(Path.GetRelativePath(packageRoot, path).Replace('\\', '/'));
            }
            foreach (string child in Directory.EnumerateDirectories(current))
            {
                if (Path.GetFileName(child).Equals(".nexmud", StringComparison.OrdinalIgnoreCase) || IsReparsePoint(child)) continue;
                pending.Push(child);
            }
        }
    }

    private static IReadOnlyList<ScriptTestCase> SelectTests(
        IReadOnlyList<ScriptTestCase> discovered,
        IReadOnlyList<string>? requested)
    {
        if (requested is null || requested.Count == 0) return discovered;
        HashSet<string> available = discovered.Select(test => test.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<ScriptTestCase> selected = [];
        foreach (string path in requested)
        {
            string normalized;
            try { normalized = ScriptWorkspacePath.NormalizeRelativePath(path).Replace('\\', '/'); }
            catch (ArgumentException) { return []; }
            if (!available.Contains(normalized)) return [];
            ScriptTestCase test = discovered.First(item => item.FilePath.Equals(normalized, StringComparison.OrdinalIgnoreCase));
            if (!selected.Contains(test)) selected.Add(test);
        }
        return selected;
    }

    private static void LinkPackageDependencies(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string entry in Directory.EnumerateFileSystemEntries(source))
        {
            string name = Path.GetFileName(entry);
            if (name.Equals(".vite-temp", StringComparison.OrdinalIgnoreCase)) continue;
            string target = Path.Combine(destination, name);
            if (Directory.Exists(entry)) Directory.CreateSymbolicLink(target, entry);
            else File.CreateSymbolicLink(target, entry);
        }
    }

    private static void CopyPackage(string source, string destination)
    {
        foreach (string file in Directory.EnumerateFiles(source))
            if (!IsReparsePoint(file)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (string directory in Directory.EnumerateDirectories(source))
        {
            string name = Path.GetFileName(directory);
            if (name.Equals(".nexmud", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                name.Equals(".git", StringComparison.OrdinalIgnoreCase) || IsReparsePoint(directory)) continue;
            CopyDirectory(directory, Path.Combine(destination, name));
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
            if (!IsReparsePoint(file)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (string directory in Directory.EnumerateDirectories(source))
        {
            string name = Path.GetFileName(directory);
            if (name.Equals(".nexmud", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                name.Equals(".git", StringComparison.OrdinalIgnoreCase) || IsReparsePoint(directory)) continue;
            CopyDirectory(directory, Path.Combine(destination, name));
        }
    }

    private static bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static string ResolvePhysicalPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath) ?? throw new IOException("The sandbox path has no filesystem root.");
        string current = root;
        foreach (string segment in fullPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            string next = Path.Combine(current, segment);
            FileSystemInfo entry = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            current = (entry.ResolveLinkTarget(returnFinalTarget: true) ?? entry).FullName;
        }
        return Path.GetFullPath(current);
    }

    private static string CreateVitestConfig(string root, string shim) =>
        "export default { root: " + JsonSerializer.Serialize(root) +
        ", cacheDir: " + JsonSerializer.Serialize(Path.Combine(root, ".nexmud-vitest-cache")) +
        ", resolve: { alias: { '@nexmud/api': " + JsonSerializer.Serialize(shim) + " } }" +
        ", test: { include: ['test/**/*.test.ts','test/**/*.spec.ts','tests/**/*.test.ts','tests/**/*.spec.ts','src/**/*.test.ts','src/**/*.spec.ts'], includeTaskLocation: true } };";

    private static async Task<byte[]?> ReadBoundedReportAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        if (stream.Length > MaximumReportBytes) return null;

        using MemoryStream report = new((int)stream.Length);
        byte[] buffer = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (report.Length + read > MaximumReportBytes) return null;
            report.Write(buffer, 0, read);
        }
        return report.ToArray();
    }

    private static ScriptTestRunResult ParseReport(byte[] json, string sandbox, int exitCode, string output)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("testResults", out JsonElement testResults) ||
            testResults.ValueKind != JsonValueKind.Array)
            throw new JsonException("Vitest report must contain an array of testResults.");

        List<ScriptTestAssertionResult> assertions = [];
        List<ScriptTestDiagnostic> diagnostics = [];
        foreach (JsonElement file in testResults.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object)
                throw new JsonException("Vitest testResults entries must be objects.");
            string filePath = ToPackagePath(sandbox, GetString(file, "name"));
            bool hasAssertions = file.TryGetProperty("assertionResults", out JsonElement assertionResults);
            if (hasAssertions && assertionResults.ValueKind != JsonValueKind.Array)
                throw new JsonException("Vitest assertionResults must be an array.");
            JsonElement.ArrayEnumerator fileAssertions = hasAssertions ? assertionResults.EnumerateArray() : default;
            foreach (JsonElement assertion in fileAssertions)
            {
                if (assertion.ValueKind != JsonValueKind.Object)
                    throw new JsonException("Vitest assertion results must be objects.");
                string status = GetString(assertion, "status") ?? "unknown";
                JsonElement location = assertion.TryGetProperty("location", out JsonElement value) ? value : default;
                int? line = GetInt(location, "line");
                int? column = GetInt(location, "column");
                string name = GetString(assertion, "fullName") ?? GetString(assertion, "title") ?? "Test";
                string? message = GetFailureMessage(assertion);
                if (status == "failed") diagnostics.Add(new ScriptTestDiagnostic(filePath, line, column, message ?? name));
                assertions.Add(new ScriptTestAssertionResult(name, status, filePath, line, column, message));
            }
            if (GetString(file, "status") == "failed" && (!hasAssertions || assertionResults.GetArrayLength() == 0))
                diagnostics.Add(new ScriptTestDiagnostic(filePath, null, null, GetString(file, "message") ?? "Test file failed to load."));
        }

        int passed = Count(root, "numPassedTests");
        int failed = Count(root, "numFailedTests");
        int skipped = Count(root, "numPendingTests") + Count(root, "numTodoTests");
        if (failed == 0 && exitCode != 0 && diagnostics.Count == 0)
            diagnostics.Add(new ScriptTestDiagnostic("", null, null, string.IsNullOrWhiteSpace(output) ? "Vitest failed." : output.Trim()));
        return new ScriptTestRunResult(exitCode == 0 && failed == 0, exitCode, passed, failed, skipped,
            assertions, diagnostics, output);
    }

    private static string ToPackagePath(string sandbox, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        string fullPath = Path.GetFullPath(path, sandbox);
        string relative = Path.GetRelativePath(sandbox, fullPath).Replace('\\', '/');
        return relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) ? "" : relative;
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out JsonElement value)) return null;
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        if (value.ValueKind == JsonValueKind.Array) return string.Join(Environment.NewLine, value.EnumerateArray().Select(item => item.GetString()));
        return null;
    }

    private static string? GetFailureMessage(JsonElement assertion)
    {
        if (!assertion.TryGetProperty("failureMessages", out JsonElement failures)) return null;
        if (failures.ValueKind == JsonValueKind.String) return failures.GetString();
        return failures.ValueKind == JsonValueKind.Array
            ? string.Join(Environment.NewLine, failures.EnumerateArray().Select(item => item.GetString()).Where(message => !string.IsNullOrWhiteSpace(message)))
            : null;
    }

    private static int? GetInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result)
            ? result : null;

    private static int Count(JsonElement element, string name) => GetInt(element, name) ?? 0;

    private static ScriptTestRunResult Failure(string message) => new(false, null, 0, 0, 0, [],
        [new ScriptTestDiagnostic("", null, null, message)], "");

    private const string ApiShim = """
const calls = [];
const listeners = new Map();
const storage = new Map();
const empty = { current: null, maximum: null, percent: null };
const snapshot = { version: 0, connected: false, inputMode: 'normal', character: { health: empty, mana: empty, movement: empty, position: null }, room: { id: null, name: null, exits: [] }, combat: { active: false, target: null } };
export const __nexmudTest = { calls, listeners, storage, snapshot, setSnapshot(value) { Object.assign(snapshot, structuredClone(value)); }, emit(type, event) { for (const handler of listeners.get(type) ?? []) void handler(structuredClone(event)); } };
export const nex = {
  events: { on(type, handler) { const set = listeners.get(type) ?? new Set(); set.add(handler); listeners.set(type, set); const subscription = { type, handler, dispose() { listeners.get(type)?.delete(handler); }, cancel() { this.dispose(); } }; return subscription; }, off(subscription) { subscription.dispose(); } },
  commands: { async send(command, options) { calls.push({ kind: 'command', command, options }); return { accepted: true, commandId: `test-${calls.length}`, reason: null }; } },
  state: { get character() { return snapshot.character; }, get room() { return snapshot.room; }, get combat() { return snapshot.combat; }, get connected() { return false; }, get inputMode() { return 'normal'; }, snapshot() { return structuredClone(snapshot); } },
  log: Object.fromEntries(['trace','debug','info','warn','error'].map(level => [level, async (message, data) => calls.push({ kind: 'log', level, message, data })])),
  timers: { async delay() {}, after(_ms, callback) { let active = true; queueMicrotask(() => { if (active) void callback(); }); return { cancel() { active = false; } }; }, every() { return { cancel() {} }; }, cancel(handle) { handle.cancel(); } },
  storage: { async get(key) { return storage.has(key) ? structuredClone(storage.get(key)) : null; }, async set(key, value) { storage.set(key, structuredClone(value)); }, async delete(key) { storage.delete(key); }, async has(key) { return storage.has(key); } },
  mapper: { async currentRoom() { return null; }, async findPath() { return null; }, async move(direction, options) { calls.push({ kind: 'mapper.move', direction, options }); return { kind: 'cancelled', fromRoomId: null, toRoomId: null, expectedRoomId: null, actualRoomId: null, reason: null, message: 'Unavailable in test shim.', recoveryCommand: null, replanSuggested: false }; } },
  codex: { async search() { return []; } }
};
""";
}
