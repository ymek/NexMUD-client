namespace NexMud.Gui.AutomationStudio;

internal sealed record StudioDocumentSessionSnapshot(
    IReadOnlyList<StudioDocument> Documents,
    string? ActiveDocumentKey);

internal sealed class StudioDocumentSessionStore
{
    private readonly Dictionary<string, StudioDocumentSessionSnapshot> _sessions = new(StringComparer.Ordinal);

    public void Capture(string profileId, IReadOnlyList<StudioDocument> documents, string? activeDocumentKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentNullException.ThrowIfNull(documents);
        if (documents.Count == 0) return;
        _sessions[profileId] = new StudioDocumentSessionSnapshot(documents.ToArray(), activeDocumentKey);
    }

    public bool TryGet(string profileId, out StudioDocumentSessionSnapshot snapshot)
    {
        if (_sessions.TryGetValue(profileId, out StudioDocumentSessionSnapshot? found))
        {
            snapshot = found;
            return true;
        }

        snapshot = null!;
        return false;
    }

    public void Remove(string profileId) => _sessions.Remove(profileId);
}
