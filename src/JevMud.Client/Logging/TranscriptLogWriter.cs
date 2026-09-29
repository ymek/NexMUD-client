using System.Text;
using System.Text.Json;
using JevMud.Client.Settings;
using JevMud.Transport.Text;

namespace JevMud.Client.Logging;

public sealed class TranscriptLogWriter : IDisposable
{
    private readonly object _gate = new();
    private readonly AnsiTextParser _ansi = new();
    private StreamWriter? _writer;
    private TranscriptLogFormat _format = TranscriptLogFormat.AnsiText;

    public bool IsActive
    {
        get
        {
            lock (_gate)
            {
                return _writer is not null;
            }
        }
    }

    public string? CurrentPath { get; private set; }
    public TranscriptLogFormat Format => _format;

    public string Start(string directory, string host, TranscriptLogFormat format = TranscriptLogFormat.AnsiText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        lock (_gate)
        {
            StopCore();
            Directory.CreateDirectory(directory);
            string safeHost = string.Concat(host.Select(value =>
                char.IsLetterOrDigit(value) || value is '.' or '-' or '_' ? value : '_'));
            string timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string extension = format switch
            {
                TranscriptLogFormat.JsonLines => ".jsonl",
                TranscriptLogFormat.PlainText => ".txt",
                _ => ".ansi.log"
            };
            string path = Path.Combine(directory, $"{timestamp}_{safeHost}{extension}");
            _writer = new StreamWriter(path, false, new UTF8Encoding(false))
            {
                AutoFlush = true
            };
            _format = format;
            CurrentPath = path;
            return path;
        }
    }

    public void Write(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_gate)
        {
            if (_writer is null)
            {
                return;
            }

            switch (_format)
            {
                case TranscriptLogFormat.AnsiText:
                    _writer.Write(text);
                    break;
                case TranscriptLogFormat.PlainText:
                    _writer.Write(string.Concat(_ansi.Process(text).Select(segment => segment.Text)));
                    break;
                case TranscriptLogFormat.JsonLines:
                    string json = JsonSerializer.Serialize(new
                    {
                        timestamp = DateTimeOffset.UtcNow,
                        source = "server",
                        text
                    });
                    _writer.WriteLine(json);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown log format {_format}.");
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            StopCore();
        }
    }

    public void Dispose() => Stop();

    private void StopCore()
    {
        _writer?.Dispose();
        _writer = null;
        CurrentPath = null;
        _ansi.Reset();
    }
}
