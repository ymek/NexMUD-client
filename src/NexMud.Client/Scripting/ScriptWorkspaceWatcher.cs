namespace NexMud.Client.Scripting;

public enum ScriptWorkspaceExternalChangeKind
{
    Created,
    Changed,
    Deleted,
    Renamed
}

public sealed record ScriptWorkspaceExternalChange(
    string ProfileId,
    string PackageId,
    string RelativePath,
    ScriptWorkspaceExternalChangeKind Kind,
    string? PreviousRelativePath = null,
    bool IsDirectory = false);

public sealed class ScriptWorkspaceExternalChangesEventArgs(
    string profileId,
    IReadOnlyList<ScriptWorkspaceExternalChange> changes) : EventArgs
{
    public string ProfileId { get; } = profileId;
    public IReadOnlyList<ScriptWorkspaceExternalChange> Changes { get; } = changes;
}

/// <summary>
/// Profile-scoped, debounced filesystem watcher for the real script workspace. The watcher only
/// reports package-relative authoring paths. It never reads or writes source content itself.
/// </summary>
public sealed class ScriptWorkspaceWatcher : IDisposable
{
    private const int DebounceMilliseconds = 180;

    private readonly string _profileId;
    private readonly string _root;
    private readonly Func<string, string> _packageIdResolver;
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private readonly Dictionary<string, ScriptWorkspaceExternalChange> _pending = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    internal ScriptWorkspaceWatcher(
        string profileId,
        string root,
        Func<string, string>? packageIdResolver = null)
    {
        _profileId = profileId;
        _root = Path.GetFullPath(root);
        _packageIdResolver = packageIdResolver ?? (directoryName => directoryName);
        Directory.CreateDirectory(_root);

        _watcher = new FileSystemWatcher(_root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName |
                           NotifyFilters.DirectoryName |
                           NotifyFilters.LastWrite |
                           NotifyFilters.Size |
                           NotifyFilters.CreationTime,
            EnableRaisingEvents = false
        };
        _watcher.Created += (_, args) => Queue(args.FullPath, ScriptWorkspaceExternalChangeKind.Created);
        _watcher.Changed += (_, args) => Queue(args.FullPath, ScriptWorkspaceExternalChangeKind.Changed);
        _watcher.Deleted += (_, args) => Queue(args.FullPath, ScriptWorkspaceExternalChangeKind.Deleted);
        _watcher.Renamed += (_, args) => QueueRename(args.OldFullPath, args.FullPath);
        _timer = new Timer(Flush, null, Timeout.Infinite, Timeout.Infinite);
        _watcher.EnableRaisingEvents = true;
    }

    public event EventHandler<ScriptWorkspaceExternalChangesEventArgs>? Changed;

    private void Queue(string fullPath, ScriptWorkspaceExternalChangeKind kind)
    {
        if (!TryMap(fullPath, out string packageId, out string relativePath)) return;
        bool isDirectory = Directory.Exists(fullPath);
        if (!IsAuthoringRelevant(relativePath, isDirectory)) return;
        Enqueue(new ScriptWorkspaceExternalChange(
            _profileId,
            packageId,
            relativePath,
            kind,
            IsDirectory: isDirectory));
    }

    private void QueueRename(string oldFullPath, string newFullPath)
    {
        bool oldMapped = TryMap(oldFullPath, out string oldPackage, out string oldRelative);
        bool newMapped = TryMap(newFullPath, out string newPackage, out string newRelative);
        bool isDirectory = Directory.Exists(newFullPath);

        if (oldMapped && newMapped && oldPackage.Equals(newPackage, StringComparison.OrdinalIgnoreCase))
        {
            if (!IsAuthoringRelevant(oldRelative, isDirectory) && !IsAuthoringRelevant(newRelative, isDirectory)) return;
            Enqueue(new ScriptWorkspaceExternalChange(
                _profileId,
                newPackage,
                newRelative,
                ScriptWorkspaceExternalChangeKind.Renamed,
                oldRelative,
                isDirectory));
            return;
        }

        if (oldMapped && IsAuthoringRelevant(oldRelative, isDirectory))
            Enqueue(new ScriptWorkspaceExternalChange(
                _profileId,
                oldPackage,
                oldRelative,
                ScriptWorkspaceExternalChangeKind.Deleted,
                IsDirectory: isDirectory));
        if (newMapped && IsAuthoringRelevant(newRelative, isDirectory))
            Enqueue(new ScriptWorkspaceExternalChange(
                _profileId,
                newPackage,
                newRelative,
                ScriptWorkspaceExternalChangeKind.Created,
                IsDirectory: isDirectory));
    }

    private void Enqueue(ScriptWorkspaceExternalChange change)
    {
        lock (_gate)
        {
            if (_disposed) return;
            string key = $"{change.Kind}\n{change.PackageId}\n{change.PreviousRelativePath}\n{change.RelativePath}";
            _pending[key] = change;
            _timer.Change(DebounceMilliseconds, Timeout.Infinite);
        }
    }

    private void Flush(object? state)
    {
        ScriptWorkspaceExternalChange[] changes;
        lock (_gate)
        {
            if (_disposed || _pending.Count == 0) return;
            changes = _pending.Values
                .OrderBy(change => change.PackageId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(change => change.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(change => change.Kind)
                .ToArray();
            _pending.Clear();
        }
        Changed?.Invoke(this, new ScriptWorkspaceExternalChangesEventArgs(_profileId, changes));
    }

    private bool TryMap(string fullPath, out string packageId, out string relativePath)
    {
        packageId = string.Empty;
        relativePath = string.Empty;
        string canonical = Path.GetFullPath(fullPath);
        string relative = Path.GetRelativePath(_root, canonical).Replace('\\', '/');
        if (relative is "." or ".." || relative.StartsWith("../", StringComparison.Ordinal)) return false;
        int slash = relative.IndexOf('/');
        string directoryName = slash < 0 ? relative : relative[..slash];
        if (string.IsNullOrWhiteSpace(directoryName) ||
            directoryName.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
            directoryName.Equals(ScriptPackageWorkspaceMigrator.NexMudDirectoryName, StringComparison.OrdinalIgnoreCase))
            return false;
        try { packageId = _packageIdResolver(directoryName); }
        catch { packageId = directoryName; }
        relativePath = slash < 0 ? string.Empty : relative[(slash + 1)..];
        return !string.IsNullOrWhiteSpace(packageId) && !IsHidden(relativePath);
    }

    private static bool IsAuthoringRelevant(string relativePath, bool isDirectory)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || isDirectory) return true;
        if (Path.GetFileName(relativePath).Equals("manifest.json", StringComparison.OrdinalIgnoreCase)) return true;
        if (ScriptWorkspaceService.IsSupportedSourcePath(relativePath)) return true;
        return string.IsNullOrEmpty(Path.GetExtension(relativePath));
    }

    private static bool IsHidden(string relativePath) =>
        relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                            segment.Equals(".nexmud", StringComparison.OrdinalIgnoreCase));

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pending.Clear();
            _watcher.EnableRaisingEvents = false;
        }
        _watcher.Dispose();
        _timer.Dispose();
    }
}
