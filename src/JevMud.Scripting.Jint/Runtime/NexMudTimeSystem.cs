using System.Globalization;
using Jint.Runtime;
using JevMud.Scripting.Scheduling;

namespace JevMud.Scripting.Jint.Runtime;

/// <summary>
/// Routes JavaScript wall-clock reads through NexMUD's scripting clock. Parsing/offset behavior is
/// delegated to Jint's implementation, while Date.now()/new Date() observe the same clock used by
/// timers and event-journal replay.
/// </summary>
internal sealed class NexMudTimeSystem : DefaultTimeSystem
{
    private readonly IScriptScheduler _clock;

    public NexMudTimeSystem(IScriptScheduler clock)
        : base(TimeZoneInfo.Utc, CultureInfo.InvariantCulture) =>
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public override DateTimeOffset GetUtcNow() => _clock.UtcNow;
}
