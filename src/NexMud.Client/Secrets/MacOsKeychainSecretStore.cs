using System.Diagnostics;

namespace NexMud.Client.Secrets;

public sealed class MacOsKeychainSecretStore : ISecretStore
{
    private const string SecurityTool = "/usr/bin/security";
    private const string Service = "NexMUD";
    private const string LegacyService = "NexMUD";
    private readonly string _account;

    public MacOsKeychainSecretStore(string? account = null)
    {
        _account = string.IsNullOrWhiteSpace(account) ? Environment.UserName : account.Trim();
    }

    public bool IsAvailable => OperatingSystem.IsMacOS() && File.Exists(SecurityTool);

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        if (!IsAvailable)
        {
            return null;
        }

        ProcessResult result = await RunAsync(
            ["find-generic-password", "-a", _account, "-s", ServiceName(Service, key), "-w"],
            null,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == 0) return result.StandardOutput.TrimEnd('\r', '\n');

        ProcessResult legacy = await RunAsync(
            ["find-generic-password", "-a", _account, "-s", ServiceName(LegacyService, key), "-w"],
            null,
            cancellationToken).ConfigureAwait(false);
        return legacy.ExitCode == 0 ? legacy.StandardOutput.TrimEnd('\r', '\n') : null;
    }

    public async Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Secret value is required.", nameof(value));
        }
        EnsureAvailable();

        // Putting -w last asks the security tool to read the password interactively.
        // Redirecting stdin avoids exposing the secret in the process argument list.
        ProcessResult result = await RunAsync(
            ["add-generic-password", "-a", _account, "-s", ServiceName(Service, key), "-U", "-w"],
            value + Environment.NewLine,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"macOS Keychain rejected the secret: {result.StandardError.Trim()}");
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        if (!IsAvailable)
        {
            return;
        }

        await RunAsync(
            ["delete-generic-password", "-a", _account, "-s", ServiceName(Service, key)],
            null,
            cancellationToken).ConfigureAwait(false);
        await RunAsync(
            ["delete-generic-password", "-a", _account, "-s", ServiceName(LegacyService, key)],
            null,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        string? standardInput,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo start = new(SecurityTool)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = start };
        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start the macOS security tool.");
        }

        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private void EnsureAvailable()
    {
        if (!IsAvailable)
        {
            throw new PlatformNotSupportedException("macOS Keychain is not available on this system.");
        }
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Secret key is required.", nameof(key));
        }
    }

    private static string ServiceName(string service, string key) => $"{service}.{key.Trim()}";

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
