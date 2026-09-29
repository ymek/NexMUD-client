namespace JevMud.Client.Secrets;

public static class SecretStoreFactory
{
    public static ISecretStore CreateDefault() => OperatingSystem.IsMacOS()
        ? new MacOsKeychainSecretStore()
        : new UnavailableSecretStore();

    private sealed class UnavailableSecretStore : ISecretStore
    {
        public bool IsAvailable => false;

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default) =>
            throw new PlatformNotSupportedException("Secure secret storage is currently implemented for macOS Keychain only.");

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
