using System.Security.Cryptography;
using System.Text;

namespace NexMud.Gui.AutomationStudio;

internal enum StudioExternalFileChangeKind
{
    Modified,
    Deleted,
    Renamed
}

internal sealed record StudioExternalFileConflict(
    StudioExternalFileChangeKind Kind,
    string Signature,
    string? DiskContent,
    string? NewPath = null);

internal sealed record StudioTextModelState(
    string Uri,
    string ProfileId,
    string PackageId,
    string RelativePath,
    string BaselineHash,
    StudioExternalFileConflict? Conflict = null,
    string? IgnoredExternalSignature = null,
    bool OverwriteApproved = false);

/// <summary>
/// C# authority for the disk baseline and external-conflict state of every open script model.
/// Monaco owns the editable buffer; this registry owns persistence/concurrency metadata.
/// </summary>
internal sealed class StudioTextModelRegistry
{
    private readonly Dictionary<string, StudioTextModelState> _states = new(StringComparer.Ordinal);

    public IReadOnlyCollection<StudioTextModelState> States => _states.Values;

    public StudioTextModelState Track(
        string uri,
        string profileId,
        string packageId,
        string relativePath,
        string diskContent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        StudioTextModelState state = new(
            uri,
            profileId,
            packageId,
            relativePath,
            Hash(diskContent));
        _states[uri] = state;
        return state;
    }

    public StudioTextModelState? Find(string uri) =>
        _states.TryGetValue(uri, out StudioTextModelState? state) ? state : null;

    public void Remove(string uri) => _states.Remove(uri);

    public void Clear() => _states.Clear();

    public void MarkSaved(string uri, string content)
    {
        if (!_states.TryGetValue(uri, out StudioTextModelState? state)) return;
        _states[uri] = state with
        {
            BaselineHash = Hash(content),
            Conflict = null,
            IgnoredExternalSignature = null,
            OverwriteApproved = false
        };
    }

    public bool MatchesBaseline(string uri, string? diskContent, bool exists = true)
    {
        if (!_states.TryGetValue(uri, out StudioTextModelState? state)) return true;
        return exists && diskContent is not null && state.BaselineHash.Equals(Hash(diskContent), StringComparison.Ordinal);
    }

    public bool CanOverwriteExternalChange(string uri) =>
        _states.TryGetValue(uri, out StudioTextModelState? state) && state.OverwriteApproved;

    public bool RecordConflict(
        string uri,
        StudioExternalFileChangeKind kind,
        string? diskContent,
        string? newPath = null)
    {
        if (!_states.TryGetValue(uri, out StudioTextModelState? state)) return false;
        string signature = Signature(kind, diskContent, newPath);
        if (state.Conflict?.Signature.Equals(signature, StringComparison.Ordinal) == true ||
            state.IgnoredExternalSignature?.Equals(signature, StringComparison.Ordinal) == true) return false;
        _states[uri] = state with
        {
            Conflict = new StudioExternalFileConflict(kind, signature, diskContent, newPath),
            OverwriteApproved = false
        };
        return true;
    }

    public void KeepEditorVersion(string uri)
    {
        if (!_states.TryGetValue(uri, out StudioTextModelState? state) || state.Conflict is null) return;
        _states[uri] = state with
        {
            IgnoredExternalSignature = state.Conflict.Signature,
            Conflict = null,
            OverwriteApproved = true
        };
    }

    public void ClearConflict(string uri)
    {
        if (!_states.TryGetValue(uri, out StudioTextModelState? state)) return;
        _states[uri] = state with { Conflict = null };
    }

    private static string Signature(StudioExternalFileChangeKind kind, string? diskContent, string? newPath) =>
        $"{kind}|{newPath ?? string.Empty}|{(diskContent is null ? "<missing>" : Hash(diskContent))}";

    private static string Hash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
