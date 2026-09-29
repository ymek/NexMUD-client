using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using JevMud.Contracts.Events;
using JevMud.Contracts.Transport;
using JevMud.Core.Actions;
using JevMud.Core.Events;
using JevMud.Transport.Telnet;

namespace JevMud.Transport;

public sealed class TcpMudTransport : ICommandSender, IAsyncDisposable
{
    private readonly IEventSink _events;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _activeProtocols = new(StringComparer.OrdinalIgnoreCase);
    private TcpClient? _client;
    private Stream? _stream;
    private CancellationTokenSource? _connectionCts;
    private Task? _readLoop;
    private MudConnectionOptions? _options;
    private volatile bool _nawsNegotiated;
    private int _terminalColumns = 120;
    private int _terminalRows = 40;
    private bool _disposed;

    public TcpMudTransport(IEventSink events)
    {
        _events = events;
    }

    public bool IsConnected => _client is not null && _stream is not null;
    public IReadOnlyCollection<string> ActiveProtocols => _activeProtocols.Keys.ToArray();

    public bool IsProtocolActive(string protocol) =>
        !string.IsNullOrWhiteSpace(protocol) && _activeProtocols.ContainsKey(protocol);

    public async Task ConnectAsync(MudConnectionOptions options, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnected)
            {
                throw new InvalidOperationException("Already connected.");
            }

            await _events.PublishAsync(
                new ConnectionStateChanged(ConnectionStatus.Connecting, options.Host, options.Port),
                "transport",
                cancellationToken).ConfigureAwait(false);

