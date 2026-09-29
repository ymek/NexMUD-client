using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using NexMud.Client.Settings;
using NexMud.Scripting.Compilation;
using NexMud.Scripting.Permissions;
using NexMud.Scripting.Runtime;

namespace NexMud.Client.Automation;

public sealed record AutomationCompilerDiagnostic(string Code, string Message, string? AutomationId = null);

public sealed record CompiledAutomationArtifact(
    CompiledScriptPackage Package,
    IReadOnlyList<AutomationProgram> Programs,
    IReadOnlyList<AutomationSourceMapEntry> SourceMap,
    IReadOnlyList<AutomationCompilerDiagnostic> Diagnostics,
    string ContentHash,
    string RuntimeGeneration);

public interface IAutomationCompiler
{
    string CompilerIdentity { get; }
    CompiledAutomationArtifact Compile(IReadOnlyList<AutomationProgram> programs, string scriptApiVersion);
}

/// <summary>
/// Deterministic compiler from validated Automation IR to a single profile-scoped JavaScript
/// module. Generated code uses only the public NexMUD SDK.
/// </summary>
public sealed class AutomationJavaScriptCompiler : IAutomationCompiler
{
    private const string ModuleIdValue = "automation.profile";
    private const string Entrypoint = "main.js";
    private const string JavaScriptTarget = "ES2022";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public string CompilerIdentity => "nexmud-automation-js/1";

    public CompiledAutomationArtifact Compile(IReadOnlyList<AutomationProgram> programs, string scriptApiVersion)
    {
        ArgumentNullException.ThrowIfNull(programs);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptApiVersion);

        AutomationProgram[] ordered = programs
            .Where(program => program.Enabled)
            .OrderByDescending(program => program.Priority)
            .ThenBy(program => program.Order)
            .ThenBy(program => program.Type)
            .ThenBy(program => program.Id, StringComparer.Ordinal)
            .ToArray();
        Validate(ordered);

