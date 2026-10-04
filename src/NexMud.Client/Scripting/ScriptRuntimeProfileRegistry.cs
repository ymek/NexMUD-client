using System.Collections.Concurrent;
using NexMud.Scripting.Diagnostics;
using NexMud.Scripting.Runtime;

namespace NexMud.Client.Scripting;

internal sealed class ScriptRuntimeProfileRegistry
{
    private readonly ConcurrentDictionary<string, string> _moduleProfiles = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string ModuleId, Guid InstanceId), string> _instanceProfiles = new();
    public string? Register(ScriptModuleId moduleId, string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        string? previousProfile = null;
        _moduleProfiles.AddOrUpdate(moduleId.Value, profileId, (_, current) =>
        {
            previousProfile = current;
            return profileId;
        });
        return previousProfile;
    }

    public void Unregister(ScriptModuleId moduleId, string profileId)
    {
        ((ICollection<KeyValuePair<string, string>>)_moduleProfiles)
            .Remove(new KeyValuePair<string, string>(moduleId.Value, profileId));
    }

    public void Restore(ScriptModuleId moduleId, string expectedProfileId, string? previousProfileId)
    {
        if (previousProfileId is null)
        {
            Unregister(moduleId, expectedProfileId);
            return;
        }

        _moduleProfiles.TryUpdate(moduleId.Value, previousProfileId, expectedProfileId);
    }

    public string? Resolve(ScriptDiagnosticRecord diagnostic)
    {
        string moduleId = diagnostic.ScriptId.Value;
        var instanceKey = (moduleId, diagnostic.ScriptInstanceId);
        if (diagnostic.Kind == ScriptDiagnosticKind.ScriptLoaded)
        {
            if (!_moduleProfiles.TryGetValue(moduleId, out string? loadedProfile)) return null;
            _instanceProfiles[instanceKey] = loadedProfile;
            return loadedProfile;
        }

        if (_instanceProfiles.TryGetValue(instanceKey, out string? instanceProfile))
        {
            if (diagnostic.Kind == ScriptDiagnosticKind.ScriptUnloaded)
                _instanceProfiles.TryRemove(instanceKey, out _);
            return instanceProfile;
        }

        return diagnostic.ScriptInstanceId == Guid.Empty && _moduleProfiles.TryGetValue(moduleId, out string? moduleProfile)
            ? moduleProfile
            : null;
    }
}
