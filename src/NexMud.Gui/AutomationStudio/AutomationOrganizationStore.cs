using System.Text.Json;
using System.Text.Json.Serialization;
using NexMud.Client.Paths;
using NexMud.Client.Settings;

namespace NexMud.Gui.AutomationStudio;

internal sealed record AutomationOrganizationFolder(
    string Id,
    StudioDocumentKind Kind,
    string Name,
    string? ParentFolderId,
    int Order);

internal sealed record AutomationOrganizationItem(
    string Id,
    StudioDocumentKind Kind,
    int SourceIndex,
    string? FolderId,
    int Order,
    string? DomainId = null);

internal sealed record AutomationOrganizationCatalog(
    int Version,
    IReadOnlyList<AutomationOrganizationFolder> Folders,
    IReadOnlyList<AutomationOrganizationItem> Items)
{
    public const int CurrentVersion = 1;

    public static AutomationOrganizationCatalog Empty { get; } = new(CurrentVersion, [], []);

    public string IdFor(StudioDocumentKind kind, int sourceIndex) =>
        Items.FirstOrDefault(item => item.Kind == kind && item.SourceIndex == sourceIndex)?.Id
        ?? throw new InvalidOperationException($"Automation organization is missing {kind} #{sourceIndex}.");

    public int? SourceIndexFor(StudioDocumentKind kind, string itemId) =>
        Items.FirstOrDefault(item => item.Kind == kind && item.Id.Equals(itemId, StringComparison.Ordinal))?.SourceIndex;

    public string? FolderIdFor(StudioDocumentKind kind, string itemId) =>
        Items.FirstOrDefault(item => item.Kind == kind && item.Id.Equals(itemId, StringComparison.Ordinal))?.FolderId;

    public AutomationOrganizationCatalog Reconcile(
        AutomationCollections collections,
        Func<string>? createId = null)
    {
        ArgumentNullException.ThrowIfNull(collections);
        createId ??= static () => $"auto_{Guid.NewGuid():N}";

        List<AutomationOrganizationFolder> folders = Folders
            .Where(folder => StudioDocumentKinds.AutomationKinds.Contains(folder.Kind))
            .ToList();
        List<AutomationOrganizationItem> reconciled = [];
        HashSet<string> usedIds = new(StringComparer.Ordinal);

        foreach (StudioDocumentKind kind in StudioDocumentKinds.AutomationKinds)
        {
            List<AutomationOrganizationItem> existing = Items
                .Where(item => item.Kind == kind)
                .OrderBy(item => item.SourceIndex)
                .ToList();

            for (int sourceIndex = 0; sourceIndex < collections.Count(kind); sourceIndex++)
            {
                string? domainId = DomainId(collections.Get(kind, sourceIndex));
                AutomationOrganizationItem? item = null;

                if (!string.IsNullOrWhiteSpace(domainId))
                {
                    item = existing.FirstOrDefault(candidate =>
                        candidate.DomainId?.Equals(domainId, StringComparison.Ordinal) == true &&
                        !usedIds.Contains(candidate.Id));
                }

                item ??= existing.FirstOrDefault(candidate =>
                    candidate.SourceIndex == sourceIndex &&
                    !usedIds.Contains(candidate.Id) &&
                    (domainId is null || candidate.DomainId is null));

                if (item is null)
                {
                    string id;
                    do { id = createId(); } while (!usedIds.Add(id));
                    item = new AutomationOrganizationItem(id, kind, sourceIndex, null, sourceIndex, domainId);
                }
                else
                {
                    usedIds.Add(item.Id);
                    string? folderId = item.FolderId;
                    if (folderId is not null && !folders.Any(folder => folder.Kind == kind && folder.Id == folderId))
                        folderId = null;
                    item = item with { SourceIndex = sourceIndex, FolderId = folderId, DomainId = domainId };
                }

                reconciled.Add(item);
            }
        }

        return this with { Version = CurrentVersion, Folders = folders, Items = reconciled };
    }

    public AutomationOrganizationCatalog RegisterAdded(
        StudioDocumentKind kind,
        int sourceIndex,
        string? folderId = null,
        Func<string>? createId = null)
    {
        createId ??= static () => $"auto_{Guid.NewGuid():N}";
        if (Items.Any(item => item.Kind == kind && item.SourceIndex == sourceIndex)) return this;

        List<AutomationOrganizationItem> items = Items
            .Select(item => item.Kind == kind && item.SourceIndex >= sourceIndex
                ? item with { SourceIndex = item.SourceIndex + 1 }
                : item)
            .ToList();
        int order = items.Where(item => item.Kind == kind && item.FolderId == folderId)
            .Select(item => item.Order)
            .DefaultIfEmpty(-1)
            .Max() + 1;
        string id = createId();
        while (items.Any(item => item.Id.Equals(id, StringComparison.Ordinal))) id = createId();
        items.Add(new AutomationOrganizationItem(id, kind, sourceIndex, folderId, order));
        return this with { Items = items };
    }

    public AutomationOrganizationCatalog RegisterRemoved(StudioDocumentKind kind, int sourceIndex)
    {
        List<AutomationOrganizationItem> items = Items
            .Where(item => item.Kind != kind || item.SourceIndex != sourceIndex)
            .Select(item => item.Kind == kind && item.SourceIndex > sourceIndex
                ? item with { SourceIndex = item.SourceIndex - 1 }
                : item)
            .ToList();
        return this with { Items = items };
    }

    private static string? DomainId(object? value) => value switch
    {
        CommandAlias definition => NormalizeId(definition.Id),
        TriggerRule definition => NormalizeId(definition.Id),
        SemanticTriggerRule definition => NormalizeId(definition.Id),
        CommandKeyBinding definition => NormalizeId(definition.Id),
        CommandTimer definition => NormalizeId(definition.Id),
        GameRule definition => NormalizeId(definition.Id),
        AutomationWorkflow definition => NormalizeId(definition.Id),
        _ => null
    };

    private static string? NormalizeId(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal sealed class AutomationOrganizationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _dataRoot;

    public AutomationOrganizationStore(string? dataRoot = null)
    {
        _dataRoot = dataRoot ?? Path.GetDirectoryName(NexMudDataPaths.GetFilePath("settings.json"))
            ?? throw new InvalidOperationException("Unable to resolve the NexMUD application data directory.");
    }

    public async Task<AutomationOrganizationCatalog> LoadAndReconcileAsync(
        string profileId,
        AutomationCollections collections,
        CancellationToken cancellationToken = default)
    {
        string path = PathFor(profileId);
        AutomationOrganizationCatalog loaded = AutomationOrganizationCatalog.Empty;
        if (File.Exists(path))
        {
            try
            {
                await using FileStream stream = File.OpenRead(path);
                loaded = await JsonSerializer.DeserializeAsync<AutomationOrganizationCatalog>(
                    stream,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false)
                    ?? AutomationOrganizationCatalog.Empty;
            }
            catch (JsonException)
            {
                loaded = AutomationOrganizationCatalog.Empty;
            }
        }

        AutomationOrganizationCatalog reconciled = loaded.Reconcile(collections);
        if (!Equivalent(loaded, reconciled))
            await SaveAsync(profileId, reconciled, cancellationToken).ConfigureAwait(false);
        return reconciled;
    }

    public async Task SaveAsync(
        string profileId,
        AutomationOrganizationCatalog catalog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        string path = PathFor(profileId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, catalog, JsonOptions, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static bool Equivalent(AutomationOrganizationCatalog left, AutomationOrganizationCatalog right) =>
        left.Version == right.Version &&
        left.Folders.SequenceEqual(right.Folders) &&
        left.Items.SequenceEqual(right.Items);

    private string PathFor(string profileId)
    {
        string safeProfileId = NormalizeIdentifier(profileId);
        return Path.Combine(_dataRoot, "profiles", safeProfileId, "automation-studio-organization.json");
    }

    private static string NormalizeIdentifier(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string normalized = value.Trim();
        if (normalized is "." or ".." || normalized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            normalized.Contains('/') || normalized.Contains('\\'))
            throw new ArgumentException("Profile id contains invalid path characters.", nameof(value));
        return normalized;
    }
}
