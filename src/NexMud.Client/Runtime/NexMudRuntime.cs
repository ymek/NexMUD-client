using System.Threading.Channels;
using NexMud.Adapters.Avendar;
using NexMud.Client.Automation;
using NexMud.Client.Commands;
using NexMud.Client.Knowledge;
using NexMud.Client.Interaction;
using NexMud.Client.Navigation;
using NexMud.Client.Settings;
using NexMud.Client.Secrets;
using NexMud.Scripting.Scheduling;
using NexMud.Scripting.Time;
using NexMud.Scripting.Host;
using NexMud.Scripting.Execution;
using NexMud.Client.Scripting;
using NexMud.Contracts.Events;
using NexMud.Contracts.Jev;
using NexMud.Contracts.Transport;
using NexMud.Core.Actions;
using NexMud.Core.Events;
using NexMud.Core.Jev;
using NexMud.Core.State;
using NexMud.Jev;
using NexMud.Jev.Typesafe;
using NexMud.Transport;

namespace NexMud.Client.Runtime;

public sealed class NexMudRuntime : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _workers = [];
    private const string TypesafeApiKeySecret = "TypesafeApiKey";
    private readonly ChannelReader<EventEnvelope> _settingsEvents;
    private readonly ChannelReader<EventEnvelope> _jevEvents;
    private readonly ChannelReader<EventEnvelope> _automationEvents;
    private readonly ChannelReader<EventEnvelope> _knowledgeEvents;
    private readonly ChannelReader<EventEnvelope> _movementEvents;
    private readonly ChannelReader<EventEnvelope> _routeControlEvents;
    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private readonly SemaphoreSlim _jevGate = new(1, 1);
    private IDisposable? _ownedDecisionEngine;
    private string? _environmentJevApiKey;

    public NexMudRuntime(ClientSettingsStore? settingsStore = null, ISecretStore? secretStore = null)
    {
        SettingsStore = settingsStore ?? new ClientSettingsStore();
        SecretStore = secretStore ?? SecretStoreFactory.CreateDefault();
        Events = new EventPipeline();
        State = new StateReducer(Events.StateEvents, Events);
        CommandJournal = new OutboundCommandJournal(Events.SubscribeLossless());
        Transport = new TcpMudTransport(Events);
        Authority = new JevAuthorityService(Events);
        Actions = new ActionProcessor(Transport, State, Authority, Events);
        ScriptClock = SystemScriptClock.Instance;
        ScriptExecution = new ScriptExecutionSupervisor(_cts.Token, ScriptClock);
        ScriptScheduler = new ScriptScheduler(ScriptClock);
        NavigationAuthority = new MapperNavigationAuthority();
        ScriptCommands = new ClientScriptCommands(
            Actions,
            State,
            ScriptScheduler,
            () => Settings.Automation ?? new AutomationPreferences(),
            NavigationAuthority);
        Avendar = new AvendarGameAdapter(Events.SubscribeLossless(), Events);
        _settingsEvents = Events.SubscribeLossless();
        _jevEvents = Events.SubscribeLossless();
        _knowledgeEvents = Events.SubscribeLossless();
        _movementEvents = Events.SubscribeLossless();
        _routeControlEvents = Events.SubscribeLossless();
        Knowledge = new WorldKnowledgeStore(_knowledgeEvents, State, mapperSettings: () => Settings.Mapper ?? new MapperPreferences());
        MapperQueries = new MapperQueryService(State, Knowledge, () => Settings.Mapper ?? new MapperPreferences());
        MovementCoordinator = new MapperMovementCoordinator(
            _movementEvents,
            State,
            ScriptCommands,
            ScriptScheduler,
            () => Settings.Mapper ?? new MapperPreferences());
        ScriptMapper = new ClientScriptMapperHost(MapperQueries, MovementCoordinator);
        JevCoordinator = new JevDecisionCoordinator(
            _jevEvents,
            State,
            Authority,
            ScriptCommands,
            ScriptScheduler,
            ScriptExecution,
            Events,
            () => DecisionEngine,
            async (snapshot, cancellationToken) =>
                await Knowledge.GetDecisionContextAsync(snapshot, cancellationToken).ConfigureAwait(false));
        Scripting = new ClientScriptPlatform(
            Events.SubscribeLossless(),
            State,
            Actions,
            Knowledge,
            ScriptMapper,
            Events,
            supervisor: ScriptExecution,
            scheduler: ScriptScheduler,
            commands: ScriptCommands,
            activeProfileId: () => ActiveConnectionProfile.Id,
            applicationCancellation: _cts.Token);
        ScriptWorkspace = new ScriptWorkspaceService(Scripting, Events, () => ActiveConnectionProfile.Id);
        Navigator = new AutoMoveService(
            _routeControlEvents,
            State,
            Knowledge,
            Events,
            () => Settings.Mapper ?? new MapperPreferences(),
            Scripting,
            ScriptMapper,
            NavigationAuthority,
            ScriptScheduler);
        _automationEvents = Events.SubscribeLossless();
        AutomationCompiler = new AutomationRuntimeCompiler(Scripting, State, ScriptScheduler);
        Automation = new ClientAutomationService(
            _automationEvents,
            State,
            ScriptCommands,
            ScriptScheduler,
            ScriptExecution,
            Events,
            () => Settings,
            async (domain, cancellationToken) =>
            {
                JevDecisionTrace? decision = domain switch
                {
                    JevDomain.Combat => await JevCoordinator.EvaluateCombatNowAsync(cancellationToken).ConfigureAwait(false),
                    JevDomain.Navigation => await JevCoordinator.EvaluateNavigationNowAsync(cancellationToken).ConfigureAwait(false),
                    JevDomain.Recovery => await JevCoordinator.EvaluateRecoveryNowAsync(cancellationToken).ConfigureAwait(false),
                    _ => null
                };

                return decision is not null;
            },
            async (query, cancellationToken) =>
                await Navigator.NavigateToQueryAsync(query, cancellationToken).ConfigureAwait(false),
            compiledAutomation: AutomationCompiler);
        InputCommands = new LocalCommandHandler(this);
        InputAliases = new AutomationAliasResolver(AutomationCompiler, () => Automation.Variables);
        Interaction = new ClientInteractionRuntime(
            () => Settings,
            Events.SubscribeLossless(),
            InputAliases,
            InputCommands,
            new KnowledgeCommandHistoryPersistence(Knowledge),
            () => State.Current);
    }

    public EventPipeline Events { get; }
    public StateReducer State { get; }
    public OutboundCommandJournal CommandJournal { get; }
    public TcpMudTransport Transport { get; }
    public JevAuthorityService Authority { get; }
    public ActionProcessor Actions { get; }
    public IScriptClock ScriptClock { get; }
    public IScriptExecutionSupervisor ScriptExecution { get; }
    public IScriptScheduler ScriptScheduler { get; }
    public IScriptCommands ScriptCommands { get; }
    public MapperNavigationAuthority NavigationAuthority { get; }
    public MapperQueryService MapperQueries { get; }
    public MapperMovementCoordinator MovementCoordinator { get; }
    public IScriptMapper ScriptMapper { get; }
    public ClientScriptPlatform Scripting { get; }
    public ScriptWorkspaceService ScriptWorkspace { get; }
    public AvendarGameAdapter Avendar { get; }
    public JevDecisionCoordinator JevCoordinator { get; }
    public ClientAutomationService Automation { get; }
    public LocalCommandHandler InputCommands { get; }
    public IInputAliasResolver InputAliases { get; }
    public AutomationRuntimeCompiler AutomationCompiler { get; }
    public WorldKnowledgeStore Knowledge { get; }
    public ClientInteractionRuntime Interaction { get; }
    public AutoMoveService Navigator { get; }
    public ClientSettingsStore SettingsStore { get; }
    public ISecretStore SecretStore { get; }
    public ClientSettings Settings { get; private set; } = ClientSettings.Default;

    public IReadOnlyList<ConnectionProfile> ConnectionProfiles => Settings.EffectiveConnectionProfiles;

    public ConnectionProfile ActiveConnectionProfile => Settings.ActiveConnectionProfile;
    public IJevDecisionEngine? DecisionEngine { get; private set; }
    public bool EnvironmentJevApiKeyConfigured => !string.IsNullOrWhiteSpace(_environmentJevApiKey);
    public CancellationToken CancellationToken => _cts.Token;

    public void Start()
    {
        if (_workers.Count > 0)
        {
            return;
        }

        _workers.Add(Task.Run(() => RunWorkerAsync("State reducer", State.RunAsync), CancellationToken.None));
        _workers.Add(Task.Run(() => RunWorkerAsync("Command journal", CommandJournal.RunAsync), CancellationToken.None));
        _workers.Add(Task.Run(() => RunWorkerAsync("Action processor", Actions.RunAsync), CancellationToken.None));
        _workers.Add(Task.Run(() => RunWorkerAsync("Avendar adapter", Avendar.RunAsync), CancellationToken.None));
        _workers.Add(Task.Run(() => RunWorkerAsync("Settings persistence", PersistSettingsAsync), CancellationToken.None));
        _workers.Add(Task.Run(() => RunWorkerAsync("Jev coordinator", JevCoordinator.RunAsync), CancellationToken.None));
        _workers.Add(Task.Run(() => RunWorkerAsync(
            "Compiled automation modules",
            async token =>
            {
                await AutomationCompiler.ReloadAsync(Settings, token).ConfigureAwait(false);
                await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            }), CancellationToken.None));
        _workers.Add(Task.Run(() => RunWorkerAsync("Client automation", Automation.RunAsync), CancellationToken.None));
        _workers.Add(Task.Run(() => RunWorkerAsync("Interaction output pipeline", Interaction.RunAsync), CancellationToken.None));
        _workers.Add(Task.Run(() => RunWorkerAsync("World knowledge", Knowledge.RunAsync), CancellationToken.None));
        _workers.Add(Task.Run(() => RunWorkerAsync("Mapper movement coordinator", MovementCoordinator.RunAsync), CancellationToken.None));
        _workers.Add(Task.Run(() => RunWorkerAsync("Mapper route control", Navigator.RunAsync), CancellationToken.None));
        _workers.Add(Task.Run(() => RunWorkerAsync("Scripting event bridge", Scripting.RunEventBridgeAsync), CancellationToken.None));
    }

    public async Task RestoreSettingsAsync(CancellationToken cancellationToken = default)
    {
        ClientSettings loaded = await SettingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        await _settingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Settings = loaded;
            Interaction.Configure(Settings);
            await Interaction.Input.RestoreHistoryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _settingsGate.Release();
        }

        JevAuthoritySnapshot authority = JevAuthoritySnapshot.Create(loaded.JevPreset, loaded.JevDomains);
        await Authority.ApplyProfileAsync(authority, cancellationToken).ConfigureAwait(false);
        await Authority.SetEnabledAsync(loaded.JevEnabled, cancellationToken).ConfigureAwait(false);
        await ScriptWorkspace.ActivateProfileAsync(loaded.ActiveConnectionProfile.Id, cancellationToken).ConfigureAwait(false);
        await AutomationCompiler.ReloadAsync(loaded, cancellationToken).ConfigureAwait(false);
    }

    public async Task InitializeDecisionEngineAsync(
        string? environmentApiKey,
        string? modelOverride = null,
        CancellationToken cancellationToken = default)
    {
        _environmentJevApiKey = string.IsNullOrWhiteSpace(environmentApiKey) ? null : environmentApiKey.Trim();
        string? stored = await SecretStore.GetAsync(TypesafeApiKeySecret, cancellationToken).ConfigureAwait(false);
        string? key = _environmentJevApiKey ?? stored;
        string model = string.IsNullOrWhiteSpace(modelOverride) ? Settings.JevModel : modelOverride.Trim();
        await ReplaceDecisionEngineAsync(key, model, cancellationToken).ConfigureAwait(false);
    }

    public Task<string?> GetStoredJevApiKeyAsync(CancellationToken cancellationToken = default) =>
        SecretStore.GetAsync(TypesafeApiKeySecret, cancellationToken);

    public async Task SaveJevConfigurationAsync(
        string? apiKey,
        string model,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("Jev model is required.", nameof(model));
        }

        string normalizedModel = model.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            await SecretStore.DeleteAsync(TypesafeApiKeySecret, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (!SecretStore.IsAvailable)
            {
                throw new PlatformNotSupportedException("Secure API-key storage is not available on this platform.");
            }
            await SecretStore.SetAsync(TypesafeApiKeySecret, apiKey.Trim(), cancellationToken).ConfigureAwait(false);
        }

        await _settingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Settings = Settings with { JevModel = normalizedModel };
            await SettingsStore.SaveAsync(Settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _settingsGate.Release();
        }

        string? effectiveKey = _environmentJevApiKey ?? (string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim());
        await ReplaceDecisionEngineAsync(effectiveKey, normalizedModel, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReplaceDecisionEngineAsync(
        string? apiKey,
        string model,
        CancellationToken cancellationToken)
    {
        await _jevGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _ownedDecisionEngine?.Dispose();
            _ownedDecisionEngine = null;
            DecisionEngine = null;

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return;
            }

            TypesafeJevDecisionEngine engine = new(
                new TypesafeJevOptions(apiKey.Trim(), model),
                Events);
            _ownedDecisionEngine = engine;
            DecisionEngine = engine;
        }
        finally
        {
            _jevGate.Release();
        }
    }

    public Task<JevDecisionTrace?> EvaluateCombatWithJevAsync(CancellationToken cancellationToken = default) =>
        JevCoordinator.EvaluateCombatNowAsync(cancellationToken);

    public Task<JevDecisionTrace?> EvaluateNavigationWithJevAsync(CancellationToken cancellationToken = default) =>
        JevCoordinator.EvaluateNavigationNowAsync(cancellationToken);

    public Task<JevDecisionTrace?> EvaluateRecoveryWithJevAsync(CancellationToken cancellationToken = default) =>
        JevCoordinator.EvaluateRecoveryNowAsync(cancellationToken);


    public MudConnectionOptions CreateConnectionOptions(ConnectionProfile? profile = null)
    {
        ConnectionProfile selected = profile ?? ActiveConnectionProfile;
        ConnectionProtocolPreferences protocols = selected.EffectiveProtocols;
        return new MudConnectionOptions(
            selected.Host,
            selected.Port,
            selected.UseTls,
            selected.TerminalType,
            Protocols: new MudProtocolOptions(
                Naws: protocols.Naws != ProtocolPolicy.Disabled,
                Gmcp: protocols.Gmcp != ProtocolPolicy.Disabled,
                Msdp: protocols.Msdp != ProtocolPolicy.Disabled,
                Mssp: protocols.Mssp != ProtocolPolicy.Disabled,
                Mccp2: protocols.Mccp2 != ProtocolPolicy.Disabled,
                Charset: protocols.Charset != ProtocolPolicy.Disabled,
                NewEnvironment: protocols.NewEnvironment != ProtocolPolicy.Disabled,
                Mtts: protocols.Mtts != ProtocolPolicy.Disabled,
                Eor: protocols.Eor != ProtocolPolicy.Disabled),
            ClientName: "NexMUD",
            ClientVersion: "0.31.0");
    }

    public MudConnectionOptions CreateConnectionOptions(string? host, int? port, bool? useTls)
    {
        ConnectionProfile active = ActiveConnectionProfile;
        return CreateConnectionOptions(active with
        {
            Host = host ?? active.Host,
            Port = port ?? active.Port,
            UseTls = useTls ?? active.UseTls
        });
    }

    public async Task SaveAutomationPreferencesAsync(
        AutomationPreferences automation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(automation);
        await _settingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Settings = Settings with { Automation = automation };
            await SettingsStore.SaveAsync(Settings, cancellationToken).ConfigureAwait(false);
            if (!automation.Enabled) Automation.CancelAllWorkflows();
            await AutomationCompiler.ReloadAsync(Settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _settingsGate.Release();
        }
    }

    public async Task SaveAutomationCompositionAsync(
        IReadOnlyList<CommandAlias> aliases,
        IReadOnlyList<TriggerRule> triggers,
        IReadOnlyList<SemanticTriggerRule> semanticTriggers,
        IReadOnlyList<GameRule> gameRules,
        IReadOnlyList<AutomationWorkflow> workflows,
        IReadOnlyList<CommandTimer> timers,
        IReadOnlyList<CommandKeyBinding> keyBindings,
        CancellationToken cancellationToken = default)
    {
        await _settingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Settings = Settings with
            {
                Aliases = aliases.ToArray(),
                Triggers = triggers.ToArray(),
                SemanticTriggers = semanticTriggers.ToArray(),
                GameRules = gameRules.ToArray(),
                Workflows = workflows.ToArray(),
                Timers = timers.ToArray(),
                KeyBindings = keyBindings.ToArray()
            };
            await SettingsStore.SaveAsync(Settings, cancellationToken).ConfigureAwait(false);
            Interaction.Configure(Settings);
            await AutomationCompiler.ReloadAsync(Settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _settingsGate.Release();
        }
    }

    public async Task SaveMapperPreferencesAsync(
        MapperPreferences mapper,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        await _settingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Settings = Settings with { Mapper = mapper };
            await SettingsStore.SaveAsync(Settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _settingsGate.Release();
        }
    }

    public async Task SaveGeneralPreferencesAsync(
        GeneralPreferences general,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(general);
        await _settingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Settings = Settings with
            {
                General = general,
                ShowContextDock = general.ShowGameplayRailByDefault
            };
            await SettingsStore.SaveAsync(Settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _settingsGate.Release();
        }
    }

    public async Task SaveAppearancePreferencesAsync(
        AppearancePreferences appearance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        await _settingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Settings = Settings with
            {
                Appearance = appearance,
                TranscriptFontSize = appearance.TranscriptSize
            };
            await SettingsStore.SaveAsync(Settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _settingsGate.Release();
        }
    }

    public async Task SaveConnectionProfilesAsync(
        IReadOnlyList<ConnectionProfile> profiles,
        string activeProfileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        if (profiles.Count == 0) throw new ArgumentException("At least one connection profile is required.", nameof(profiles));
        ConnectionProfile active = profiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, activeProfileId, StringComparison.Ordinal))
            ?? throw new ArgumentException("Active connection profile must exist in the profile collection.", nameof(activeProfileId));

        await _settingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Settings = Settings with
            {
                ConnectionProfiles = profiles.ToArray(),
                ActiveConnectionProfileId = active.Id,
                Host = active.Host,
                Port = active.Port,
                UseTls = active.UseTls,
                TerminalType = active.TerminalType,
                Protocols = ToLegacyProtocolPreferences(active.EffectiveProtocols)
            };
            await SettingsStore.SaveAsync(Settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _settingsGate.Release();
        }
        await ScriptWorkspace.ActivateProfileAsync(active.Id, cancellationToken).ConfigureAwait(false);
    }

    private static ProtocolPreferences ToLegacyProtocolPreferences(ConnectionProtocolPreferences protocols) => new(
        Naws: protocols.Naws != ProtocolPolicy.Disabled,
        Gmcp: protocols.Gmcp != ProtocolPolicy.Disabled,
        Msdp: protocols.Msdp != ProtocolPolicy.Disabled,
        Mssp: protocols.Mssp != ProtocolPolicy.Disabled,
        Mccp2: protocols.Mccp2 != ProtocolPolicy.Disabled,
        Charset: protocols.Charset != ProtocolPolicy.Disabled,
        NewEnvironment: protocols.NewEnvironment != ProtocolPolicy.Disabled,
        Mtts: protocols.Mtts != ProtocolPolicy.Disabled,
        Eor: protocols.Eor != ProtocolPolicy.Disabled);

    public Task<bool> ApproveJevDecisionAsync(Guid decisionId, CancellationToken cancellationToken = default) =>
        JevCoordinator.ApproveAsync(decisionId, cancellationToken);

    public Task<bool> RejectJevDecisionAsync(Guid decisionId, CancellationToken cancellationToken = default) =>
        JevCoordinator.RejectAsync(decisionId, cancellationToken);

    public async Task SaveConnectionSettingsAsync(
        string host,
        int port,
        bool useTls,
        CancellationToken cancellationToken = default)
    {
        ConnectionProfile active = ActiveConnectionProfile with
        {
            Host = host,
            Port = port,
            UseTls = useTls
        };
        ConnectionProfile[] profiles = ConnectionProfiles
            .Select(profile => string.Equals(profile.Id, active.Id, StringComparison.Ordinal) ? active : profile)
            .ToArray();
        await SaveConnectionProfilesAsync(profiles, active.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveClientPreferencesAsync(
        string host,
        int port,
        bool useTls,
        string terminalType,
        double transcriptFontSize,
        bool showContextDock,
        bool autoOpenCombatContext,
        bool autoLogSessions,
        bool slurpTelemetryPrompt,
        string commandSeparator,
        IReadOnlyList<TranscriptHighlightRule> highlightRules,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("Host is required.", nameof(host));
        }
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1 and 65535.");
        }
        if (string.IsNullOrWhiteSpace(terminalType))
        {
            throw new ArgumentException("Terminal type is required.", nameof(terminalType));
        }

        await _settingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Settings = Settings with
            {
                Host = host.Trim(),
                Port = port,
                UseTls = useTls,
                TerminalType = terminalType.Trim(),
                TranscriptFontSize = Math.Clamp(transcriptFontSize, 8, 24),
                ShowContextDock = showContextDock,
                AutoOpenCombatContext = autoOpenCombatContext,
                AutoLogSessions = autoLogSessions,
                SlurpTelemetryPrompt = slurpTelemetryPrompt,
                CommandSeparator = commandSeparator,
                HighlightRules = highlightRules.ToArray()
            };
            Interaction.Configure(Settings);
            await SettingsStore.SaveAsync(Settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _settingsGate.Release();
        }
    }

    public async Task SaveConvenienceSettingsAsync(
        TranscriptLogFormat logFormat,
        IReadOnlyList<CommandAlias> aliases,
        IReadOnlyList<TriggerRule> triggers,
        IReadOnlyList<GameRule> gameRules,
        IReadOnlyList<AutomationWorkflow> workflows,
        IReadOnlyList<CommandTimer> timers,
        IReadOnlyList<CommandKeyBinding> keyBindings,
        CancellationToken cancellationToken = default)
    {
        await _settingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Settings = Settings with
            {
                LogFormat = logFormat,
                Aliases = aliases.ToArray(),
                Triggers = triggers.ToArray(),
                GameRules = gameRules.ToArray(),
                Workflows = workflows.ToArray(),
                Timers = timers.ToArray(),
                KeyBindings = keyBindings.ToArray()
            };
            Interaction.Configure(Settings);
            await SettingsStore.SaveAsync(Settings, cancellationToken).ConfigureAwait(false);
            await AutomationCompiler.ReloadAsync(Settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _settingsGate.Release();
        }
    }

    public async Task SaveInteractionSettingsAsync(
        InputPreferences input,
        OutputPreferences output,
        IReadOnlyList<OutputTransformationRule> outputRules,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(outputRules);
        await _settingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Settings = Settings with
            {
                Input = input,
                Output = output,
                OutputRules = outputRules.ToArray()
            };
            Interaction.Configure(Settings);
            await SettingsStore.SaveAsync(Settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _settingsGate.Release();
        }
    }

    public async Task SaveGlobalClientPreferencesAsync(
        GeneralPreferences general,
        AppearancePreferences appearance,
        bool autoLogSessions,
        bool slurpTelemetryPrompt,
        string commandSeparator,
        TranscriptLogFormat logFormat,
        InputPreferences input,
        OutputPreferences output,
        IReadOnlyList<TranscriptHighlightRule> highlightRules,
        IReadOnlyList<OutputTransformationRule> outputRules,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(general);
        ArgumentNullException.ThrowIfNull(appearance);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(highlightRules);
        ArgumentNullException.ThrowIfNull(outputRules);

        await _settingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Settings = Settings with
            {
                General = general,
                Appearance = appearance,
                ShowContextDock = general.ShowGameplayRailByDefault,
                TranscriptFontSize = appearance.TranscriptSize,
                AutoLogSessions = autoLogSessions,
                SlurpTelemetryPrompt = slurpTelemetryPrompt,
                CommandSeparator = commandSeparator,
                LogFormat = logFormat,
                Input = input,
                Output = output,
                HighlightRules = highlightRules.ToArray(),
                OutputRules = outputRules.ToArray()
            };
            Interaction.Configure(Settings);
            await SettingsStore.SaveAsync(Settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _settingsGate.Release();
        }
    }

    public async Task SaveSettingsWorkspaceAsync(
        IReadOnlyList<ConnectionProfile> profiles,
        string activeProfileId,
        GeneralPreferences general,
        AppearancePreferences appearance,
        bool autoLogSessions,
        bool slurpTelemetryPrompt,
        string commandSeparator,
        TranscriptLogFormat logFormat,
        InputPreferences input,
        OutputPreferences output,
        IReadOnlyList<TranscriptHighlightRule> highlightRules,
        IReadOnlyList<OutputTransformationRule> outputRules,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(general);
        ArgumentNullException.ThrowIfNull(appearance);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(highlightRules);
        ArgumentNullException.ThrowIfNull(outputRules);
        if (profiles.Count == 0)
            throw new ArgumentException("At least one connection profile is required.", nameof(profiles));

        ConnectionProfile active = profiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, activeProfileId, StringComparison.Ordinal))
            ?? throw new ArgumentException(
                "Active connection profile must exist in the profile collection.",
                nameof(activeProfileId));

        await _settingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Settings = Settings with
            {
                ConnectionProfiles = profiles.ToArray(),
                ActiveConnectionProfileId = active.Id,
                Host = active.Host,
                Port = active.Port,
                UseTls = active.UseTls,
                TerminalType = active.TerminalType,
                Protocols = ToLegacyProtocolPreferences(active.EffectiveProtocols),
                General = general,
                Appearance = appearance,
                ShowContextDock = general.ShowGameplayRailByDefault,
                TranscriptFontSize = appearance.TranscriptSize,
                AutoLogSessions = autoLogSessions,
                SlurpTelemetryPrompt = slurpTelemetryPrompt,
                CommandSeparator = commandSeparator,
                LogFormat = logFormat,
                Input = input,
                Output = output,
                HighlightRules = highlightRules.ToArray(),
                OutputRules = outputRules.ToArray()
            };
            Interaction.Configure(Settings);
            await SettingsStore.SaveAsync(Settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _settingsGate.Release();
        }
    }

    public async Task SaveWorkspaceAsync(
        WorkspacePreferences workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        await _settingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Settings = Settings with { Workspace = workspace };
            await SettingsStore.SaveAsync(Settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _settingsGate.Release();
        }
    }

    public async Task SetJevEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await Authority.SetEnabledAsync(enabled, cancellationToken).ConfigureAwait(false);
        await _settingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Settings = Settings with { JevEnabled = enabled };
            await SettingsStore.SaveAsync(Settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _settingsGate.Release();
        }
    }

    public async Task ApplyAndSaveAuthorityAsync(
        JevAuthoritySnapshot authority,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        await Authority.ApplyProfileAsync(authority, cancellationToken).ConfigureAwait(false);
        await SaveAuthoritySnapshotAsync(authority, cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveAuthoritySnapshotAsync(
        JevAuthoritySnapshot authority,
        CancellationToken cancellationToken)
    {
        await _settingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Settings = ClientSettingsStore.WithAuthority(Settings, authority);
            await SettingsStore.SaveAsync(Settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _settingsGate.Release();
        }
    }

    private async Task PersistSettingsAsync(CancellationToken cancellationToken)
    {
        await foreach (EventEnvelope envelope in _settingsEvents.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (envelope.Payload is not JevAuthorityProfileChanged changed)
            {
                continue;
            }

            await SaveAuthoritySnapshotAsync(changed.Snapshot, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunWorkerAsync(string name, Func<CancellationToken, Task> worker)
    {
        try
        {
            await worker(_cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            try
            {
                await Events.PublishAsync(
                    new ComponentError(name, exception.Message),
                    "runtime",
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // The event pipeline may already be shutting down.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            await Transport.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Shutdown continues so the remaining workers can be cancelled.
        }

        _ownedDecisionEngine?.Dispose();
        _ownedDecisionEngine = null;
        DecisionEngine = null;

        await Scripting.DisposeAsync().ConfigureAwait(false);
        await ScriptExecution.DisposeAsync().ConfigureAwait(false);
        await Events.DisposeAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        _settingsGate.Dispose();
        _jevGate.Dispose();
        _cts.Dispose();
    }
}
