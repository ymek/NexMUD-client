using JevMud.Scripting.Runtime;

namespace JevMud.Scripting.TypeScript.Declarations;

/// <summary>
/// Versioned TypeScript declaration surface for the NexMUD scripting host. This is kept separate
/// from the Jint adapter so the authoring format and execution engine can evolve independently.
/// </summary>
public static class NexMudTypeDeclarations
{
    public static string ApiVersion => ScriptApiVersion.Current;

    public const string ModuleSpecifier = "@nexmud/api";

    public const string Source = """
interface NexMudResourceState {
  readonly current: number | null;
  readonly maximum: number | null;
  readonly percent: number | null;
}

interface NexMudCharacterStateSnapshot {
  readonly health: NexMudResourceState;
  readonly mana: NexMudResourceState;
  readonly movement: NexMudResourceState;
  readonly position: string | null;
}

interface NexMudRoomStateSnapshot {
  readonly id: string | null;
  readonly name: string | null;
  readonly exits: readonly string[];
}

interface NexMudCombatStateSnapshot {
  readonly active: boolean;
  readonly target: string | null;
}

interface NexMudScriptStateSnapshot {
  readonly version: number;
  readonly connected: boolean;
  readonly inputMode: string;
  readonly character: NexMudCharacterStateSnapshot;
  readonly room: NexMudRoomStateSnapshot;
  readonly combat: NexMudCombatStateSnapshot;
}

interface CharacterVitalsChangedEvent {
  readonly health: NexMudResourceState;
  readonly mana: NexMudResourceState;
  readonly movement: NexMudResourceState;
  readonly timestamp: number;
}

interface ConnectionStateChangedEvent {
  readonly status: string;
  readonly host: string | null;
  readonly port: number | null;
  readonly reason: string | null;
}

interface RoomEnteredEvent {
  readonly id: string | null;
  readonly name: string | null;
  readonly exits: readonly unknown[];
}

interface CombatStateEvent {
  readonly active: boolean;
  readonly targetId: string | null;
}

interface ItemAcquiredEvent {
  readonly item: string;
  readonly source: string | null;
  readonly sourceKind: string;
}


interface MapperRouteLifecycleEvent {
  readonly kind: string;
  readonly routeExecutionId: string;
  readonly destinationRoomId: string;
  readonly destinationLabel: string | null;
  readonly routeId: string | null;
  readonly completedSteps: number;
  readonly totalSteps: number;
  readonly routeStep: number | null;
  readonly direction: string | null;
  readonly reason: string | null;
  readonly failureReason: string | null;
}

interface NexMudEventMap {
  "character.vitalsChanged": CharacterVitalsChangedEvent;
  "connection.stateChanged": ConnectionStateChangedEvent;
  "room.entered": RoomEnteredEvent;
  "room.updated": RoomEnteredEvent;
  "combat.started": CombatStateEvent;
  "combat.ended": CombatStateEvent;
  "item.acquired": ItemAcquiredEvent;
  "mapper.routeStarted": MapperRouteLifecycleEvent;
  "mapper.routePlanned": MapperRouteLifecycleEvent;
  "mapper.routeStepStarted": MapperRouteLifecycleEvent;
  "mapper.routeStepCompleted": MapperRouteLifecycleEvent;
  "mapper.routeBlocked": MapperRouteLifecycleEvent;
  "mapper.routeReplanning": MapperRouteLifecycleEvent;
  "mapper.routePaused": MapperRouteLifecycleEvent;
  "mapper.routeResumed": MapperRouteLifecycleEvent;
  "mapper.routeCompleted": MapperRouteLifecycleEvent;
  "mapper.routeAborted": MapperRouteLifecycleEvent;
  "mapper.routeFailed": MapperRouteLifecycleEvent;
}

interface NexMudSubscription {
  dispose(): void;
  cancel(): void;
}

interface NexMudCommandResult {
  readonly accepted: boolean;
  readonly commandId: string;
  readonly reason: string | null;
}

interface NexMudEventsApi {
  on<K extends keyof NexMudEventMap>(
    type: K,
    handler: (event: NexMudEventMap[K]) => void | Promise<void>
  ): NexMudSubscription;
  off(subscription: NexMudSubscription): void;
}

interface NexMudCommandsApi {
  send(command: string, options?: { readonly reason?: string; readonly sensitive?: boolean }): Promise<NexMudCommandResult>;
}

interface NexMudStateApi {
  readonly character: NexMudCharacterStateSnapshot;
  readonly room: NexMudRoomStateSnapshot;
  readonly combat: NexMudCombatStateSnapshot;
  readonly connected: boolean;
  readonly inputMode: string;
  snapshot(): NexMudScriptStateSnapshot;
}

interface NexMudTimerHandle { cancel(): void; }
type NexMudTimerCallback = () => void | Promise<void>;

interface NexMudTimersApi {
  delay(milliseconds: number): Promise<void>;
  after(milliseconds: number, callback: NexMudTimerCallback): NexMudTimerHandle;
  every(milliseconds: number, callback: NexMudTimerCallback): NexMudTimerHandle;
  cancel(handle: NexMudTimerHandle): void;
}

interface NexMudStorageApi {
  get<T>(key: string): Promise<T | null>;
  set<T>(key: string, value: T): Promise<void>;
  delete(key: string): Promise<void>;
  has(key: string): Promise<boolean>;
}

interface NexMudLogApi {
  debug(message: string, data?: Readonly<Record<string, unknown>>): Promise<void>;
  info(message: string, data?: Readonly<Record<string, unknown>>): Promise<void>;
  warn(message: string, data?: Readonly<Record<string, unknown>>): Promise<void>;
  error(message: string, data?: Readonly<Record<string, unknown>>): Promise<void>;
}


interface NexMudMapperRoomRef { readonly roomId: string; }

type NexMudDirection =
  | "north" | "n" | "south" | "s" | "east" | "e" | "west" | "w"
  | "northeast" | "ne" | "northwest" | "nw" | "southeast" | "se" | "southwest" | "sw"
  | "up" | "u" | "down" | "d";

interface NexMudMapperRoomSnapshot {
  readonly id: string;
  readonly name: string | null;
}

interface NexMudMapperRouteStep {
  readonly sequence: number;
  readonly fromRoomId: string;
  readonly direction: string;
  readonly expectedRoomId: string;
  readonly doorState: string | null;
  readonly traversability: string | null;
  readonly recoveryCommand: string | null;
}

interface NexMudMapperRoutePlan {
  readonly routeId: string;
  readonly startRoomId: string;
  readonly destinationRoomId: string;
  readonly graphVersion: number;
  readonly steps: readonly NexMudMapperRouteStep[];
}

type NexMudMovementFailureReason =
  | "NoExit" | "ClosedDoor" | "LockedDoor" | "CannotMove"
  | "CombatRestriction" | "StandingRequired" | "Unknown";

interface NexMudMovementResult {
  readonly kind: "moved" | "blocked" | "unexpected" | "timeout" | "disconnected" | "cancelled";
  readonly fromRoomId: string | null;
  readonly toRoomId: string | null;
  readonly expectedRoomId: string | null;
  readonly actualRoomId: string | null;
  readonly reason: NexMudMovementFailureReason | null;
  readonly message: string | null;
  readonly recoveryCommand: string | null;
  readonly replanSuggested: boolean;
}

interface NexMudMapperMoveOptions {
  readonly fromRoomId?: string;
  readonly expectedRoomId?: string;
  readonly routeExecutionId?: string;
  readonly routeId?: string;
  readonly routeStep?: number;
  readonly routeTotalSteps?: number;
  readonly recovery?: "stand" | "open-door";
}

interface NexMudMapperApi {
  currentRoom(): Promise<NexMudMapperRoomSnapshot | null>;
  findPath(destination: NexMudMapperRoomRef | string): Promise<NexMudMapperRoutePlan | null>;
  move(direction: NexMudDirection | string, options?: NexMudMapperMoveOptions): Promise<NexMudMovementResult>;
}

interface NexMudApi {
  readonly events: NexMudEventsApi;
  readonly commands: NexMudCommandsApi;
  readonly state: NexMudStateApi;
  readonly log: NexMudLogApi;
  readonly timers: NexMudTimersApi;
  readonly storage: NexMudStorageApi;
  readonly mapper: NexMudMapperApi;
}

declare const nex: NexMudApi;

declare module "@nexmud/api" {
  export {
    CharacterVitalsChangedEvent,
    CombatStateEvent,
    ConnectionStateChangedEvent,
    ItemAcquiredEvent,
    MapperRouteLifecycleEvent,
    RoomEnteredEvent,
    NexMudApi,
    NexMudCharacterStateSnapshot,
    NexMudCommandResult,
    NexMudCommandsApi,
    NexMudEventMap,
    NexMudEventsApi,
    NexMudLogApi,
    NexMudMapperApi,
    NexMudMapperMoveOptions,
    NexMudMapperRoomRef,
    NexMudMapperRoomSnapshot,
    NexMudMapperRoutePlan,
    NexMudMapperRouteStep,
    NexMudMovementFailureReason,
    NexMudMovementResult,
    NexMudResourceState,
    NexMudRoomStateSnapshot,
    NexMudCombatStateSnapshot,
    NexMudScriptStateSnapshot,
    NexMudStateApi,
    NexMudStorageApi,
    NexMudSubscription,
    NexMudTimerCallback,
    NexMudTimerHandle,
    NexMudTimersApi,
    nex
  };
}
""";
}
