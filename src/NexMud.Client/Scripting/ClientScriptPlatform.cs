using System.Threading.Channels;
using NexMud.Client.Knowledge;
using NexMud.Contracts.Events;
using NexMud.Core.Actions;
using NexMud.Core.Events;
using NexMud.Core.State;
using NexMud.Scripting.Events;
using NexMud.Scripting.Execution;
using NexMud.Scripting.Host;
using NexMud.Scripting.Permissions;
using NexMud.Scripting.Runtime;
using NexMud.Scripting.Jint.Runtime;
using NexMud.Scripting.Scheduling;

namespace NexMud.Client.Scripting;

/// <summary>
/// Application composition root for the language-neutral scripting platform. Domain services are
/// adapted into capability-gated host interfaces here; the scripting project itself has no
/// dependency on NexMud.Client, transport, persistence schemas, or GUI types.
/// </summary>
public sealed class ClientScriptPlatform : IAsyncDisposable
{
    private readonly ClientScriptEventBridge _eventBridge;
    private readonly ScriptRuntimeProfileRegistry _runtimeProfiles = new();
    private readonly bool _ownsSupervisor;
    private readonly bool _ownsEventHub;

    public ClientScriptPlatform(
        ChannelReader<EventEnvelope> events,
        StateReducer state,
        ActionProcessor actions,
        WorldKnowledgeStore knowledge,
        IScriptMapper mapper,
        IEventSink eventSink,
        IScriptExecutionSupervisor? supervisor = null,
        IScriptScheduler? scheduler = null,
        ScriptEventHub? eventHub = null,
        IScriptCommands? commands = null,
        Func<string>? activeProfileId = null,
        CancellationToken applicationCancellation = default)
    {
        _ownsSupervisor = supervisor is null;
        _ownsEventHub = eventHub is null;
        EventSink = eventSink;
        Supervisor = supervisor ?? new ScriptExecutionSupervisor(applicationCancellation);
        Supervisor.TaskFaulted += HandleTaskFaulted;
        Scheduler = scheduler ?? new ScriptScheduler();
        EventHub = eventHub ?? new ScriptEventHub();
        Runtime = new ManagedScriptRuntime(Supervisor);
        JavaScriptRuntime = new JintScriptRuntime(
            Supervisor,
            diagnostics: new ClientScriptDiagnosticsSink(EventSink, _runtimeProfiles.Resolve));
        Functions = new ClientScriptFunctionInvoker(
            JavaScriptRuntime,
            activeProfileId ?? (() => "default"));
        Commands = commands ?? new ClientScriptCommands(actions, state, Scheduler);
        State = new ClientScriptStateHost(state);
        Mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        Codex = new ClientScriptCodexHost(knowledge);
        _eventBridge = new ClientScriptEventBridge(events, state, EventHub);
    }

    public IScriptExecutionSupervisor Supervisor { get; }
    public IScriptScheduler Scheduler { get; }
    public ScriptEventHub EventHub { get; }
    public IScriptRuntime Runtime { get; }
    public IJavaScriptRuntime JavaScriptRuntime { get; }
    public IScriptFunctions Functions { get; }
    public IScriptCommands Commands { get; }
    public IScriptState State { get; }
    public IScriptMapper Mapper { get; }
    public IScriptCodex Codex { get; }
    public IEventSink EventSink { get; }

    public string? RegisterRuntimeProfile(ScriptModuleId moduleId, string profileId) =>
        _runtimeProfiles.Register(moduleId, profileId);

    public string? ResolveRuntimeProfile(string moduleId) => _runtimeProfiles.Resolve(moduleId);

    public void UnregisterRuntimeProfile(ScriptModuleId moduleId, string profileId) =>
        _runtimeProfiles.Unregister(moduleId, profileId);

    public void RestoreRuntimeProfile(ScriptModuleId moduleId, string expectedProfileId, string? previousProfileId) =>
        _runtimeProfiles.Restore(moduleId, expectedProfileId, previousProfileId);

    private void HandleTaskFaulted(ScriptTaskFault fault)
    {
        _ = PublishTaskFaultAsync(fault);
    }

    private async Task PublishTaskFaultAsync(ScriptTaskFault fault)
    {
        try
        {
            await EventSink.PublishAsync(
                new ScriptRuntimeTaskFaulted(
                    fault.OwnerId,
                    fault.OwnerKind.ToString(),
                    fault.OwnerName,
                    fault.Operation,
                    fault.Exception.GetType().Name,
                    fault.Exception.Message),
                "scripting.runtime").ConfigureAwait(false);
        }
        catch
        {
            // Runtime fault reporting must never create a secondary task fault loop.
        }
    }

    public Task RunEventBridgeAsync(CancellationToken cancellationToken) => _eventBridge.RunAsync(cancellationToken);

    public IScriptHost CreateHost(
        ScriptModuleId moduleId,
        IScriptPermissionSet permissions,
        ScriptCommandOrigin commandOrigin = ScriptCommandOrigin.Script,
        string? ownerName = null,
        string? storageDatabasePath = null,
        IScriptMapper? mapperOverride = null)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        return new CapabilityScriptHost(
            moduleId,
            permissions,
            EventHub,
            Commands,
            State,
            mapperOverride ?? Mapper,
            Codex,
            new SqliteScriptStorage(moduleId, storageDatabasePath, () => Scheduler.UtcNow),
            Scheduler,
            new ClientScriptUiHost(moduleId, EventSink),
            new ClientScriptLogHost(moduleId, EventSink),
            Functions,
            commandOrigin,
            ownerName);
    }

    public async ValueTask DisposeAsync()
    {
        Supervisor.TaskFaulted -= HandleTaskFaulted;
        await JavaScriptRuntime.DisposeAsync().ConfigureAwait(false);
        await Runtime.DisposeAsync().ConfigureAwait(false);
        if (_ownsEventHub) await EventHub.DisposeAsync().ConfigureAwait(false);
        if (_ownsSupervisor) await Supervisor.DisposeAsync().ConfigureAwait(false);
    }
}
