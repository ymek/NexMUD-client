using System.Diagnostics;
using System.Text;

namespace NexMud.Scripting.Tooling;

public sealed record ToolProcessRequest(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyList<string>? PathEntries = null,
    IReadOnlyDictionary<string, string?>? Environment = null,
    int? OutputCharacterLimit = null);

public sealed record ToolProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool OutputTruncated = false)
{
    public string Output => string.Concat(StandardOutput, StandardError);
}

public interface IToolProcessRunner
{
    Task<ToolProcessResult> RunAsync(ToolProcessRequest request, CancellationToken cancellationToken = default);
}

public sealed class ToolProcessRunner : IToolProcessRunner
{
    public async Task<ToolProcessResult> RunAsync(
        ToolProcessRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.OutputCharacterLimit < 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Output character limit cannot be negative.");
        ProcessStartInfo startInfo = ToolProcessStartInfoFactory.Create(request);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Unable to start tooling process '{request.Executable}'.");
        OutputCapture capture = new(request.OutputCharacterLimit);
        Task stdout = capture.DrainAsync(process.StandardOutput, isError: false);
        Task stderr = capture.DrainAsync(process.StandardError, isError: true);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            throw;
        }

        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        return new ToolProcessResult(process.ExitCode, capture.StandardOutput, capture.StandardError, capture.Truncated);
    }

    private sealed class OutputCapture(int? limit)
    {
        private readonly object _gate = new();
        private readonly StringBuilder _standardOutput = new();
        private readonly StringBuilder _standardError = new();
        private int _retained;
        private bool _truncated;

        public string StandardOutput { get { lock (_gate) return _standardOutput.ToString(); } }
        public string StandardError { get { lock (_gate) return _standardError.ToString(); } }
        public bool Truncated { get { lock (_gate) return _truncated; } }

        public async Task DrainAsync(StreamReader reader, bool isError)
        {
            char[] buffer = new char[8192];
            while (true)
            {
                int count = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false);
                if (count == 0) return;
                lock (_gate)
                {
                    int retained = limit is null ? count : Math.Min(count, Math.Max(0, limit.Value - _retained));
                    if (retained > 0)
                    {
                        (isError ? _standardError : _standardOutput).Append(buffer, 0, retained);
                        _retained += retained;
                    }
                    if (retained < count) _truncated = true;
                }
            }
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Cancellation remains authoritative even if the process exited concurrently.
        }
    }
}

public static class ToolProcessStartInfoFactory
{
    private static readonly string[] PreservedEnvironmentVariables = OperatingSystem.IsWindows()
        ? ["HOME", "USERPROFILE", "TEMP", "TMP", "SystemRoot", "WINDIR", "LANG", "LC_ALL"]
        : ["HOME", "TMPDIR", "TMP", "TEMP", "LANG", "LC_ALL"];

    public static ProcessStartInfo Create(ToolProcessRequest request, bool redirectStandardInput = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkingDirectory);

        ProcessStartInfo startInfo = new()
        {
            FileName = request.Executable,
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardInput = redirectStandardInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in request.Arguments) startInfo.ArgumentList.Add(argument);
        SanitizeEnvironment(startInfo, request);
        return startInfo;
    }

    private static void SanitizeEnvironment(ProcessStartInfo startInfo, ToolProcessRequest request)
    {
        Dictionary<string, string?> inherited = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in PreservedEnvironmentVariables)
            inherited[name] = Environment.GetEnvironmentVariable(name);

        startInfo.Environment.Clear();
        foreach ((string name, string? value) in inherited)
        {
            if (!string.IsNullOrWhiteSpace(value)) startInfo.Environment[name] = value;
        }

        string[] pathEntries = (request.PathEntries ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();
        startInfo.Environment["PATH"] = string.Join(Path.PathSeparator, pathEntries);

        startInfo.Environment["NO_UPDATE_NOTIFIER"] = "1";
        startInfo.Environment["NPM_CONFIG_UPDATE_NOTIFIER"] = "false";
        startInfo.Environment["NPM_CONFIG_AUDIT"] = "false";
        startInfo.Environment["NPM_CONFIG_FUND"] = "false";
        startInfo.Environment["COREPACK_ENABLE_DOWNLOAD_PROMPT"] = "0";

        if (request.Environment is null) return;
        foreach ((string name, string? value) in request.Environment)
        {
            if (string.Equals(name, "PATH", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Use PathEntries instead of overriding PATH directly.", nameof(request));
            if (string.IsNullOrWhiteSpace(name) || name.Contains('='))
                throw new ArgumentException($"Invalid tooling environment variable name '{name}'.", nameof(request));
            if (value is null) startInfo.Environment.Remove(name);
            else startInfo.Environment[name] = value;
        }
    }
}