            TcpClient client = new();
            Stream? stream = null;
            CancellationTokenSource? connectionCts = null;
            try
            {
                await client.ConnectAsync(options.Host, options.Port, cancellationToken).ConfigureAwait(false);
                stream = client.GetStream();

                if (options.UseTls)
                {
                    SslStream ssl = new(stream, leaveInnerStreamOpen: false);
                    await ssl.AuthenticateAsClientAsync(options.Host).WaitAsync(cancellationToken).ConfigureAwait(false);
                    stream = ssl;
                }

                connectionCts = new CancellationTokenSource();
                _client = client;
                _stream = stream;
                _options = options;
                _connectionCts = connectionCts;
                _nawsNegotiated = false;
                _activeProtocols.Clear();
                Volatile.Write(ref _terminalColumns, options.Columns);
                Volatile.Write(ref _terminalRows, options.Rows);

                await _events.PublishAsync(
                    new ConnectionStateChanged(ConnectionStatus.Connected, options.Host, options.Port),
                    "transport",
                    CancellationToken.None).ConfigureAwait(false);

                _readLoop = Task.Run(
                    () => ReadLoopAsync(client, stream, options, connectionCts, connectionCts.Token),
                    CancellationToken.None);
            }
            catch
            {
                if (ReferenceEquals(_client, client))
                {
                    _client = null;
                    _stream = null;
                    _options = null;
                    _connectionCts = null;
                    _nawsNegotiated = false;
                }

                connectionCts?.Cancel();
                connectionCts?.Dispose();
                stream?.Dispose();
                client.Dispose();
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await _events.PublishAsync(
                new ConnectionStateChanged(ConnectionStatus.Disconnected, options.Host, options.Port, "Connection cancelled."),
                "transport",
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            await _events.PublishAsync(
                new TransportError(exception.Message),
                "transport",
                CancellationToken.None).ConfigureAwait(false);
            await _events.PublishAsync(
                new ConnectionStateChanged(ConnectionStatus.Disconnected, options.Host, options.Port, exception.Message),
                "transport",
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task DisconnectAsync(string reason = "Disconnected by user.")
    {
        TcpClient? client;
        Stream? stream;
        CancellationTokenSource? connectionCts;
        Task? readLoop;
        MudConnectionOptions? options;

        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            client = _client;
            stream = _stream;
            connectionCts = _connectionCts;
            readLoop = _readLoop;
            options = _options;

            _client = null;
            _stream = null;
            _connectionCts = null;
            _readLoop = null;
            _options = null;
            _nawsNegotiated = false;
            _activeProtocols.Clear();
        }
        finally
        {
            _lifecycle.Release();
        }

        connectionCts?.Cancel();
        stream?.Dispose();
        client?.Dispose();

        if (readLoop is not null)
        {
            try
            {
                await readLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                // Read-loop failures are published by the loop itself.
            }
        }

        connectionCts?.Dispose();

        if (client is not null || stream is not null || options is not null)
        {
            await _events.PublishAsync(
                new ConnectionStateChanged(ConnectionStatus.Disconnected, options?.Host, options?.Port, reason),
                "transport",
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task SendCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        Stream stream = _stream ?? throw new InvalidOperationException("Not connected.");
        byte[] bytes = Encoding.UTF8.GetBytes(command.TrimEnd('\r', '\n') + "\r\n");

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(stream, _stream))
            {
                throw new InvalidOperationException("Connection changed before the command could be sent.");
            }

            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }


    public async Task SendGmcpAsync(string module, string? payload = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(module) || module.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("GMCP module is required and cannot contain whitespace.", nameof(module));
        }
        Stream stream = _stream ?? throw new InvalidOperationException("Not connected.");
        if (!IsProtocolActive("GMCP")) throw new InvalidOperationException("GMCP is not negotiated for this connection.");

        await WriteProtocolAsync(stream, TelnetFrameBuilder.Gmcp(module.Trim(), payload), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SendMsdpAsync(string variable, string value, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(variable)) throw new ArgumentException("MSDP variable is required.", nameof(variable));
        Stream stream = _stream ?? throw new InvalidOperationException("Not connected.");
        if (!IsProtocolActive("MSDP")) throw new InvalidOperationException("MSDP is not negotiated for this connection.");

        byte[] frame = TelnetFrameBuilder.Subnegotiation(
            TelnetProtocol.Msdp,
            [
                TelnetProtocol.MsdpVar,
                .. Encoding.UTF8.GetBytes(variable.Trim()),
                TelnetProtocol.MsdpVal,
                .. Encoding.UTF8.GetBytes(value ?? string.Empty)
            ]);
        await WriteProtocolAsync(stream, frame, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateTerminalSizeAsync(ushort columns, ushort rows, CancellationToken cancellationToken = default)
    {
        if (columns == 0) throw new ArgumentOutOfRangeException(nameof(columns));
        if (rows == 0) throw new ArgumentOutOfRangeException(nameof(rows));

        Volatile.Write(ref _terminalColumns, columns);
        Volatile.Write(ref _terminalRows, rows);

        Stream? stream = _stream;
        if (stream is null || !_nawsNegotiated) return;

        await WriteProtocolAsync(stream, TelnetFrameBuilder.Naws(columns, rows), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(
        TcpClient client,
        Stream stream,
        MudConnectionOptions options,
        CancellationTokenSource connectionCts,
        CancellationToken cancellationToken)
    {
        TelnetParser telnet = new(options);
        Decoder decoder = Encoding.UTF8.GetDecoder();
        byte[] buffer = new byte[16 * 1024];
        Stream readStream = stream;
        ZLibStream? compressionStream = null;
        string? disconnectReason = null;
        Exception? failure = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int read = await readStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    if (compressionStream is not null)
                    {
                        compressionStream.Dispose();
                        compressionStream = null;
                        readStream = stream;
                        await _events.PublishAsync(
                            new ProtocolStateChanged("MCCP2", true, "Compression stream ended; option remains negotiated"),
                            "transport.mccp2",
                            cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    disconnectReason = "Remote host closed the connection.";
                    break;
                }

                TelnetParseResult result = telnet.Process(buffer.AsSpan(0, read));

                foreach (byte[] response in result.Responses)
                {
                    await WriteProtocolAsync(stream, response, cancellationToken).ConfigureAwait(false);
                }

                foreach (TelnetProtocolTransition transition in result.ProtocolTransitions)
                {
                    if (transition.Enabled) _activeProtocols[transition.Protocol] = 0;
                    else _activeProtocols.TryRemove(transition.Protocol, out _);

                    if (transition.Protocol.Equals("NAWS", StringComparison.OrdinalIgnoreCase))
                    {
                        _nawsNegotiated = transition.Enabled;
                        if (transition.Enabled)
                        {
                            ushort columns = checked((ushort)Math.Clamp(Volatile.Read(ref _terminalColumns), 1, ushort.MaxValue));
                            ushort rows = checked((ushort)Math.Clamp(Volatile.Read(ref _terminalRows), 1, ushort.MaxValue));
                            if (columns != options.Columns || rows != options.Rows)
                            {
                                await WriteProtocolAsync(stream, TelnetFrameBuilder.Naws(columns, rows), cancellationToken)
                                    .ConfigureAwait(false);
                            }
                        }
                    }

                    await _events.PublishAsync(
                        new ProtocolStateChanged(transition.Protocol, transition.Enabled, transition.Detail),
                        $"transport.{transition.Protocol.ToLowerInvariant()}",
                        cancellationToken).ConfigureAwait(false);
                }

                foreach (TelnetProtocolIssue issue in result.Issues)
                {
                    await _events.PublishAsync(
                        new ProtocolError(issue.Protocol, issue.Message, issue.Fatal),
                        $"transport.{issue.Protocol.ToLowerInvariant()}",
                        cancellationToken).ConfigureAwait(false);
                }

                foreach (string boundary in result.PromptBoundaries)
                {
                    await _events.PublishAsync(
                        new ProtocolPromptBoundaryReceived(boundary),
                        "transport.telnet",
                        cancellationToken).ConfigureAwait(false);
                }

                foreach (GmcpFrame frame in result.GmcpFrames)
                {
                    await _events.PublishAsync(
                        new GmcpMessageReceived(frame.Module, frame.Payload),
                        "transport.gmcp",
                        cancellationToken).ConfigureAwait(false);
                }

                foreach (MsdpFrame frame in result.MsdpFrames)
                {
                    await _events.PublishAsync(new MsdpMessageReceived(frame.Values), "transport.msdp", cancellationToken)
                        .ConfigureAwait(false);
                }

                foreach (MsspFrame frame in result.MsspFrames)
                {
                    await _events.PublishAsync(new MsspMessageReceived(frame.Values), "transport.mssp", cancellationToken)
                        .ConfigureAwait(false);
                }

                if (result.Mccp2Started && compressionStream is null)
                {
                    Stream compressedSource = new PrefixReadStream(result.CompressedRemainder ?? Array.Empty<byte>(), stream);
                    compressionStream = new ZLibStream(compressedSource, CompressionMode.Decompress, leaveOpen: false);
                    readStream = compressionStream;
                    _activeProtocols["MCCP2"] = 0;
                    await _events.PublishAsync(new ProtocolStateChanged("MCCP2", true, "Compression active"), "transport.mccp2", cancellationToken)
                        .ConfigureAwait(false);
                }

                if (result.TextBytes.Length > 0)
                {
                    char[] chars = new char[Encoding.UTF8.GetMaxCharCount(result.TextBytes.Length)];
                    decoder.Convert(
                        result.TextBytes,
                        chars,
                        flush: false,
                        out _,
                        out int charsUsed,
                        out _);

                    if (charsUsed > 0)
                    {
                        string text = new(chars, 0, charsUsed);
                        await _events.PublishAsync(new TextReceived(text), "transport.text", cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (InvalidDataException exception) when (compressionStream is not null)
        {
            failure = exception;
            disconnectReason = $"MCCP2 decompression failed: {exception.Message}";
            await _events.PublishAsync(
                new ProtocolError("MCCP2", exception.Message, Fatal: true),
                "transport.mccp2",
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
            disconnectReason = exception.Message;
        }
        finally
        {
            compressionStream?.Dispose();
            await FinalizeReadLoopAsync(
                client,
                stream,
                options,
                connectionCts,
                disconnectReason,
                failure).ConfigureAwait(false);
        }
    }

    private async Task FinalizeReadLoopAsync(
        TcpClient client,
        Stream stream,
        MudConnectionOptions options,
        CancellationTokenSource connectionCts,
        string? disconnectReason,
        Exception? failure)
    {
        bool ownsConnection = false;

        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(_stream, stream))
            {
                ownsConnection = true;
                _client = null;
                _stream = null;
                _connectionCts = null;
                _options = null;
                _readLoop = null;
                _nawsNegotiated = false;
                _activeProtocols.Clear();
            }
        }
        finally
        {
            _lifecycle.Release();
        }

        if (!ownsConnection)
        {
            return;
        }

        stream.Dispose();
        client.Dispose();
        connectionCts.Dispose();

        if (failure is not null)
        {
            await _events.PublishAsync(new TransportError(failure.Message), "transport", CancellationToken.None)
                .ConfigureAwait(false);
        }

        await _events.PublishAsync(
            new ConnectionStateChanged(
                ConnectionStatus.Disconnected,
                options.Host,
                options.Port,
                disconnectReason ?? "Connection ended."),
            "transport",
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task WriteProtocolAsync(Stream stream, byte[] bytes, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(stream, _stream))
            {
                return;
            }

            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await DisconnectAsync("Client shutting down.").ConfigureAwait(false);
        _lifecycle.Dispose();
        _sendLock.Dispose();
    }

    private sealed class PrefixReadStream : Stream
    {
        private readonly byte[] _prefix;
        private readonly Stream _inner;
        private int _offset;

        public PrefixReadStream(byte[] prefix, Stream inner)
        {
            _prefix = prefix;
            _inner = inner;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_offset < _prefix.Length)
            {
                int copied = Math.Min(count, _prefix.Length - _offset);
                Array.Copy(_prefix, _offset, buffer, offset, copied);
                _offset += copied;
                return copied;
            }
            return _inner.Read(buffer, offset, count);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset < _prefix.Length)
            {
                int copied = Math.Min(buffer.Length, _prefix.Length - _offset);
                _prefix.AsMemory(_offset, copied).CopyTo(buffer);
                _offset += copied;
                return copied;
            }
            return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            // The wrapper owns only its prefix. The transport owns the network stream so MCCP2
            // can end cleanly and the Telnet session can continue uncompressed.
            base.Dispose(disposing);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

}
