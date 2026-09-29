namespace NexMud.Client.Paths;

/// <summary>
/// Product data-path boundary. NexMUD uses the new product directory while performing a one-time,
/// best-effort copy from the legacy NexMUD directory so the rename does not discard user state.
/// </summary>
public static class NexMudDataPaths
{
    public const string ProductDirectoryName = "NexMUD";
    public const string LegacyProductDirectoryName = "NexMUD";

    public static string GetFilePath(string fileName, bool includeSqliteSidecars = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string productDirectory = Path.Combine(root, ProductDirectoryName);
        string target = Path.Combine(productDirectory, fileName);
        if (File.Exists(target)) return target;

        string legacy = Path.Combine(root, LegacyProductDirectoryName, fileName);
        if (!File.Exists(legacy)) return target;

        try
        {
            Directory.CreateDirectory(productDirectory);
            File.Copy(legacy, target, overwrite: false);
            if (includeSqliteSidecars)
            {
                CopyIfPresent(legacy + "-wal", target + "-wal");
                CopyIfPresent(legacy + "-shm", target + "-shm");
            }
        }
        catch (IOException)
        {
            // Another startup may have completed migration concurrently. Prefer the NexMUD path.
        }
        catch (UnauthorizedAccessException)
        {
            // Storage callers will surface the normal access failure if the target cannot be used.
        }
        return target;
    }

    private static void CopyIfPresent(string source, string destination)
    {
        if (File.Exists(source) && !File.Exists(destination)) File.Copy(source, destination, overwrite: false);
    }
}
