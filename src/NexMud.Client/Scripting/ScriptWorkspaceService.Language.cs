namespace NexMud.Client.Scripting;

public sealed partial class ScriptWorkspaceService
{
    /// <summary>
    /// Returns the profile-scoped filesystem workspace consumed by TypeScript project tooling.
    /// The GUI may use this trusted path for the bundled language server, but it is never exposed
    /// to Jint scripts or the browser editor bridge as filesystem authority.
    /// </summary>
    public string GetLanguageWorkspaceRoot(string profileId)
    {
        profileId = ScriptWorkspacePath.NormalizeIdentifier(profileId, nameof(profileId));
        return Path.GetFullPath(GetScriptsRoot(profileId));
    }

    /// <summary>
    /// Returns the canonical file URI used by Monaco and the TypeScript language server for one
    /// package source document. A source file has one identity across editor, LSP, diagnostics,
    /// navigation, rename, and filesystem-change reconciliation.
    /// </summary>
    public string GetSourceDocumentUri(string profileId, string packageId, string relativePath)
    {
        string path = GetSourcePath(profileId, packageId, relativePath);
        return ScriptLanguageProjectProjection.ToCanonicalFileUri(path);
    }
}
