using System.Collections.Concurrent;
using NexMud.Scripting.Compilation;

namespace NexMud.Scripting.TypeScript.Cache;

public interface IScriptCompileCache
{
    bool TryGet(string cacheKey, out CompiledScriptPackage? package);
    void Put(CompiledScriptPackage package);
    void Remove(string cacheKey);
    void Clear();
}

public sealed class InMemoryScriptCompileCache : IScriptCompileCache
{
    private readonly ConcurrentDictionary<string, CompiledScriptPackage> _packages = new(StringComparer.Ordinal);

    public bool TryGet(string cacheKey, out CompiledScriptPackage? package) =>
        _packages.TryGetValue(cacheKey, out package);

    public void Put(CompiledScriptPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        _packages[package.CacheKey] = package;
    }

    public void Remove(string cacheKey) => _packages.TryRemove(cacheKey, out _);
    public void Clear() => _packages.Clear();
}
