using System.Runtime.InteropServices;

namespace NexMud.Scripting.Tooling;

public static class ToolchainComponentNames
{
    public const string Node = "node";
    public const string Pnpm = "pnpm";
    public const string TypeScript = "typescript";
    public const string TypeScriptLanguageServer = "typescript-language-server";
    public const string Esbuild = "esbuild";
    public const string Vitest = "vitest";
}

public sealed record ToolchainComponentManifest(
    string Name,
    string Version,
    string RelativePath,
    string? Sha256 = null,
    bool Executable = false);

public sealed record ToolchainManifest(
    int SchemaVersion,
    string Platform,
    string Architecture,
    string Fingerprint,
    IReadOnlyList<ToolchainComponentManifest> Components);

public sealed record ToolchainComponentLocation(
    string Name,
    string Version,
    string FullPath,
    string? Sha256,
    bool Executable);

public sealed class ToolchainUnavailableException : Exception
{
    public ToolchainUnavailableException(string message) : base(message) { }
    public ToolchainUnavailableException(string message, Exception innerException) : base(message, innerException) { }
}

public static class ToolchainPlatform
{
    public static string CurrentPlatform =>
        OperatingSystem.IsMacOS() ? "darwin" :
        OperatingSystem.IsLinux() ? "linux" :
        OperatingSystem.IsWindows() ? "win32" :
        throw new PlatformNotSupportedException("NexMUD scripting tooling does not support this operating system.");

    public static string CurrentArchitecture => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.X64 => "x64",
        _ => throw new PlatformNotSupportedException(
            $"NexMUD scripting tooling does not support process architecture '{RuntimeInformation.ProcessArchitecture}'.")
    };

    public static string CurrentRid => (CurrentPlatform, CurrentArchitecture) switch
    {
        ("darwin", "arm64") => "osx-arm64",
        ("darwin", "x64") => "osx-x64",
        ("linux", "arm64") => "linux-arm64",
        ("linux", "x64") => "linux-x64",
        ("win32", "arm64") => "win-arm64",
        ("win32", "x64") => "win-x64",
        _ => throw new PlatformNotSupportedException("Unable to resolve the current NexMUD toolchain RID.")
    };
}
