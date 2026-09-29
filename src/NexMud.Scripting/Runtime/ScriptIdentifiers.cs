namespace NexMud.Scripting.Runtime;

public readonly record struct ScriptModuleId
{
    public ScriptModuleId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value.Trim();
    }

    public string Value { get; }
    public override string ToString() => Value;
}

public enum ScriptOwnerKind
{
    Application,
    UserScript,
    AutomationRule,
    AutomationWorkflow,
    JevSession,
    MapperRoute,
    Plugin,
    System
}

public sealed record ScriptOwner(
    Guid Id,
    ScriptOwnerKind Kind,
    string Name,
    ScriptModuleId? ModuleId = null,
    Guid? ParentId = null)
{
    public static ScriptOwner Create(
        ScriptOwnerKind kind,
        string name,
        ScriptModuleId? moduleId = null,
        Guid? parentId = null) =>
        new(Guid.NewGuid(), kind, name, moduleId, parentId);
}
