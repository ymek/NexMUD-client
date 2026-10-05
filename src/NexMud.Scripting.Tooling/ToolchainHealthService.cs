using System.Security.Cryptography;

namespace NexMud.Scripting.Tooling;

public sealed record ToolchainComponentHealth(
    string Name,
    string Version,
    string Path,
    bool Healthy,
    string? Message = null);

public sealed record ToolchainHealthReport(
    bool Healthy,
    IReadOnlyList<ToolchainComponentHealth> Components,
    string? Message = null);

public sealed class ToolchainHealthService
{
    private readonly IToolchainLocator _locator;

    public ToolchainHealthService(IToolchainLocator locator)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
    }

    public async Task<ToolchainHealthReport> CheckAsync(CancellationToken cancellationToken = default)
    {
        ToolchainManifest manifest;
        try
        {
            manifest = _locator.Manifest;
        }
        catch (Exception exception) when (exception is ToolchainUnavailableException or IOException or UnauthorizedAccessException)
        {
            return new ToolchainHealthReport(false, [], exception.Message);
        }

        List<ToolchainComponentHealth> components = [];
        foreach (ToolchainComponentManifest component in manifest.Components)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                ToolchainComponentLocation location = _locator.ResolveRequired(component.Name);
                if (location.Executable && !IsExecutable(location.FullPath))
                {
                    components.Add(new ToolchainComponentHealth(
                        location.Name,
                        location.Version,
                        location.FullPath,
                        false,
                        "Component is not executable."));
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(location.Sha256))
                {
                    string actual = await ComputeSha256Async(location.FullPath, cancellationToken).ConfigureAwait(false);
                    if (!actual.Equals(location.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        components.Add(new ToolchainComponentHealth(
                            location.Name,
                            location.Version,
                            location.FullPath,
                            false,
                            $"SHA-256 mismatch. Expected {location.Sha256}, got {actual}."));
                        continue;
                    }
                }

                components.Add(new ToolchainComponentHealth(
                    location.Name,
                    location.Version,
                    location.FullPath,
                    true));
            }
            catch (Exception exception) when (exception is ToolchainUnavailableException or IOException or UnauthorizedAccessException)
            {
                components.Add(new ToolchainComponentHealth(
                    component.Name,
                    component.Version,
                    component.RelativePath,
                    false,
                    exception.Message));
            }
        }

        return new ToolchainHealthReport(
            components.Count > 0 && components.All(component => component.Healthy),
            components);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        using SHA256 sha256 = SHA256.Create();
        byte[] hash = await sha256.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool IsExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return true;
        try
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            UnixFileMode execute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return (mode & execute) != 0;
        }
        catch (PlatformNotSupportedException)
        {
            return true;
        }
    }
}
