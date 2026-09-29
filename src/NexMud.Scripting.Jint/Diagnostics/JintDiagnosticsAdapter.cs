using Jint.Runtime;
using NexMud.Scripting.Compilation;
using NexMud.Scripting.Diagnostics;

namespace NexMud.Scripting.Jint.Diagnostics;

public static class JintDiagnosticsAdapter
{
    public static ScriptError SanitizeRuntimeError(
        Exception exception,
        CompiledScriptPackage? package = null,
        ScriptSourceLocation? location = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        location ??= package is null ? null : ResolveLocation(exception, package);
        string typeName = exception.GetType().Name;
        ScriptErrorKind kind = typeName switch
        {
            "TimeoutException" => ScriptErrorKind.ScriptTimeoutError,
            "MemoryLimitExceededException" or "StatementsCountOverflowException" or "RecursionDepthOverflowException" => ScriptErrorKind.ScriptResourceLimitError,
            "OperationCanceledException" or "ExecutionCanceledException" => ScriptErrorKind.CancelledError,
            _ => ScriptErrorKind.ScriptRuntimeError
        };
        string message = kind switch
        {
            ScriptErrorKind.ScriptTimeoutError => "Script execution exceeded its time limit.",
            ScriptErrorKind.ScriptResourceLimitError => "Script execution exceeded a resource limit.",
            ScriptErrorKind.CancelledError => "Script execution was cancelled.",
            _ => "Script execution failed."
        };
        return new ScriptError(kind, typeName, message, location);
    }

    private static ScriptSourceLocation? ResolveLocation(Exception exception, CompiledScriptPackage package)
    {
        if (exception is not JavaScriptException javascriptException) return null;
        ref readonly var location = ref javascriptException.Location;
        string? generatedFile = location.SourceFile;
        int generatedLine = location.Start.Line;
        int generatedColumn = location.Start.Column;
        CompiledScriptModule? module = FindModule(package, generatedFile);
        if (module is null)
            return new ScriptSourceLocation(null, null, null, generatedFile, generatedLine, generatedColumn);
        return ScriptSourceMaps.TryMap(module, generatedLine, generatedColumn, out ScriptSourceLocation mapped)
            ? mapped
            : ScriptSourceMaps.Generated(module, generatedLine, generatedColumn);
    }

    private static CompiledScriptModule? FindModule(CompiledScriptPackage package, string? generatedFile)
    {
        if (!string.IsNullOrWhiteSpace(generatedFile))
        {
            string normalized = generatedFile.Replace('\\', '/').TrimStart('/');
            if (package.Modules.TryGetValue(normalized, out CompiledScriptModule? exact)) return exact;
            CompiledScriptModule? bySuffix = package.Modules.Values.FirstOrDefault(module =>
                normalized.EndsWith(module.Path, StringComparison.Ordinal));
            if (bySuffix is not null) return bySuffix;
        }
        return package.Modules.TryGetValue(package.Manifest.Entrypoint, out CompiledScriptModule? entrypoint) ? entrypoint : null;
    }
}
