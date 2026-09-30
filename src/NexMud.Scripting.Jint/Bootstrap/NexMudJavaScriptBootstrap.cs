namespace NexMud.Scripting.Jint.Bootstrap;

public static class NexMudJavaScriptBootstrap
{
    public const string ModuleName = "@nexmud/api";

    public const string Source = """
const pending = new Map();
const eventHandlers = new Map();
const timerHandlers = new Map();
let nextRequest = 1;
let nextSubscription = 1;
let nextTimer = 1;

function payload(value) { return JSON.stringify(value ?? {}); }
function parse(value) { return value == null || value === '' ? null : JSON.parse(value); }
function deepFreeze(value) {
  if (value == null || typeof value !== 'object' || Object.isFrozen(value)) return value;
  for (const nested of Object.values(value)) deepFreeze(nested);
  return Object.freeze(value);
}
function hostError(error, fallback = 'Host operation failed.') {
  const exception = new Error(error?.message ?? fallback);
  exception.code = error?.code ?? 'HostOperationError';
  return exception;
}
function query(operation, value) {
  const response = parse(globalThis.__nexQueryRaw(operation, payload(value)));
  if (!response?.ok) throw hostError(response?.error);
  return response.value ?? null;
}
function begin(operation, value) {
  const requestId = `r${nextRequest++}`;
  return new Promise((resolve, reject) => {
    pending.set(requestId, { resolve, reject });
    globalThis.__nexBeginRaw(operation, requestId, payload(value));
  });
}
function completeInvocation(invocationId, succeeded, resultJson = '', error = '') {
  globalThis.__nexInvocationCompleteRaw(invocationId, succeeded, String(resultJson ?? ''), String(error ?? ''));
}
function invokeCallable(fn, contextJson, invocationId) {
  if (typeof fn !== 'function') {
    completeInvocation(invocationId, false, '', 'Selected export is not callable.');
    return;
  }
  try {
    const context = deepFreeze(parse(contextJson));
    const result = fn(context);
    if (result && typeof result.then === 'function') {
      result.then(
        value => completeInvocation(invocationId, true, payload(value), ''),
        error => completeInvocation(invocationId, false, '', error?.message ?? 'Script function failed.')
      );
    } else {
      completeInvocation(invocationId, true, payload(result), '');
    }
  } catch (error) {
    completeInvocation(invocationId, false, '', error?.message ?? 'Script function failed.');
  }
}

Object.defineProperty(globalThis, '__nexResolve', { value: (requestId, json) => {
  const entry = pending.get(requestId);
  if (!entry) return;
  pending.delete(requestId);
  entry.resolve(parse(json));
}, writable: false, configurable: false });
Object.defineProperty(globalThis, '__nexReject', { value: (requestId, json) => {
  const entry = pending.get(requestId);
  if (!entry) return;
  pending.delete(requestId);
  const error = parse(json) ?? { message: 'Host operation failed.' };
  entry.reject(hostError(error));
}, writable: false, configurable: false });
Object.defineProperty(globalThis, '__nexDispatchEvent', { value: (id, json, invocationId) => {
  const handler = eventHandlers.get(id);
  if (!handler) { completeInvocation(invocationId, true); return; }
  try {
    const result = handler(deepFreeze(parse(json)));
    if (result && typeof result.then === 'function') {
      result.then(
        () => completeInvocation(invocationId, true),
        error => completeInvocation(invocationId, false, '', error?.message ?? 'Script handler failed.')
      );
    } else {
      completeInvocation(invocationId, true);
    }
  } catch (error) {
    completeInvocation(invocationId, false, '', error?.message ?? 'Script handler failed.');
  }
}, writable: false, configurable: false });
Object.defineProperty(globalThis, '__nexDispatchTimer', { value: (id, invocationId) => {
  const entry = timerHandlers.get(id);
  if (!entry) { completeInvocation(invocationId, true); return; }
  try {
    const result = entry.handler();
    if (!entry.recurring) timerHandlers.delete(id);
    if (result && typeof result.then === 'function') {
      result.then(
        () => completeInvocation(invocationId, true),
        error => completeInvocation(invocationId, false, '', error?.message ?? 'Timer handler failed.')
      );
    } else {
      completeInvocation(invocationId, true);
    }
  } catch (error) {
    completeInvocation(invocationId, false, '', error?.message ?? 'Timer handler failed.');
  }
}, writable: false, configurable: false });
Object.defineProperty(globalThis, '__nexInvokeCallable', { value: invokeCallable, writable: false, configurable: false });
Object.defineProperty(globalThis, '__nexParseHostJson', { value: (json) => parse(json), writable: false, configurable: false });

const events = Object.freeze({
  on(types, handler) {
    if (typeof handler !== 'function') throw new TypeError('event handler must be a function');
    const id = `s${nextSubscription++}`;
    const normalized = Array.isArray(types) ? types : [types];
    eventHandlers.set(id, handler);
    globalThis.__nexSubscribeRaw(id, payload(normalized));
    const dispose = () => { eventHandlers.delete(id); globalThis.__nexUnsubscribeRaw(id); };
    return Object.freeze({ dispose, cancel: dispose });
  },
  off(subscription) { subscription?.dispose?.(); }
});

const commands = Object.freeze({
  async send(command, options = {}) {
    const result = await begin('commands.send', { command: String(command ?? ''), ...options });
    return Object.freeze({
      accepted: Boolean(result?.accepted),
      commandId: String(result?.actionId ?? ''),
      reason: result?.reason ?? null
    });
  }
});
const state = Object.freeze({
  snapshot() { return deepFreeze(query('state.snapshot', {})); },
  get character() { return deepFreeze(query('state.snapshot', {}).character); },
  get room() { return deepFreeze(query('state.snapshot', {}).room); },
  get combat() { return deepFreeze(query('state.snapshot', {}).combat); },
  get connected() { return Boolean(query('state.snapshot', {}).connected); },
  get inputMode() { return String(query('state.snapshot', {}).inputMode ?? ''); }
});
const mapper = Object.freeze({
  currentRoom() { return begin('mapper.currentRoom', {}); },
  findPath(destination, options = {}) {
    const roomId = typeof destination === 'string' ? destination : destination?.roomId;
    return begin('mapper.findPath', { destinationRoomId: String(roomId ?? ''), ...options });
  },
  move(direction, options = {}) {
    return begin('mapper.move', { direction: String(direction ?? ''), ...options });
  }
});
const codex = Object.freeze({
  search(queryText, limit = 50) { return begin('codex.search', { query: String(queryText ?? ''), limit: Number(limit) }); }
});
const storage = Object.freeze({
  get(key) { return begin('storage.get', { key }); },
  set(key, value) { return begin('storage.set', { key, value }); },
  delete(key) { return begin('storage.delete', { key }); },
  has(key) { return begin('storage.has', { key }); }
});
const timers = Object.freeze({
  delay(milliseconds) { return begin('timers.delay', { milliseconds: Number(milliseconds) }); },
  after(milliseconds, handler) { return schedule(milliseconds, false, handler); },
  every(milliseconds, handler) { return schedule(milliseconds, true, handler); },
  cancel(handle) { handle?.cancel?.(); }
});
function schedule(milliseconds, recurring, handler) {
  if (typeof handler !== 'function') throw new TypeError('timer handler must be a function');
  const id = `t${nextTimer++}`;
  timerHandlers.set(id, { handler, recurring });
  globalThis.__nexScheduleRaw(id, Number(milliseconds), recurring);
  return Object.freeze({ cancel() { timerHandlers.delete(id); globalThis.__nexCancelTimerRaw(id); } });
}
function logWrite(level, message, data) { return begin('log.write', { level, message: String(message ?? ''), data: data ?? null }); }
const log = Object.freeze({
  trace(message, data) { return logWrite('Trace', message, data); },
  debug(message, data) { return logWrite('Debug', message, data); },
  info(message, data) { return logWrite('Information', message, data); },
  warn(message, data) { return logWrite('Warning', message, data); },
  error(message, data) { return logWrite('Error', message, data); }
});

// Deliberately omitted from @nexmud/api declarations. Only the generated Automation package is
// granted InvokeScriptFunctions; capability gating remains authoritative at the C# host boundary.
const __automation = Object.freeze({
  invokeScriptFunction(request) { return begin('scripts.invoke', request ?? {}); }
});

const nex = Object.freeze({ events, commands, state, log, timers, storage, mapper, codex, __automation });
Object.defineProperty(globalThis, 'nex', { value: nex, writable: false, configurable: false });
export { nex };
""";
}
