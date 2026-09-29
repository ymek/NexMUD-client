using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NexMud.Client.Settings;
using NexMud.Contracts.Events;
using NexMud.Scripting.Compilation;
using NexMud.Scripting.Host;
using NexMud.Scripting.Permissions;
using NexMud.Scripting.Runtime;

namespace NexMud.Client.Navigation;

internal sealed class MapperRouteControl : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _abort = new();
    private TaskCompletionSource<bool> _resume = CompletedSignal();
    private bool _paused;
    private bool _disposed;

    public bool IsPaused { get { lock (_gate) return _paused; } }
    public bool IsAborted => _abort.IsCancellationRequested;
    public CancellationToken CancellationToken => _abort.Token;

    public bool Pause()
    {
        lock (_gate)
        {
            if (_disposed || _abort.IsCancellationRequested || _paused) return false;
            _paused = true;
            _resume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            return true;
        }
    }

    public bool Resume()
    {
        TaskCompletionSource<bool>? signal = null;
        lock (_gate)
        {
            if (_disposed || _abort.IsCancellationRequested || !_paused) return false;
            _paused = false;
            signal = _resume;
        }
        signal.TrySetResult(true);
        return true;
    }

    public void Abort()
    {
        TaskCompletionSource<bool> signal;
        lock (_gate)
        {
            if (_abort.IsCancellationRequested) return;
            _paused = false;
            signal = _resume;
            _abort.Cancel();
        }
        signal.TrySetCanceled(_abort.Token);
    }

    public async Task WaitUntilRunnableAsync(CancellationToken cancellationToken)
    {
        Task task;
        lock (_gate)
        {
            if (_abort.IsCancellationRequested) throw new OperationCanceledException(_abort.Token);
            task = _paused ? _resume.Task : Task.CompletedTask;
        }
        if (task.IsCompletedSuccessfully) return;
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _abort.Token);
        await task.WaitAsync(linked.Token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Abort();
        _abort.Dispose();
    }

    private static TaskCompletionSource<bool> CompletedSignal()
    {
        TaskCompletionSource<bool> source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult(true);
        return source;
    }
}

internal sealed record MapperRouteRuntimeUpdate(
    MapperRouteLifecycleKind Kind,
    AutoMoveStatus Status,
    string? Reason = null,
    string? RouteId = null,
    int CompletedSteps = 0,
    int TotalSteps = 0,
    int? RouteStep = null,
    string? Direction = null,
    MapperRouteFailureReason? FailureReason = null);

internal sealed class MapperRouteExecutionContext
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _recoveryAttempts = new(StringComparer.Ordinal);
    private int _completedSteps;
    private int _totalSteps;
    private int _replans;
    private bool _planned;
    private int _moveInFlight;

    public MapperRouteExecutionContext(
        Guid executionId,
        string destinationRoomId,
        string? destinationLabel,
        bool singleStep,
        int maximumReplans,
        MapperRouteControl control,
        Func<MapperRouteRuntimeUpdate, CancellationToken, Task> report)
    {
        ExecutionId = executionId;
        DestinationRoomId = destinationRoomId;
        DestinationLabel = destinationLabel;
        SingleStep = singleStep;
        MaximumReplans = Math.Max(0, maximumReplans);
        Control = control;
        Report = report;
    }

    public Guid ExecutionId { get; }
    public string DestinationRoomId { get; }
    public string? DestinationLabel { get; }
    public bool SingleStep { get; }
    public int MaximumReplans { get; }
    public MapperRouteControl Control { get; }
    public Func<MapperRouteRuntimeUpdate, CancellationToken, Task> Report { get; }
    public string? ScriptVersion { get; private set; }
    public string? RuntimeOwnerName { get; private set; }

    public void BindRuntimeIdentity(string scriptVersion, string runtimeOwnerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeOwnerName);
        ScriptVersion = scriptVersion;
        RuntimeOwnerName = runtimeOwnerName;
    }

    public (int Completed, int Total) SetPlan(int remainingSteps)
    {
        lock (_gate)
        {
            _totalSteps = Math.Max(_totalSteps, _completedSteps + Math.Max(0, remainingSteps));
            _planned = true;
            return (_completedSteps, _totalSteps);
        }
    }

    public bool HasPlanned { get { lock (_gate) return _planned; } }
    public bool MoveInFlight => Volatile.Read(ref _moveInFlight) != 0;
    public void BeginMove() => Interlocked.Exchange(ref _moveInFlight, 1);
    public void EndMove() => Interlocked.Exchange(ref _moveInFlight, 0);

    public (int Completed, int Total) CompleteStep()
    {
        lock (_gate)
        {
            _completedSteps++;
            _totalSteps = Math.Max(_totalSteps, _completedSteps);
            return (_completedSteps, _totalSteps);
        }
    }

    public (int Completed, int Total) Progress()
    {
        lock (_gate) return (_completedSteps, _totalSteps);
    }

    public bool TryUseRecovery(string routeId, int? step, ScriptMovementFailureReason reason)
    {
        string key = $"{routeId}:{step?.ToString() ?? "?"}:{reason}";
        lock (_gate)
        {
            int count = _recoveryAttempts.GetValueOrDefault(key);
            _recoveryAttempts[key] = count + 1;
            return count == 0;
        }
    }

    public bool TryReplan()
    {
        lock (_gate)
        {
            if (_replans >= MaximumReplans) return false;
            _replans++;
            return true;
        }
    }
}

