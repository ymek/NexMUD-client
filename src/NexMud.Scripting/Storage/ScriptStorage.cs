using NexMud.Scripting.Runtime;

namespace NexMud.Scripting.Storage;

public sealed record ScriptStorageEntry(string Key, string JsonValue, DateTimeOffset UpdatedAt);

public interface IScriptStorage
{
    ScriptModuleId ModuleId { get; }
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task SetAsync(string key, string jsonValue, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScriptStorageEntry>> ListAsync(string? prefix = null, CancellationToken cancellationToken = default);
}
