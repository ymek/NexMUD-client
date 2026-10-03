using System.Diagnostics;

namespace NexMud.Scripting.Tooling;

public sealed record ToolProcessRequest(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyList<string>? PathEntries = null,
    IReadOnlyDictionary<string, string?>? Environment = null);

public sealed record ToolProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError)
{
    public string Output => string.Concat(StandardOutput, StandardError);
}

public interface IToolProcessRunner
{
    Task<ToolProcessResult> RunAsync(ToolProcessRequest request, CancellationToken cancellationToken = default);
}

public sealed class ToolProcessRunner : IToolProcessRunner
{
    private static readonly string[] PreservedEnvironmentVariables = OperatingSystem.IsWindows()
        ? ["HOME", "USERPROFILE", "TEMP", "TMP", "SystemRoot", "WINDIR", "LANG", "LC_ALL"]
        : ["HOME", "TMPDIR", "TMP", "TEMP", "LANG", "LC_ALL"];

    public async Task<ToolProcessResult> RunAsync(
        ToolProcessRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkingDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        ProcessStartInfo startInfo = new()
        {
            FileName = request.Executable,
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in request.Arguments) startInfo.ArgumentList.Add(argument);
        SanitizeEnvironment(startInfo, request);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Unable to start tooling process '{request.Executable}'.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();

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

        return new ToolProcessResult(
            process.ExitCode,
            await stdout.ConfigureAwait(false),
            await stderr.ConfigureAwait(false));
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
