namespace JevMud.Scripting.Permissions;

[Flags]
public enum ScriptCapability
{
    None = 0,
    ReadState = 1 << 0,
    SendCommands = 1 << 1,
    ReadMapper = 1 << 2,
    ModifyMapper = 1 << 3,
    ReadCodex = 1 << 4,
    WriteScriptStorage = 1 << 5,
    CreateTimers = 1 << 6,
    SubscribeEvents = 1 << 7,
    EmitUiNotifications = 1 << 8,
    NetworkAccess = 1 << 9,
    FileAccess = 1 << 10,
    AdvancedDatabaseAccess = 1 << 11,
    Log = 1 << 12,
    ReadScriptStorage = 1 << 13,
    MapperPathfind = 1 << 14,
    MapperMove = 1 << 15,
    MapperRouteObserve = 1 << 16
}

public interface IScriptPermissionSet
{
    ScriptCapability Capabilities { get; }
    bool Allows(ScriptCapability capability);
    void Demand(ScriptCapability capability);
}

public sealed record ScriptPermissionSet(ScriptCapability Capabilities) : IScriptPermissionSet
{
    public static ScriptPermissionSet None { get; } = new(ScriptCapability.None);

    public bool Allows(ScriptCapability capability) =>
        capability == ScriptCapability.None || (Capabilities & capability) == capability;

    public void Demand(ScriptCapability capability)
    {
        if (!Allows(capability))
            throw new UnauthorizedAccessException($"Script capability '{capability}' is not granted.");
    }
}
