using System.Diagnostics;

namespace NexMud.Client.Scripting;

public sealed record ScriptWorkspaceEntry(
    string RelativePath,
    bool IsDirectory,
    long Size,
    DateTimeOffset LastWriteTimeUtc);

public sealed partial class ScriptWorkspaceService
{
    public ScriptWorkspaceWatcher WatchProfile(string profileId)
    {
        profileId = ScriptWorkspacePath.NormalizeIdentifier(profileId, nameof(profileId));
        return new ScriptWorkspaceWatcher(
            profileId,
            GetScriptsRoot(profileId),
            directoryName => ResolvePackageIdFromDirectory(profileId, directoryName));
    }

    public Task<IReadOnlyList<ScriptWorkspaceEntry>> ListEntriesAsync(
        string profileId,
        string packageId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string packageRoot = GetPackageRoot(profileId, packageId);
        if (!Directory.Exists(packageRoot))
            return Task.FromResult<IReadOnlyList<ScriptWorkspaceEntry>>([]);

        List<ScriptWorkspaceEntry> entries = [];
        foreach (string directory in Directory.EnumerateDirectories(packageRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(packageRoot, directory).Replace('\\', '/');
            if (IsHiddenAuthoringPath(relative)) continue;
            DirectoryInfo info = new(directory);
            entries.Add(new ScriptWorkspaceEntry(relative, true, 0, info.LastWriteTimeUtc));
        }

        foreach (string file in Directory.EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(packageRoot, file).Replace('\\', '/');
            if (IsHiddenAuthoringPath(relative) ||
                Path.GetFileName(file).Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase) ||
                !IsSupportedSourcePath(relative))
                continue;
            FileInfo info = new(file);
            entries.Add(new ScriptWorkspaceEntry(relative, false, info.Length, info.LastWriteTimeUtc));
        }

        return Task.FromResult<IReadOnlyList<ScriptWorkspaceEntry>>(entries
            .OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    public Task RevealInFileManagerAsync(
        string profileId,
        string packageId,
        string? relativePath = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string packageRoot = GetPackageRoot(profileId, packageId);
        string target = string.IsNullOrWhiteSpace(relativePath)
            ? packageRoot
            : ScriptWorkspacePath.CombineInside(packageRoot, relativePath);
        if (!File.Exists(target) && !Directory.Exists(target))
            throw new FileNotFoundException("Script workspace path was not found.", relativePath ?? packageId);

        ProcessStartInfo start = BuildRevealStartInfo(target);
        _ = Process.Start(start) ?? throw new InvalidOperationException("Unable to open the platform file manager.");
        return Task.CompletedTask;
    }

    private static bool IsHiddenAuthoringPath(string relativePath) =>
        relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                            segment.Equals(".nexmud", StringComparison.OrdinalIgnoreCase));

    private static ProcessStartInfo BuildRevealStartInfo(string target)
    {
        if (OperatingSystem.IsMacOS())
        {
            ProcessStartInfo start = new("open") { UseShellExecute = false };
            if (File.Exists(target)) start.ArgumentList.Add("-R");
            start.ArgumentList.Add(target);
            return start;
        }

        if (OperatingSystem.IsWindows())
        {
            ProcessStartInfo start = new("explorer.exe") { UseShellExecute = false };
            start.ArgumentList.Add(File.Exists(target) ? $"/select,{target}" : target);
            return start;
        }

        string directory = Directory.Exists(target) ? target : Path.GetDirectoryName(target) ?? target;
        ProcessStartInfo linux = new("xdg-open") { UseShellExecute = false };
        linux.ArgumentList.Add(directory);
        return linux;
    }
}
