namespace JevMud.Client.Secrets;

public interface ISecretStore
{
    bool IsAvailable { get; }

    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task SetAsync(string key, string value, CancellationToken cancellationToken = default);
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}
