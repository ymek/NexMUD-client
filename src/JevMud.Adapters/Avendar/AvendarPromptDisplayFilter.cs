namespace JevMud.Adapters.Avendar;

/// <summary>
/// Optional presentation-only filter for Avendar's [J|...] telemetry prompt.
/// Parsing and raw logging remain independent consumers of the original server stream.
/// </summary>
public sealed class AvendarPromptDisplayFilter
{
    private readonly AvendarPromptStreamProcessor _processor = new();

    public void Reset() => _processor.Reset();

    public string Process(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return string.Concat(_processor.Process(text)
            .OfType<AvendarDisplayToken>()
            .Select(token => token.Text));
    }
}
