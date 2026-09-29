using System.Text.Json;

namespace JevMud.Client.Automation;

/// <summary>
/// Durable state for first-party automation variables. This is intentionally separate from
/// ClientSettings: variables are mutable runtime state, not configuration.
/// </summary>
public sealed class AutomationStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public AutomationStateStore(string? path = null)
    {
        Path = path ?? GetDefaultPath();
    }

    public string Path { get; }

    public async Task<Dictionary<string, string>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(Path))
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            await using FileStream stream = File.OpenRead(Path);
            Dictionary<string, string>? values = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(
                stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            return new Dictionary<string, string>(values ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(IReadOnlyDictionary<string, string> variables, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(variables);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            string temporary = Path + ".tmp";
            await using (FileStream stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, variables, JsonOptions, cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, Path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static string GetDefaultPath() =>
        JevMud.Client.Paths.NexMudDataPaths.GetFilePath("automation-state.json");
}
