using System.Collections.ObjectModel;
using NexMud.Contracts.Gameplay;
using NexMud.Transport.Text;

namespace NexMud.Adapters.Avendar;

/// <summary>
/// Creates immutable source observations from the decoded Avendar stream.
/// The observation sequence is session-local and independent of the MUD clock.
/// </summary>
public sealed class AvendarObservationFactory
{
    private readonly AnsiTextParser _ansi = new();
    private long _sequence;
    private string _sessionId = Guid.NewGuid().ToString("N");

    public string SessionId => _sessionId;

    public GameObservation Create(
        string rawText,
        DateTimeOffset receivedAt,
        string? sessionId = null,
        ObservationKind kind = ObservationKind.Text,
        ObservationMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(rawText);

        IReadOnlyList<AnsiTextSegment> segments = _ansi.Process(rawText);
        GameAnsiRun[] runs = segments.Select(segment => new GameAnsiRun(
            segment.Text,
            ConvertStyle(segment.Style))).ToArray();
        string plainText = string.Concat(runs.Select(run => run.Text));

        return new GameObservation(
            Interlocked.Increment(ref _sequence),
            receivedAt,
            sessionId ?? _sessionId,
            kind,
            rawText,
            plainText,
            new ReadOnlyCollection<GameAnsiRun>(runs),
            metadata ?? new ObservationMetadata());
    }

    public GameObservation CreateEvidence(
        string text,
        DateTimeOffset receivedAt,
        ObservationKind kind,
        ObservationMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(metadata);

        IReadOnlyList<GameAnsiRun> runs = text.Length == 0
            ? Array.Empty<GameAnsiRun>()
            : new ReadOnlyCollection<GameAnsiRun>(
                new[] { new GameAnsiRun(text, GameAnsiStyle.Default) });
        return new GameObservation(
            Interlocked.Increment(ref _sequence),
            receivedAt,
            _sessionId,
            kind,
            text,
            text,
            runs,
            metadata);
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _sequence, 0);
        _sessionId = Guid.NewGuid().ToString("N");
        _ansi.Reset();
    }

    private static GameAnsiStyle ConvertStyle(AnsiTextStyle style) => new(
        ConvertColor(style.Foreground),
        ConvertColor(style.Background),
        style.Bold,
        style.Faint,
        style.Italic,
        style.Underline,
        style.Blink,
        style.Reverse,
        style.Strikethrough);

    private static GameAnsiColor? ConvertColor(AnsiColor? color) =>
        color is null ? null : new GameAnsiColor(color.Red, color.Green, color.Blue);
}