        ScriptCapability permissions = DerivePermissions(ordered);
        string programJson = SerializeCanonical(ordered);
        string runtimeGeneration = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{CompilerIdentity}\n{scriptApiVersion}\n{JavaScriptTarget}\n{programJson}"))).ToLowerInvariant()[..16];
        string source = GenerateSource(programJson, runtimeGeneration);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{CompilerIdentity}\n{scriptApiVersion}\n{JavaScriptTarget}\n{(int)permissions}\n{source}"))).ToLowerInvariant();
        ScriptManifest manifest = new(
            new ScriptModuleId(ModuleIdValue),
            "Automation runtime",
            $"1.0.0+{hash[..12]}",
            scriptApiVersion,
            Entrypoint,
            permissions,
            ScriptOwnerKind.AutomationRule,
            ScriptRuntimeProfile.GeneratedAutomation);
        CompiledScriptPackage package = new(
            manifest,
            [new CompiledScriptModule(Entrypoint, source, OriginalSourcePath: "automation-ir")],
            CompilerIdentity,
            hash);
        AutomationSourceMapEntry[] sourceMap = ordered
            .SelectMany(program => program.Actions.Select((_, index) => new AutomationSourceMapEntry(
                program.Id,
                program.Name,
                program.Type,
                ActionIndex: index)))
            .Concat(ordered.SelectMany(program => program.Conditions.Select((_, index) => new AutomationSourceMapEntry(
                program.Id,
                program.Name,
                program.Type,
                ConditionIndex: index))))
            .ToArray();
        return new CompiledAutomationArtifact(package, ordered, sourceMap, [], hash, runtimeGeneration);
    }


    private static string SerializeCanonical(IReadOnlyList<AutomationProgram> programs)
    {
        JsonElement element = JsonSerializer.SerializeToElement(programs, JsonOptions);
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = false }))
            WriteCanonical(writer, element);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                element.WriteTo(writer);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static void Validate(IReadOnlyList<AutomationProgram> programs)
    {
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        foreach (AutomationProgram program in programs)
        {
            if (string.IsNullOrWhiteSpace(program.Id)) throw new InvalidOperationException("Automation id is required.");
            if (!ids.Add(program.Id)) throw new InvalidOperationException($"Duplicate Automation id '{program.Id}'.");
            if (string.IsNullOrWhiteSpace(program.Name)) throw new InvalidOperationException($"Automation '{program.Id}' requires a name.");
            if (program.Actions.Count == 0) throw new InvalidOperationException($"Automation '{program.Id}' requires at least one action.");
            ValidateTrigger(program);
            foreach (AutomationCondition condition in program.Conditions) ValidateCondition(program, condition);
            foreach (AutomationAction action in program.Actions) ValidateAction(program, action);
        }
    }

    private static void ValidateTrigger(AutomationProgram program)
    {
        bool typeMatches = (program.Type, program.Trigger) switch
        {
            (AutomationProgramType.Alias, AliasAutomationTrigger) => true,
            (AutomationProgramType.TextTrigger, TextAutomationTrigger) => true,
            (AutomationProgramType.SemanticTrigger, SemanticAutomationTrigger) => true,
            (AutomationProgramType.Timer, TimerAutomationTrigger) => true,
            (AutomationProgramType.StateRule, StateAutomationTrigger) => true,
            _ => false
        };
        if (!typeMatches)
            throw new InvalidOperationException($"Automation '{program.Id}' type does not match its trigger.");

        switch (program.Trigger)
        {
            case AliasAutomationTrigger alias when string.IsNullOrWhiteSpace(alias.Pattern):
                throw new InvalidOperationException($"Alias '{program.Id}' requires a pattern.");
            case TextAutomationTrigger text when string.IsNullOrWhiteSpace(text.Pattern):
                throw new InvalidOperationException($"Text trigger '{program.Id}' requires a pattern.");
            case TextAutomationTrigger { MatchMode: HighlightMatchMode.Regex } regexTrigger:
                try
                {
                    _ = new Regex(
                        regexTrigger.Pattern,
                        RegexOptions.CultureInvariant | (regexTrigger.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase),
                        RegexTimeout);
                }
                catch (ArgumentException exception)
                {
                    throw new InvalidOperationException($"Text trigger '{program.Id}' contains an invalid regular expression: {exception.Message}", exception);
                }
                break;
            case SemanticAutomationTrigger semantic when string.IsNullOrWhiteSpace(semantic.EventName):
                throw new InvalidOperationException($"Semantic trigger '{program.Id}' requires an event name.");
            case TimerAutomationTrigger timer when timer.IntervalMilliseconds <= 0:
                throw new InvalidOperationException($"Timer '{program.Id}' requires a positive interval.");
            case StateAutomationTrigger state when string.IsNullOrWhiteSpace(state.Expression):
                throw new InvalidOperationException($"State rule '{program.Id}' requires an expression.");
        }
    }

    private static void ValidateCondition(AutomationProgram program, AutomationCondition condition)
    {
        switch (condition)
        {
            case StateExpressionAutomationCondition state when string.IsNullOrWhiteSpace(state.Expression):
                throw new InvalidOperationException($"Automation '{program.Id}' contains an empty state condition.");
            case EventFieldComparisonAutomationCondition comparison when
                string.IsNullOrWhiteSpace(comparison.Field) || !IsComparisonOperator(comparison.Operator):
                throw new InvalidOperationException($"Automation '{program.Id}' contains an invalid event comparison.");
            case RegexAutomationCondition regex when string.IsNullOrWhiteSpace(regex.Field) || string.IsNullOrWhiteSpace(regex.Pattern):
                throw new InvalidOperationException($"Automation '{program.Id}' contains an invalid regex condition.");
            case ContainsAutomationCondition contains when string.IsNullOrWhiteSpace(contains.Field):
                throw new InvalidOperationException($"Automation '{program.Id}' contains an invalid contains condition.");
            case StorageValueAutomationCondition storage when string.IsNullOrWhiteSpace(storage.Key) || !IsComparisonOperator(storage.Operator):
                throw new InvalidOperationException($"Automation '{program.Id}' contains an invalid storage comparison.");
            case AllAutomationCondition all when all.Conditions.Count == 0:
                throw new InvalidOperationException($"Automation '{program.Id}' contains an empty AND condition.");
            case AnyAutomationCondition any when any.Conditions.Count == 0:
                throw new InvalidOperationException($"Automation '{program.Id}' contains an empty OR condition.");
        }

        if (condition is AllAutomationCondition allConditions)
            foreach (AutomationCondition nested in allConditions.Conditions) ValidateCondition(program, nested);
        else if (condition is AnyAutomationCondition anyConditions)
            foreach (AutomationCondition nested in anyConditions.Conditions) ValidateCondition(program, nested);
        else if (condition is NotAutomationCondition not)
            ValidateCondition(program, not.Condition);
    }

    private static bool IsComparisonOperator(string value) =>
        value is "==" or "!=" or "<" or ">" or "<=" or ">=";

    private static void ValidateAction(AutomationProgram program, AutomationAction action)
    {
        switch (action)
        {
            case SendCommandAutomationAction send when string.IsNullOrWhiteSpace(send.Command):
                throw new InvalidOperationException($"Automation '{program.Id}' contains an empty command.");
            case SendCommandsAutomationAction batch when batch.Commands.Count == 0 || batch.Commands.Any(string.IsNullOrWhiteSpace):
                throw new InvalidOperationException($"Automation '{program.Id}' contains an invalid command batch.");
            case DelayAutomationAction delay when delay.Milliseconds < 0:
                throw new InvalidOperationException($"Automation '{program.Id}' contains a negative delay.");
            case LogAutomationAction log when string.IsNullOrWhiteSpace(log.Message):
                throw new InvalidOperationException($"Automation '{program.Id}' contains an empty log message.");
            case SetStorageAutomationAction storage when string.IsNullOrWhiteSpace(storage.Key):
                throw new InvalidOperationException($"Automation '{program.Id}' contains an empty storage key.");
            case DeleteStorageAutomationAction storage when string.IsNullOrWhiteSpace(storage.Key):
                throw new InvalidOperationException($"Automation '{program.Id}' contains an empty storage key.");
        }
    }

    private static ScriptCapability DerivePermissions(IEnumerable<AutomationProgram> programs)
    {
        AutomationProgram[] snapshot = programs.ToArray();
        ScriptCapability capabilities = snapshot.Length == 0
            ? ScriptCapability.None
            : ScriptCapability.ReadState | ScriptCapability.Log;
        foreach (AutomationProgram program in snapshot)
        {
            capabilities |= program.Trigger switch
            {
                TimerAutomationTrigger => ScriptCapability.CreateTimers,
                StateAutomationTrigger => ScriptCapability.SubscribeEvents | ScriptCapability.ReadState,
                _ => ScriptCapability.SubscribeEvents
            };
            foreach (AutomationCondition condition in program.Conditions)
                capabilities |= ConditionPermissions(condition);
            foreach (AutomationAction action in program.Actions)
            {
                capabilities |= action switch
                {
                    SendCommandAutomationAction or SendCommandsAutomationAction => ScriptCapability.SendCommands,
                    DelayAutomationAction => ScriptCapability.CreateTimers,
                    LogAutomationAction => ScriptCapability.Log,
                    SetStorageAutomationAction or DeleteStorageAutomationAction => ScriptCapability.WriteScriptStorage,
                    _ => ScriptCapability.None
                };
            }
        }
        return capabilities;
    }

    private static ScriptCapability ConditionPermissions(AutomationCondition condition) => condition switch
    {
        StateExpressionAutomationCondition => ScriptCapability.ReadState,
        StorageValueAutomationCondition => ScriptCapability.ReadScriptStorage,
        AllAutomationCondition all => all.Conditions.Aggregate(ScriptCapability.None, (current, item) => current | ConditionPermissions(item)),
        AnyAutomationCondition any => any.Conditions.Aggregate(ScriptCapability.None, (current, item) => current | ConditionPermissions(item)),
        NotAutomationCondition not => ConditionPermissions(not.Condition),
        _ => ScriptCapability.None
    };

    private static string GenerateSource(string programJson, string runtimeGeneration) => $$"""
import { nex } from "@nexmud/api";

const runtimeGeneration = "{{runtimeGeneration}}";
const programs = Object.freeze({{programJson}});
const subscriptions = [];
const timers = [];
const byId = new Map(programs.map(program => [program.id, program]));

function valueAt(root, path) {
  if (!path) return root;
  return String(path).split('.').reduce((value, key) => value == null ? null : value[key], root);
}

function compare(left, right, op) {
  if (left == null || right == null) {
    const equal = left == null && right == null;
    return op === '==' ? equal : op === '!=' ? !equal : false;
  }
  const leftNumber = Number(left);
  const rightNumber = Number(right);
  let comparison;
  if (Number.isFinite(leftNumber) && Number.isFinite(rightNumber) && String(left).trim() !== '' && String(right).trim() !== '') {
    comparison = leftNumber === rightNumber ? 0 : leftNumber < rightNumber ? -1 : 1;
  } else {
    comparison = String(left).localeCompare(String(right), undefined, { sensitivity: 'accent' });
  }
  switch (op) {
    case '==': return comparison === 0;
    case '!=': return comparison !== 0;
    case '<': return comparison < 0;
    case '>': return comparison > 0;
    case '<=': return comparison <= 0;
    case '>=': return comparison >= 0;
    default: return false;
  }
}

async function conditionPasses(condition, context, program) {
  switch (condition.kind) {
    case 'stateExpression': return context?.conditionPassed === true;
    case 'eventFieldComparison': return compare(valueAt(context, condition.field), condition.value, condition.operator);
    case 'regex': {
      const flags = condition.caseSensitive ? '' : 'i';
      return new RegExp(condition.pattern, flags).test(String(valueAt(context, condition.field) ?? ''));
    }
    case 'contains': {
      let actual = String(valueAt(context, condition.field) ?? '');
      let expected = String(condition.value ?? '');
      if (!condition.caseSensitive) { actual = actual.toLowerCase(); expected = expected.toLowerCase(); }
      return actual.includes(expected);
    }
    case 'storageValue': return compare(await nex.storage.get(storageKey(program, condition.key)), condition.value, condition.operator);
    case 'all': {
      for (const nested of condition.conditions ?? []) if (!await conditionPasses(nested, context, program)) return false;
      return true;
    }
    case 'any': {
      for (const nested of condition.conditions ?? []) if (await conditionPasses(nested, context, program)) return true;
      return false;
    }
    case 'not': return !await conditionPasses(condition.condition, context, program);
    default: return true;
  }
}

function render(template, context) {
  let value = String(template ?? '');
  const captures = context?.captures ?? [];
  value = value.replaceAll('$*', String(context?.tail ?? ''));
  for (let index = 1; index <= 9; index++) value = value.replaceAll(`$${index}`, String(captures[index] ?? ''));
  value = value.replace(/\$\{([^}]+)\}/g, (match, key) => {
    const resolved = context?.values?.[String(key).trim()];
    return resolved == null ? match : String(resolved);
  });
  return value;
}

function storageKey(program, key) { return `automation:${program.id}:${String(key ?? '')}`; }
function commandOptions(program, context) {
  return {
    reason: `Automation: ${program.name}`,
    automationId: program.id,
    automationType: program.type,
    triggerId: String(context?.triggerId ?? program.id)
  };
}
function logData(program, context, extra = {}) {
  return { automationId: program.id, automationName: program.name, automationType: program.type, triggerId: context?.triggerId ?? program.id, ...extra };
}

async function executeAction(program, action, context, actionIndex) {
  await nex.log.debug('Automation action started', logData(program, context, { actionIndex, actionKind: action.kind }));
  switch (action.kind) {
    case 'sendCommand':
      await nex.commands.send(render(action.command, context), commandOptions(program, context));
      break;
    case 'sendCommands':
      for (const command of action.commands ?? []) await nex.commands.send(render(command, context), commandOptions(program, context));
      break;
    case 'delay':
      await nex.timers.delay(Number(action.milliseconds ?? 0));
      break;
    case 'log': {
      const level = String(action.level ?? 'info').toLowerCase();
      const writer = nex.log[level] ?? nex.log.info;
      await writer(render(action.message, context), logData(program, context, { actionIndex }));
      break;
    }
    case 'setStorage':
      await nex.storage.set(storageKey(program, action.key), action.value ?? null);
      break;
    case 'deleteStorage':
      await nex.storage.delete(storageKey(program, action.key));
      break;
    default:
      throw new Error(`Unsupported Automation action '${action.kind}'.`);
  }
  await nex.log.debug('Automation action completed', logData(program, context, { actionIndex, actionKind: action.kind }));
}

async function invoke(program, context = {}) {
  let conditionIndex = null;
  let actionIndex = null;
  try {
    const snapshot = nex.state.snapshot();
    if (!snapshot.connected || String(snapshot.inputMode ?? '').toLowerCase() !== 'normal') return;
    for (let index = 0; index < (program.conditions ?? []).length; index++) {
      conditionIndex = index;
      if (!await conditionPasses(program.conditions[index], context, program)) {
        await nex.log.debug('Automation condition failed', logData(program, context, { conditionIndex: index }));
        return;
      }
      await nex.log.debug('Automation condition passed', logData(program, context, { conditionIndex: index }));
    }
    conditionIndex = null;
    for (let index = 0; index < (program.actions ?? []).length; index++) {
      actionIndex = index;
      await executeAction(program, program.actions[index], context, index);
    }
  } catch (error) {
    await nex.log.error(
      error?.message ?? 'Automation execution failed.',
      logData(program, context, {
        conditionIndex,
        actionIndex,
        actionKind: actionIndex == null ? null : program.actions?.[actionIndex]?.kind ?? null
      }));
  }
}

function dispatchMatched(event) {
  if (String(event?.runtimeGeneration ?? '') !== runtimeGeneration) return;
  const program = byId.get(String(event?.automationId ?? ''));
  if (!program) return;
  return invoke(program, event);
}

export function activate() {
  if (programs.some(program => program.trigger.kind === 'alias'))
    subscriptions.push(nex.events.on('automation.aliasMatched', dispatchMatched));
  if (programs.some(program => program.trigger.kind === 'text'))
    subscriptions.push(nex.events.on('automation.textTriggerMatched', dispatchMatched));
  if (programs.some(program => program.trigger.kind === 'state'))
    subscriptions.push(nex.events.on('automation.stateRuleMatched', dispatchMatched));

  const semanticNames = [...new Set(programs.filter(program => program.trigger.kind === 'semantic').map(program => program.trigger.eventName))].sort();
  for (const eventName of semanticNames) {
    subscriptions.push(nex.events.on(eventName, async event => {
      for (const program of programs) {
        if (program.trigger.kind === 'semantic' && program.trigger.eventName === eventName) await invoke(program, event);
      }
    }));
  }

  for (const program of programs) {
    if (program.trigger.kind !== 'timer') continue;
    const callback = async () => {
      const context = { triggerId: `timer:${program.id}` };
      await nex.log.debug('Automation timer fired', logData(program, context));
      await invoke(program, context);
    };
    timers.push(program.trigger.repeat
      ? nex.timers.every(program.trigger.intervalMilliseconds, callback)
      : nex.timers.after(program.trigger.intervalMilliseconds, callback));
  }
}

export function deactivate() {
  for (const subscription of subscriptions.splice(0)) nex.events.off(subscription);
  for (const timer of timers.splice(0)) nex.timers.cancel(timer);
}
""";
}
