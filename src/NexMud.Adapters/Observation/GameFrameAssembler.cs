using System.Collections.ObjectModel;
using System.Text;
using NexMud.Contracts.Gameplay;
using NexMud.Transport.Text;

namespace NexMud.Adapters.Observation;

/// <summary>
/// Assembles decoded transport text into immutable, ordered gameplay observations.
/// TCP reads are deliberately not treated as logical game frames.
/// </summary>
public readonly record struct GameFrameBoundary(int Start, int Length);

public sealed class GameFrameAssembler
{
    private readonly AnsiTextParser _ansi = new();
    private readonly StringBuilder _buffer = new();
    private readonly Func<string, GameFrameBoundary?>? _promptBoundaryDetector;
    private long _sequence;
    private SessionId _sessionId = SessionId.New();
    private bool _pendingCarriageReturn;

    public GameFrameAssembler(Func<string, GameFrameBoundary?>? promptBoundaryDetector = null)
    {
        _promptBoundaryDetector = promptBoundaryDetector;
    }

    public SessionId SessionId => _sessionId;

    public IReadOnlyList<GameObservation> AppendText(
        string rawText,
        DateTimeOffset receivedAt,
        ObservationMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(rawText);
        if (rawText.Length == 0)
        {
            return Array.Empty<GameObservation>();
        }

        List<GameObservation> observations = [];
        foreach (char value in rawText)
        {
            if (_pendingCarriageReturn)
            {
                if (value == '\n')
                {
                    _buffer.Append(value);
                    _pendingCarriageReturn = false;
                    EmitBuffered(observations, receivedAt, ObservationKind.Text, metadata);
                    continue;
                }

                _pendingCarriageReturn = false;
                EmitBuffered(observations, receivedAt, ObservationKind.Text, metadata);
            }

            _buffer.Append(value);
            if (value == '\r')
            {
                _pendingCarriageReturn = true;
                continue;
            }

            if (value == '\n')
            {
                EmitBuffered(observations, receivedAt, ObservationKind.Text, metadata);
                continue;
            }

            EmitDetectedPrompts(observations, receivedAt, metadata);
        }

        return new ReadOnlyCollection<GameObservation>(observations.ToArray());
    }

    public IReadOnlyList<GameObservation> FlushPromptCandidate(
        DateTimeOffset receivedAt,
        ObservationMetadata? metadata = null)
    {
        List<GameObservation> observations = [];
        _pendingCarriageReturn = false;
        EmitBuffered(observations, receivedAt, ObservationKind.PromptCandidate, metadata);
        return new ReadOnlyCollection<GameObservation>(observations.ToArray());
    }

    public IReadOnlyList<GameObservation> FlushText(
        DateTimeOffset receivedAt,
        ObservationMetadata? metadata = null)
    {
        List<GameObservation> observations = [];
        _pendingCarriageReturn = false;
        EmitBuffered(observations, receivedAt, ObservationKind.Text, metadata);
        return new ReadOnlyCollection<GameObservation>(observations.ToArray());
    }

    public GameObservation CreateEvidence(
        string text,
        DateTimeOffset receivedAt,
        ObservationKind kind,
        ObservationMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return CreateObservation(text, receivedAt, kind, metadata ?? new ObservationMetadata());
    }

    public void BeginSession(SessionId? sessionId = null)
    {
        _sessionId = sessionId ?? SessionId.New();
        Interlocked.Exchange(ref _sequence, 0);
        _pendingCarriageReturn = false;
        _buffer.Clear();
        _ansi.Reset();
    }

    private void EmitDetectedPrompts(
        List<GameObservation> observations,
        DateTimeOffset receivedAt,
        ObservationMetadata? metadata)
    {
        if (_promptBoundaryDetector is null || _buffer.Length == 0)
        {
            return;
        }

        while (_buffer.Length > 0)
        {
            GameFrameBoundary? detected = _promptBoundaryDetector(_buffer.ToString());
            if (detected is null)
            {
                return;
            }

            GameFrameBoundary boundary = detected.Value;
            if (boundary.Start < 0 || boundary.Length <= 0 ||
                boundary.Start + boundary.Length > _buffer.Length)
            {
                throw new InvalidOperationException("Prompt boundary detector returned an invalid frame range.");
            }

            if (boundary.Start > 0)
            {
                string prefix = _buffer.ToString(0, boundary.Start);
                _buffer.Remove(0, boundary.Start);
                observations.Add(CreateObservation(
                    prefix,
                    receivedAt,
                    ObservationKind.Text,
                    metadata ?? new ObservationMetadata()));
                boundary = boundary with { Start = 0 };
            }

            string prompt = _buffer.ToString(0, boundary.Length);
            _buffer.Remove(0, boundary.Length);
            observations.Add(CreateObservation(
                prompt,
                receivedAt,
                ObservationKind.PromptCandidate,
                metadata ?? new ObservationMetadata(IsPrompt: true)));
        }
    }

    private void EmitBuffered(
        List<GameObservation> observations,
        DateTimeOffset receivedAt,
        ObservationKind kind,
        ObservationMetadata? metadata)
    {
        if (_buffer.Length == 0)
        {
            return;
        }

        string raw = _buffer.ToString();
        _buffer.Clear();
        observations.Add(CreateObservation(
            raw,
            receivedAt,
            kind,
            metadata ?? new ObservationMetadata(IsPrompt: kind == ObservationKind.PromptCandidate)));
    }

    private GameObservation CreateObservation(
        string rawText,
        DateTimeOffset receivedAt,
        ObservationKind kind,
        ObservationMetadata metadata)
    {
        IReadOnlyList<AnsiTextSegment> segments = _ansi.Process(rawText);
        GameAnsiRun[] runs = segments.Select(segment => new GameAnsiRun(
            segment.Text,
            ConvertStyle(segment.Style))).ToArray();
        string plainText = string.Concat(runs.Select(run => run.Text));

        return new GameObservation(
            Interlocked.Increment(ref _sequence),
            receivedAt,
            _sessionId,
            kind,
            rawText,
            plainText,
            new ReadOnlyCollection<GameAnsiRun>(runs),
            metadata);
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