/// <summary>
/// Route-scoped Mapper host. It adds lifecycle/authority semantics around the stable Mapper domain
/// API without exposing route-control or persistence primitives to JavaScript.
/// </summary>
internal sealed class RouteBoundScriptMapperHost : IScriptMapper
{
    private readonly IScriptMapper _inner;
    private readonly MapperRouteExecutionContext _context;

    public RouteBoundScriptMapperHost(IScriptMapper inner, MapperRouteExecutionContext context)
    {
        _inner = inner;
        _context = context;
    }

    public async Task<ScriptRoomSnapshot?> CurrentRoomAsync(CancellationToken cancellationToken = default)
    {
        await _context.Control.WaitUntilRunnableAsync(cancellationToken).ConfigureAwait(false);
        ScriptRoomSnapshot? room = await _inner.CurrentRoomAsync(cancellationToken).ConfigureAwait(false);
        (int completed, int total) = _context.Progress();
        if (room is null)
        {
            await _context.Report(new MapperRouteRuntimeUpdate(
                MapperRouteLifecycleKind.Failed,
                AutoMoveStatus.Failed,
                "Current room is unknown.",
                CompletedSteps: completed,
                TotalSteps: total,
                FailureReason: MapperRouteFailureReason.NoCurrentRoom), cancellationToken).ConfigureAwait(false);
        }
        else if (string.Equals(room.Id, _context.DestinationRoomId, StringComparison.Ordinal))
        {
            await _context.Report(new MapperRouteRuntimeUpdate(
                MapperRouteLifecycleKind.Completed,
                AutoMoveStatus.Completed,
                "Destination reached.",
                CompletedSteps: completed,
                TotalSteps: Math.Max(total, completed)), cancellationToken).ConfigureAwait(false);
        }
        return room;
    }

    public async Task<ScriptRoutePlan?> FindPathAsync(string destinationRoomId, CancellationToken cancellationToken = default)
    {
        await _context.Control.WaitUntilRunnableAsync(cancellationToken).ConfigureAwait(false);
        ScriptRoutePlan? plan = await _inner.FindPathAsync(destinationRoomId, cancellationToken).ConfigureAwait(false);
        (int completed, int total) = _context.Progress();
        if (plan is null)
        {
            await _context.Report(new MapperRouteRuntimeUpdate(
                MapperRouteLifecycleKind.Failed,
                AutoMoveStatus.Failed,
                "No known route to the destination.",
                CompletedSteps: completed,
                TotalSteps: total,
                FailureReason: MapperRouteFailureReason.NoPath), cancellationToken).ConfigureAwait(false);
            return null;
        }

        (completed, total) = _context.SetPlan(plan.Steps.Count);
        await _context.Report(new MapperRouteRuntimeUpdate(
            MapperRouteLifecycleKind.Planned,
            AutoMoveStatus.Moving,
            RouteId: plan.RouteId,
            CompletedSteps: completed,
            TotalSteps: total,
            RouteStep: plan.Steps.Count > 0 ? plan.Steps[0].Sequence : null,
            Direction: plan.Steps.Count > 0 ? plan.Steps[0].Direction : null), cancellationToken).ConfigureAwait(false);
        return plan;
    }

