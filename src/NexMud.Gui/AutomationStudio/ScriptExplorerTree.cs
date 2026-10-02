using NexMud.Client.Scripting;

namespace NexMud.Gui.AutomationStudio;

internal enum ScriptExplorerEntryKind
{
    Folder,
    File
}

internal sealed record ScriptExplorerEntry(
    ScriptExplorerEntryKind Kind,
    string Name,
    string RelativePath,
    IReadOnlyList<ScriptExplorerEntry> Children)
{
    public bool IsFolder => Kind == ScriptExplorerEntryKind.Folder;
}

/// <summary>
/// Pure package-relative projection used by the Scripts explorer. Files remain the workspace
/// source of truth; directory nodes are reconstructed from canonical relative source paths.
/// </summary>
internal static class ScriptExplorerTree
{
    private sealed class MutableNode(string name, string relativePath, ScriptExplorerEntryKind kind)
    {
        public string Name { get; } = name;
        public string RelativePath { get; } = relativePath;
        public ScriptExplorerEntryKind Kind { get; } = kind;
        public Dictionary<string, MutableNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<ScriptExplorerEntry> Build(
        IEnumerable<ScriptWorkspaceSourceFile> files,
        string? filter = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        return Build(
            files.Select(file => new ScriptWorkspaceEntry(
                file.RelativePath,
                IsDirectory: false,
                file.Size,
                DateTimeOffset.MinValue)),
            filter);
    }

    public static IReadOnlyList<ScriptExplorerEntry> Build(
        IEnumerable<ScriptWorkspaceEntry> entries,
        string? filter = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        MutableNode root = new(string.Empty, string.Empty, ScriptExplorerEntryKind.Folder);
        foreach (ScriptWorkspaceEntry entry in entries)
        {
            string relativePath = Normalize(entry.RelativePath);
            string[] parts = relativePath.Split('/');
            MutableNode parent = root;
            string current = string.Empty;
            for (int index = 0; index < parts.Length; index++)
            {
                current = Combine(current, parts[index]);
                bool leaf = index == parts.Length - 1;
                ScriptExplorerEntryKind kind = leaf && !entry.IsDirectory
                    ? ScriptExplorerEntryKind.File
                    : ScriptExplorerEntryKind.Folder;
                if (!parent.Children.TryGetValue(parts[index], out MutableNode? child))
                {
                    child = new MutableNode(parts[index], current, kind);
                    parent.Children[parts[index]] = child;
                }
                parent = child;
            }
        }

        return FreezeChildren(root, filter);
    }

    public static string ParentPath(string relativePath)
    {
        string normalized = Normalize(relativePath);
        int slash = normalized.LastIndexOf('/');
        return slash < 0 ? string.Empty : normalized[..slash];
    }

    public static string Combine(string? parentPath, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string normalizedName = Normalize(name);
        if (normalizedName.Contains('/'))
            throw new ArgumentException("A child name must not contain path separators.", nameof(name));
        return string.IsNullOrWhiteSpace(parentPath)
            ? normalizedName
            : $"{Normalize(parentPath)}/{normalizedName}";
    }

    public static bool IsSameOrDescendant(string candidatePath, string ancestorPath)
    {
        string candidate = Normalize(candidatePath);
        string ancestor = Normalize(ancestorPath);
        return candidate.Equals(ancestor, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(ancestor + "/", StringComparison.OrdinalIgnoreCase);
    }

    public static string Rebase(string path, string sourceRoot, string destinationRoot)
    {
        string normalizedPath = Normalize(path);
        string source = Normalize(sourceRoot);
        if (!IsSameOrDescendant(normalizedPath, source))
            throw new ArgumentException("Path is not inside the source root.", nameof(path));
        string suffix = normalizedPath.Length == source.Length
            ? string.Empty
            : normalizedPath[(source.Length + 1)..];
        string destination = Normalize(destinationRoot);
        return string.IsNullOrEmpty(suffix) ? destination : $"{destination}/{suffix}";
    }

    private static IReadOnlyList<ScriptExplorerEntry> FreezeChildren(MutableNode parent, string? filter) =>
        parent.Children.Values
            .OrderBy(node => node.Kind == ScriptExplorerEntryKind.File)
            .ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
            .Select(node => Freeze(node, filter))
            .Where(node => node is not null)
            .Cast<ScriptExplorerEntry>()
            .ToArray();

    private static ScriptExplorerEntry? Freeze(MutableNode node, string? filter)
    {
        bool matches = string.IsNullOrWhiteSpace(filter) ||
                       node.Name.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase) ||
                       node.RelativePath.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase);
        string? childFilter = matches ? null : filter;
        IReadOnlyList<ScriptExplorerEntry> children = node.Kind == ScriptExplorerEntryKind.Folder
            ? FreezeChildren(node, childFilter)
            : [];
        if (!matches && children.Count == 0) return null;
        return new ScriptExplorerEntry(node.Kind, node.Name, node.RelativePath, children);
    }

    private static string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string normalized = value.Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new ArgumentException("Script explorer paths must be package-relative.", nameof(value));
        return normalized;
    }
}
