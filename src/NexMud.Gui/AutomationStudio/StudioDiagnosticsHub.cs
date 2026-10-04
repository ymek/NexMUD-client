namespace NexMud.Gui.AutomationStudio;

internal sealed record StudioDiagnostic(
    string ProfileId,
    string Source,
    string Key,
    string Severity,
    string Message,
    string? PackageId = null,
    string? FilePath = null,
    int? Line = null,
    int? Column = null);

internal sealed class StudioDiagnosticsHub
{
    private readonly Dictionary<(string ProfileId, string Source, string Key), StudioDiagnostic> _items = [];

    public IReadOnlyList<StudioDiagnostic> Snapshot(string profileId) =>
        _items.Values.Where(item => item.ProfileId == profileId)
            .OrderBy(item => item.PackageId, StringComparer.Ordinal)
            .ThenBy(item => item.FilePath, StringComparer.Ordinal)
            .ThenBy(item => item.Source, StringComparer.Ordinal)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .ToArray();

    public void Upsert(StudioDiagnostic diagnostic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnostic.ProfileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnostic.Source);
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnostic.Key);
        _items[(diagnostic.ProfileId, diagnostic.Source, diagnostic.Key)] = diagnostic;
    }

    public void ReplaceSource(string profileId, string source, IEnumerable<StudioDiagnostic> diagnostics)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(diagnostics);
        StudioDiagnostic[] replacement = diagnostics.ToArray();
        if (replacement.Any(diagnostic => diagnostic.ProfileId != profileId || diagnostic.Source != source))
            throw new ArgumentException("Replacement diagnostics must match the requested profile and source.", nameof(diagnostics));
        ResolveSource(profileId, source);
        foreach (StudioDiagnostic diagnostic in replacement) Upsert(diagnostic);
    }

    public void Resolve(string profileId, string source, string key) =>
        _items.Remove((profileId, source, key));

    public void ResolveSource(string profileId, string source)
    {
        foreach (var key in _items.Keys.Where(key => key.ProfileId == profileId && key.Source == source).ToArray())
            _items.Remove(key);
    }

    public void ResolveSourcesByPrefix(string profileId, string sourcePrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePrefix);
        foreach (var key in _items.Keys.Where(key => key.ProfileId == profileId &&
                     key.Source.StartsWith(sourcePrefix, StringComparison.Ordinal)).ToArray())
            _items.Remove(key);
    }

}
