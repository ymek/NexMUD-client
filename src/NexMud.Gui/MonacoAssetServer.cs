using System.Net;
using System.Net.Sockets;

namespace NexMud.Gui;

/// <summary>
/// Serves the bundled Monaco assets over loopback HTTP. Monaco lazy-loads language tokenizers
/// and workers via dynamic import, which WebKit blocks for file:// pages (symptom: no syntax
/// highlighting, language-service timeouts). Binds to 127.0.0.1 only and serves a single root.
/// </summary>
internal sealed class MonacoAssetServer : IDisposable
{
    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8", [".js"] = "text/javascript; charset=utf-8", [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8", [".json"] = "application/json; charset=utf-8", [".ttf"] = "font/ttf",
        [".woff"] = "font/woff", [".woff2"] = "font/woff2", [".svg"] = "image/svg+xml", [".map"] = "application/json"
    };

    private readonly string _root;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();

    private MonacoAssetServer(string root, int port)
    {
        _root = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        Port = port;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public int Port { get; }
    public Uri IndexUri => new($"http://127.0.0.1:{Port}/index.html");

    public static MonacoAssetServer Start(string root)
    {
        // Reserve a free port, release it, then bind; retry on the rare race.
        for (int attempt = 0; ; attempt++)
        {
            TcpListener probe = new(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            MonacoAssetServer server = new(root, port);
            try
            {
                server._listener.Start();
                _ = Task.Run(() => server.ServeAsync(server._cts.Token));
                return server;
            }
            catch (HttpListenerException) when (attempt < 5) { server._listener.Close(); }
        }
    }

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception) when (cancellationToken.IsCancellationRequested || !_listener.IsListening) { return; }
            _ = Task.Run(() => HandleAsync(context), CancellationToken.None);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            string relative = Uri.UnescapeDataString(context.Request.Url?.AbsolutePath ?? "/").TrimStart('/');
            if (relative.Length == 0) relative = "index.html";
            string full = Path.GetFullPath(Path.Combine(_root, relative));
            if (!full.StartsWith(_root, StringComparison.Ordinal) || !File.Exists(full))
            {
                context.Response.StatusCode = 404;
                return;
            }
            context.Response.ContentType = MimeTypes.GetValueOrDefault(Path.GetExtension(full), "application/octet-stream");
            byte[] bytes = await File.ReadAllBytesAsync(full).ConfigureAwait(false);
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        }
        catch (Exception) { context.Response.StatusCode = 500; }
        finally { context.Response.Close(); }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Close(); } catch (ObjectDisposedException) { }
        _cts.Dispose();
    }
}
