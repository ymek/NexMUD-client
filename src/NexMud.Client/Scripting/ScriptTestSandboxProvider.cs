using System.Text;
using NexMud.Scripting.Tooling;

namespace NexMud.Client.Scripting;

public interface IScriptTestSandboxProvider
{
    bool TryCreateRequest(
        ToolProcessRequest request,
        IReadOnlyList<string> allowedReadRoots,
        string writeRoot,
        out ToolProcessRequest sandboxedRequest,
        out string error);
}

/// <summary>Applies the required macOS OS sandbox around the Node test process.</summary>
public sealed class MacOsScriptTestSandboxProvider : IScriptTestSandboxProvider
{
    private const string SandboxExecPath = "/usr/bin/sandbox-exec";

    public bool TryCreateRequest(
        ToolProcessRequest request,
        IReadOnlyList<string> allowedReadRoots,
        string writeRoot,
        out ToolProcessRequest sandboxedRequest,
        out string error)
    {
        sandboxedRequest = request;
        if (!OperatingSystem.IsMacOS())
        {
            error = "OS-isolated script tests are currently supported only on macOS.";
            return false;
        }
        if (!File.Exists(SandboxExecPath))
        {
            error = "Required macOS sandbox executable '/usr/bin/sandbox-exec' is unavailable.";
            return false;
        }

        try
        {
            string node = ResolvePhysicalPath(request.Executable);
            string sandbox = ResolvePhysicalPath(writeRoot);
            string profilePath = Path.Combine(sandbox, ".nexmud-test-sandbox.sb");
            string[] roots = allowedReadRoots.Select(ResolvePhysicalPath)
                .Distinct(StringComparer.Ordinal).ToArray();
            File.WriteAllText(profilePath, CreateProfile(node, roots, sandbox), new UTF8Encoding(false));
            List<string> arguments = ["-f", profilePath, node, .. request.Arguments];
            sandboxedRequest = request with { Executable = SandboxExecPath, Arguments = arguments };
            error = "";
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = "Unable to prepare the required macOS test sandbox: " + exception.Message;
            return false;
        }
    }

    internal static string CreateProfile(string nodeExecutable, IReadOnlyList<string> readRoots, string writeRoot)
    {
        string node = CanonicalExistingPath(nodeExecutable);
        string write = CanonicalExistingPath(writeRoot);
        string[] reads = readRoots.Select(CanonicalExistingPath).Distinct(StringComparer.Ordinal).ToArray();
        if (!Directory.Exists(write)) throw new IOException("The test sandbox write root must be an existing directory.");
        if (reads.Any(root => !Directory.Exists(root))) throw new IOException("Every allowed read root must be an existing directory.");

        StringBuilder profile = new();
        profile.AppendLine("(version 1)");
        profile.AppendLine("(deny default)");
        profile.AppendLine("(import \"system.sb\")");
        profile.AppendLine("(allow process-exec (literal \"" + Quote(node) + "\"))");
        profile.AppendLine("(allow process-fork)");
        profile.AppendLine("(allow signal (target self))");
        profile.AppendLine("(allow sysctl-read)");
        profile.AppendLine("(deny mach-lookup)");
        foreach (string root in reads)
            profile.AppendLine("(allow file-read* (subpath \"" + Quote(root) + "\"))");
        foreach (string ancestor in GetTraversalAncestors([node, .. reads]))
            profile.AppendLine("(allow file-read-metadata (literal \"" + Quote(ancestor) + "\"))");
        profile.AppendLine("(allow file-write* (subpath \"" + Quote(write) + "\"))");
        profile.AppendLine("(deny network*)");
        return profile.ToString();
    }

    private static string CanonicalExistingPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        RejectControlCharacters(path);
        string fullPath = ResolvePhysicalPath(path);
        if (!Directory.Exists(fullPath) && !File.Exists(fullPath))
            throw new IOException($"Sandbox path '{path}' does not exist.");
        return fullPath;
    }

    private static string ResolvePhysicalPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        RejectControlCharacters(path);
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath) ?? throw new IOException("Sandbox path has no filesystem root.");
        string current = root;
        foreach (string segment in fullPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            string next = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            current = (info.ResolveLinkTarget(returnFinalTarget: true) ?? info).FullName;
        }
        return Path.GetFullPath(current);
    }

    private static IEnumerable<string> GetTraversalAncestors(IEnumerable<string> paths)
    {
        HashSet<string> ancestors = new(StringComparer.Ordinal);
        foreach (string path in paths)
        {
            string? current = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            while (!string.IsNullOrEmpty(current))
            {
                string? parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || parent == current) break;
                ancestors.Add(parent);
                current = parent;
            }
        }
        return ancestors;
    }

    private static string Quote(string value)
    {
        RejectControlCharacters(value);
        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private static void RejectControlCharacters(string value)
    {
        if (value.IndexOfAny(['\0', '\r', '\n']) >= 0)
            throw new ArgumentException("Sandbox paths cannot contain NUL or newline characters.");
    }
}
