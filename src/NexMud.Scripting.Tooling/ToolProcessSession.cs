using System.Diagnostics;

namespace NexMud.Scripting.Tooling;

/// <summary>
/// Long-lived shell-free tooling process used for stdio protocols such as LSP.
/// It shares the same sanitized environment construction as one-shot tooling commands.
/// </summary>
public sealed class ToolProcessSession : IAsyncDisposable
{
    private readonly Process _process;
    private int _disposed;

    private ToolProcessSession(Process process) => _process = process;

    public Stream StandardInput => _process.StandardInput.BaseStream;
    public Stream StandardOutput => _process.StandardOutput.BaseStream;
    public TextReader StandardError => _process.StandardError;
    public bool HasExited => _process.HasExited;
    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

    public static ToolProcessSession Start(ToolProcessRequest request)
    {
        ProcessStartInfo startInfo = ToolProcessStartInfoFactory.Create(request, redirectStandardInput: true);
        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Unable to start tooling process '{request.Executable}'.");
        return new ToolProcessSession(process);
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken = default) =>
        _process.WaitForExitAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Shutdown remains best-effort; the child may have exited concurrently.
        }

        try { await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
        _process.Dispose();
    }
}