    public async Task<ScriptMovementResult> MoveAsync(ScriptMoveRequest request, CancellationToken cancellationToken = default)
    {
        await _context.Control.WaitUntilRunnableAsync(cancellationToken).ConfigureAwait(false);
        ScriptMoveRequest bound = request with { RouteExecutionId = _context.ExecutionId };
        (int completed, int total) = _context.Progress();
        await _context.Report(new MapperRouteRuntimeUpdate(
            MapperRouteLifecycleKind.StepStarted,
            string.IsNullOrWhiteSpace(request.Recovery) ? AutoMoveStatus.Moving : AutoMoveStatus.Recovering,
            string.IsNullOrWhiteSpace(request.Recovery) ? null : $"Recovery: {request.Recovery}",
            request.RouteId,
            completed,
            total,
            request.RouteStep,
            request.Direction), cancellationToken).ConfigureAwait(false);

        _context.BeginMove();
        ScriptMovementResult result;
        try
        {
            result = await _inner.MoveAsync(bound, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _context.EndMove();
        }
        switch (result.Kind)
        {
            case "moved":
                (completed, total) = _context.CompleteStep();
                await _context.Report(new MapperRouteRuntimeUpdate(
                    MapperRouteLifecycleKind.StepCompleted,
                    AutoMoveStatus.Moving,
                    RouteId: request.RouteId,
                    CompletedSteps: completed,
                    TotalSteps: total,
                    RouteStep: request.RouteStep,
                    Direction: request.Direction), cancellationToken).ConfigureAwait(false);
                if (string.Equals(result.ToRoomId, _context.DestinationRoomId, StringComparison.Ordinal))
                {
                    await _context.Report(new MapperRouteRuntimeUpdate(
                        MapperRouteLifecycleKind.Completed,
                        AutoMoveStatus.Completed,
                        "Destination reached.",
                        request.RouteId,
                        completed,
                        total,
                        request.RouteStep,
                        request.Direction), cancellationToken).ConfigureAwait(false);
                }
                else if (_context.SingleStep && _context.Control.Pause())
                {
                    await _context.Report(new MapperRouteRuntimeUpdate(
                        MapperRouteLifecycleKind.Paused,
                        AutoMoveStatus.Paused,
                        "Step complete.",
                        request.RouteId,
                        completed,
                        total,
                        request.RouteStep,
                        request.Direction), cancellationToken).ConfigureAwait(false);
                }
                else if (_context.Control.IsPaused)
                {
                    await _context.Report(new MapperRouteRuntimeUpdate(
                        MapperRouteLifecycleKind.Paused,
                        AutoMoveStatus.Paused,
                        "Paused after current movement settled.",
                        request.RouteId,
                        completed,
                        total,
                        request.RouteStep,
                        request.Direction), cancellationToken).ConfigureAwait(false);
                }
                break;

            case "unexpected":
                if (_context.TryReplan())
                {
                    await _context.Report(new MapperRouteRuntimeUpdate(
                        MapperRouteLifecycleKind.Replanning,
                        AutoMoveStatus.Replanning,
                        $"Expected room {result.ExpectedRoomId ?? "?"}, observed {result.ActualRoomId ?? "?"}.",
                        request.RouteId,
                        completed,
                        total,
                        request.RouteStep,
                        request.Direction), cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    result = result with { Kind = "blocked", Reason = ScriptMovementFailureReason.Unknown, Message = "Route replan budget exhausted." };
                    await _context.Report(new MapperRouteRuntimeUpdate(
                        MapperRouteLifecycleKind.Failed,
                        AutoMoveStatus.Failed,
                        "Route replan budget exhausted.",
                        request.RouteId,
                        completed,
                        total,
                        request.RouteStep,
                        request.Direction,
                        MapperRouteFailureReason.RecoveryExhausted), cancellationToken).ConfigureAwait(false);
                }
                break;

            case "blocked":
                bool recoverable = result.Reason is ScriptMovementFailureReason.StandingRequired or ScriptMovementFailureReason.ClosedDoor &&
                                   !string.IsNullOrWhiteSpace(result.RecoveryCommand);
                bool canRecover = recoverable && string.IsNullOrWhiteSpace(request.Recovery) && result.Reason is { } reason &&
                                  _context.TryUseRecovery(request.RouteId ?? "route", request.RouteStep, reason);
                await _context.Report(new MapperRouteRuntimeUpdate(
                    MapperRouteLifecycleKind.Blocked,
                    canRecover ? AutoMoveStatus.Recovering : AutoMoveStatus.Replanning,
                    result.Message ?? result.Reason?.ToString() ?? "Movement blocked.",
                    request.RouteId,
                    completed,
                    total,
                    request.RouteStep,
                    request.Direction), cancellationToken).ConfigureAwait(false);

                if (!canRecover && _context.TryReplan())
                {
                    result = result with { ReplanSuggested = true };
                    await _context.Report(new MapperRouteRuntimeUpdate(
                        MapperRouteLifecycleKind.Replanning,
                        AutoMoveStatus.Replanning,
                        "Movement blocked; recomputing route from the authoritative room.",
                        request.RouteId,
                        completed,
                        total,
                        request.RouteStep,
                        request.Direction), cancellationToken).ConfigureAwait(false);
                }
                else if (!canRecover)
                {
                    await _context.Report(new MapperRouteRuntimeUpdate(
                        MapperRouteLifecycleKind.Failed,
                        AutoMoveStatus.Failed,
                        recoverable ? "Movement recovery/replan budget exhausted." : result.Message ?? "Movement blocked.",
                        request.RouteId,
                        completed,
                        total,
                        request.RouteStep,
                        request.Direction,
                        recoverable ? MapperRouteFailureReason.RecoveryExhausted : MapperRouteFailureReason.MovementBlocked), cancellationToken).ConfigureAwait(false);
                }
                break;

            case "timeout":
                await _context.Report(new MapperRouteRuntimeUpdate(
                    MapperRouteLifecycleKind.Failed,
                    AutoMoveStatus.Failed,
                    "Timed out waiting for movement confirmation.",
                    request.RouteId,
                    completed,
                    total,
                    request.RouteStep,
                    request.Direction,
                    MapperRouteFailureReason.MovementTimeout), cancellationToken).ConfigureAwait(false);
                break;

            case "disconnected":
                await _context.Report(new MapperRouteRuntimeUpdate(
                    MapperRouteLifecycleKind.Failed,
                    AutoMoveStatus.Failed,
                    "Disconnected during route execution.",
                    request.RouteId,
                    completed,
                    total,
                    request.RouteStep,
                    request.Direction,
                    MapperRouteFailureReason.Disconnected), cancellationToken).ConfigureAwait(false);
                break;

            case "cancelled":
                await _context.Report(new MapperRouteRuntimeUpdate(
                    _context.Control.IsAborted ? MapperRouteLifecycleKind.Aborted : MapperRouteLifecycleKind.Paused,
                    _context.Control.IsAborted ? AutoMoveStatus.Aborted : AutoMoveStatus.Paused,
                    _context.Control.IsAborted ? "Route aborted." : "Route paused.",
                    request.RouteId,
                    completed,
                    total,
                    request.RouteStep,
                    request.Direction,
                    _context.Control.IsAborted ? MapperRouteFailureReason.Cancelled : null), CancellationToken.None).ConfigureAwait(false);
                break;
        }
        return result;
    }
}

internal static class MapperRouteScriptCompiler
{
    public static readonly ScriptModuleId ModuleId = new("nexmud.mapper.route");
    public const string StartEventType = "mapper.route.execute";

    public static ScriptCapability Permissions =>
        ScriptCapability.ReadMapper |
        ScriptCapability.MapperPathfind |
        ScriptCapability.MapperMove |
        ScriptCapability.CreateTimers |
        ScriptCapability.SubscribeEvents |
        ScriptCapability.MapperRouteObserve |
        ScriptCapability.Log;

    public static CompiledScriptPackage Compile(
        Guid routeExecutionId,
        string destinationRoomId,
        MapperPreferences settings,
        ScriptRoutePlan? initialPlan = null)
    {
        string destinationJson = JsonSerializer.Serialize(destinationRoomId);
        string initialPlanJson = JsonSerializer.Serialize(initialPlan, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        int delay = Math.Clamp(settings.AutoMoveStepDelayMilliseconds, 0, 5000);
        string source = $$"""
            import { nex } from "@nexmud/api";

            const destination = Object.freeze({ roomId: {{destinationJson}} });
            const initialPlan = {{initialPlanJson}};
            const routeExecutionId = "{{routeExecutionId:D}}";
            const stepDelay = {{delay}};
            const recoveryAttempts = new Set();
            let started = false;
            let startupFallback = null;

            export function activate() {
              nex.events.on("{{StartEventType}}", event => {
                if (event?.routeExecutionId !== routeExecutionId) return;
                if (startupFallback) nex.timers.cancel(startupFallback);
                return startRoute();
              });

              // Startup is normally driven explicitly by AutoMoveService after the module is fully
              // loaded. Keep a bounded fallback so a lost internal start signal cannot strand the
              // UI in Planning forever. startRoute() is idempotent, so the two paths cannot race
              // into duplicate route execution.
              startupFallback = nex.timers.after(250, startRoute);
            }

            function startRoute() {
              if (started) return;
              started = true;
              return executeRoute();
            }

            async function executeRoute() {
              let seededPlan = initialPlan;
              while (true) {
                const current = await nex.mapper.currentRoom();
                if (!current || current.id === destination.roomId) return;

                let plan = seededPlan;
                seededPlan = null;
                if (!plan || plan.startRoomId !== current.id || plan.destinationRoomId !== destination.roomId) {
                  plan = await nex.mapper.findPath(destination);
                }
                if (!plan || plan.steps.length === 0) return;

                const step = plan.steps[0];
                let result = await move(step, plan, null);

                if (result.kind === "moved") {
                  if (result.toRoomId === destination.roomId) return;
                  if (stepDelay > 0) await nex.timers.delay(stepDelay);
                  continue;
                }
                if (result.kind === "unexpected") {
                  continue;
                }
                if (result.kind !== "blocked") return;

                const recovery = recoveryKind(result);
                const recoveryKey = `${plan.routeId}:${step.sequence}:${recovery ?? "none"}`;
                if (recovery && !recoveryAttempts.has(recoveryKey)) {
                  recoveryAttempts.add(recoveryKey);
                  result = await move(step, plan, recovery);
                  if (result.kind === "moved" || result.kind === "unexpected") {
                    if (result.toRoomId === destination.roomId) return;
                    if (stepDelay > 0) await nex.timers.delay(stepDelay);
                    continue;
                  }
                }
                if (result.kind === "blocked" && result.replanSuggested) {
                  if (stepDelay > 0) await nex.timers.delay(stepDelay);
                  continue;
                }
                return;
              }
            }

            function move(step, plan, recovery) {
              return nex.mapper.move(step.direction, {
                fromRoomId: step.fromRoomId,
                expectedRoomId: step.expectedRoomId,
                routeExecutionId,
                routeId: plan.routeId,
                routeStep: step.sequence,
                routeTotalSteps: plan.steps.length,
                recovery
              });
            }

            function recoveryKind(result) {
              if (!result?.recoveryCommand) return null;
              if (result.reason === "StandingRequired") return "stand";
              if (result.reason === "ClosedDoor") return "open-door";
              return null;
            }
            """;

        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
        ScriptManifest manifest = new(
            ModuleId,
            $"Mapper route {routeExecutionId:N}",
            $"1.0.0+{hash[..12]}",
            ScriptApiVersion.Current,
            "main.js",
            Permissions,
            ScriptOwnerKind.MapperRoute,
            ScriptRuntimeProfile.InternalTrusted);
        return new CompiledScriptPackage(
            manifest,
            [new CompiledScriptModule("main.js", source, OriginalSourcePath: "internal/mapper-route.ts")],
            "nexmud-mapper-route/1",
            hash);
    }
}
