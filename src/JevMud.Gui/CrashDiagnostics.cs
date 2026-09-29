namespace JevMud.Gui;

internal static class CrashDiagnostics
{
    private static readonly object Gate = new();

    public static string LogPath { get; } = BuildLogPath();

    public static void Record(string context, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        ArgumentNullException.ThrowIfNull(exception);
        string entry = $"[{DateTimeOffset.Now:O}] {context}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}";
        try
        {
            lock (Gate)
            {
                string? directory = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                File.AppendAllText(LogPath, entry);
            }
        }
        catch
        {
            // Diagnostics must never cause a second failure while handling the first one.
        }

        try
        {
            Console.Error.Write(entry);
        }
        catch
        {
        }
    }

    private static string BuildLogPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = AppContext.BaseDirectory;
        return Path.Combine(root, "NexMUD", "logs", "unhandled-exceptions.log");
    }
}
