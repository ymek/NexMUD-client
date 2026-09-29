using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using NexMud.Adapters.Avendar;
using NexMud.Contracts.Actions;
using NexMud.Contracts.Events;
using NexMud.Contracts.Jev;
using NexMud.Contracts.Gameplay;
using NexMud.Contracts.State;
using NexMud.Contracts.Transport;
using NexMud.Client.Settings;
using NexMud.Client.Commands;
using NexMud.Client.Automation;
using NexMud.Client.Runtime;
using NexMud.Client.Presentation;
using NexMud.Client.Logging;
using NexMud.Client.Knowledge;
using NexMud.Client.Interaction;
using NexMud.Client.Navigation;
using NexMud.Client.Scripting;
using NexMud.Scripting.Compilation;
using NexMud.Scripting.Events;
using NexMud.Scripting.Execution;
using NexMud.Scripting.Host;
using NexMud.Scripting.Permissions;
using NexMud.Scripting.Runtime;
using NexMud.Scripting.Scheduling;
using NexMud.Scripting.Storage;
using NexMud.Scripting.Time;
using NexMud.Scripting.Jint.Runtime;
using NexMud.Scripting.TypeScript.Declarations;
using NexMud.Scripting.TypeScript.Compiler;
using NexMud.Core.Actions;
using NexMud.Core.Events;
using NexMud.Core.Jev;
using NexMud.Core.State;
using NexMud.Gui;
using NexMud.Jev.Typesafe;
using NexMud.Transport.Telnet;
using NexMud.Transport.Text;

namespace NexMud.Tests;

public static class Program
{
    private static int _passed;
    private static int _failed;

    public static async Task<int> Main()
    {
        await RunAsync("connection options reject bad port", ConnectionOptionsRejectBadPort);
        await RunAsync("default telnet identity is neutral", DefaultTelnetIdentityIsNeutral);
        await RunAsync("copilot preset configures matrix", CopilotPresetConfiguresMatrix);
        await RunAsync("initial state authority is off", InitialStateAuthorityIsOff);
        await RunAsync("manual domain edit marks profile custom", ManualEditMarksCustom);
        await RunAsync("profile application is atomic", ProfileApplicationIsAtomic);
        await RunAsync("master Jev toggle preserves authority profile", MasterJevTogglePreservesAuthorityProfile);
        await RunAsync("state reducer is deterministic", StateReducerIsDeterministic);
        await RunAsync("observability events do not advance state version", ObservabilityEventsDoNotAdvanceStateVersion);
        await RunAsync("identical prompt does not advance state version", IdenticalPromptDoesNotAdvanceStateVersion);
        await RunAsync("lossless event subscription preserves burst", LosslessEventSubscriptionPreservesBurst);
        await RunAsync("concurrent event publishing preserves sequence order", ConcurrentEventPublishingPreservesSequenceOrder);
        await RunAsync("script permissions deny missing capabilities", ScriptPermissionsDenyMissingCapabilities);
        await RunAsync("script execution scope cancellation reaches owned tasks", ScriptExecutionScopeCancellationIsStructured);
        await RunAsync("script execution supervisor reports owned task faults", ScriptExecutionSupervisorReportsFaults);
        await RunAsync("script event subscriptions preserve per-module ordering", ScriptEventSubscriptionsAreSequential);
        await RunAsync("script event bridge observes reduced state before notification", ScriptEventBridgeFollowsStateReduction);
        await RunAsync("script storage is namespaced by module", ScriptStorageIsNamespaced);
        await RunAsync("script command provenance survives controlled dispatch", ScriptCommandProvenanceSurvivesDispatch);
        await RunAsync("script command adapter permits empty MUD input", ScriptCommandAdapterPermitsEmptyMudInput);
        await RunAsync("script host binds command provenance to module identity", ScriptHostBindsCommandProvenance);
        await RunAsync("Jint runtime executes hardened NexMUD module", JintRuntimeExecutesHardenedModule);
        await RunAsync("Jint runtime isolates independently loaded scripts", JintRuntimeIsolatesScriptEngines);
        await RunAsync("Jint runtime rejects dynamic string compilation", JintRuntimeRejectsDynamicCompilation);
        await RunAsync("Jint JavaScript time follows NexMUD script clock", JintRuntimeUsesScriptClock);
        await RunAsync("Jint reload preserves running instance when replacement fails", JintReloadPreservesOldInstanceOnFailure);
        await RunAsync("NexMUD TypeScript declarations track script API version", TypeScriptDeclarationsTrackApiVersion);
        await RunAsync("TypeScript compiler compiles and type-checks reference script", TypeScriptCompilerCompilesReferenceScript);
        await RunAsync("scripting vertical slice routes semantic vitals event through central command dispatch", ScriptingVerticalSliceRoutesVitalsToCommand);
        await RunAsync("scripting vertical slice isolates permission denial", ScriptingVerticalSliceDeniesMissingCommandPermission);
        await RunAsync("scripting unload discards late async host completion", ScriptingUnloadDiscardsLateHostCompletion);
        await RunAsync("scripting replay produces deterministic command without network", ScriptingReplayProducesDeterministicCommand);
        await RunAsync("scripting handler faults stay isolated", ScriptingHandlerFaultStaysIsolated);
        await RunAsync("scripting runaway handler is constrained", ScriptingRunawayHandlerIsConstrained);
        await RunAsync("scripting runtime diagnostics map to TypeScript source", ScriptingRuntimeDiagnosticsMapToTypeScriptSource);
        await RunAsync("Automation compiler is deterministic and capability-derived", AutomationCompilerIsDeterministic);
        await RunAsync("compiled Automation alias executes through Jint with Automation provenance", CompiledAutomationAliasExecutesThroughJint);
        await RunAsync("compiled Automation alias resumes after delayed host completion", CompiledAutomationAliasResumesAfterDelayedHostCompletion);
        await RunAsync("compiled Automation semantic condition replays deterministically through Jint", CompiledAutomationSemanticConditionReplaysDeterministically);
        await RunAsync("compiled Automation recurring timer follows virtual fixed-delay time", CompiledAutomationTimerUsesVirtualFixedDelay);
        await RunAsync("Automation SDK type-checks timers storage and stable state", AutomationSdkTypeChecksTimersStorageAndState);
        await RunAsync("Mapper command provenance and authority survive central dispatch", MapperCommandProvenanceAndAuthoritySurviveDispatch);
        await RunAsync("Mapper SDK type-checks route operations", MapperSdkTypeChecksRouteOperations);
        await RunAsync("Mapper SDK routes structured operations through Jint", MapperSdkRoutesStructuredOperationsThroughJint);
        await RunAsync("Mapper route compiler grants every generated SDK capability", MapperRouteCompilerGrantsGeneratedSdkCapabilities);
        await RunAsync("Mapper route explicit start signal begins production orchestration", MapperRouteExplicitStartSignalBeginsOrchestration);
        await RunAsync("Mapper route resumes after delayed pathfinding host completion", MapperRouteResumesAfterDelayedPathfinding);
        await RunAsync("Mapper route replay replans after a blocked step and completes", MapperRouteReplayReplansAndCompletes);
        await RunAsync("Mapper route pause resume and abort are deterministic", MapperRoutePauseResumeAndAbortAreDeterministic);
        await RunAsync("telnet parser handles fragmented gmcp", TelnetParserHandlesFragmentedGmcp);
        await RunAsync("telnet negotiation deduplicates repeated offers", TelnetNegotiationDeduplicatesOffers);
        await RunAsync("telnet terminal type follows MTTS sequence", TelnetTerminalTypeFollowsMttsSequence);
        await RunAsync("telnet charset selects UTF-8", TelnetCharsetSelectsUtf8);
        await RunAsync("telnet new-environ answers requested variables only", TelnetNewEnvironmentFiltersVariables);
        await RunAsync("MSDP parser preserves nested structures", MsdpParserPreservesNestedStructures);
        await RunAsync("telnet parser bounds oversized subnegotiation", TelnetParserBoundsOversizedSubnegotiation);
        await RunAsync("telnet prompt boundaries are observable", TelnetPromptBoundariesAreObservable);
        await RunAsync("MSDP discovery reports only supported desired variables", MsdpDiscoveryReportsDesiredVariables);
        await RunAsync("telnet charset rejects unsupported translation table", TelnetCharsetRejectsTranslationTable);
        await RunAsync("telnet TTYPE sequence resets when server disables it", TelnetTerminalTypeSequenceResetsOnDont);
        await RunAsync("ansi stripper handles fragmented csi", AnsiStripperHandlesFragmentedCsi);
        await RunAsync("ansi parser preserves fragmented styles", AnsiParserPreservesFragmentedStyles);
        await RunAsync("avendar prompt parser handles wrapped telemetry", AvendarPromptParserHandlesWrappedTelemetry);
        await RunAsync("avendar prompt marks unavailable exits unknown", AvendarPromptMarksUnknownExits);
        await RunAsync("avendar prompt rejects unknown exit codes", AvendarPromptRejectsUnknownExitCodes);
        await RunAsync("avendar prompt models none exits as known empty", AvendarPromptModelsNoneExits);
        await RunAsync("avendar stream extracts telemetry without losing bytes", AvendarStreamExtractsTelemetryWithoutLosingBytes);
        await RunAsync("game observations preserve source and monotonic ordering", GameObservationsPreserveSourceAndOrder);
        await RunAsync("avendar replay injects equivalent semantic observations", AvendarReplayInjectsEquivalentObservations);
        await RunAsync("avendar replay infers legacy group and room semantics", AvendarReplayInfersLegacyStructure);
        await RunAsync("legacy prompt parser preserves signed and over-max resources", LegacyPromptPreservesObservedResources);
        await RunAsync("group parser accepts negative HP and transformed names", GroupParserAcceptsNegativeHp);
        await RunAsync("effects parser preserves zero permanent and continuation modifiers", EffectsParserPreservesDurationsAndModifiers);
        await RunAsync("semantic parser emits clear movement and condition events", SemanticParserEmitsGameplaySemantics);
        await RunAsync("opaque special movement preserves unknown destination", OpaqueSpecialMovementPreservesUnknownDestination);
        await RunAsync("scan semantics remain separate from current room occupants", ScanSemanticsDoNotPolluteRoom);
        await RunAsync("command journal does not infer prompt acknowledgement", CommandJournalDoesNotInferPromptAcknowledgement);
        await RunAsync("command journal preserves repeated command bursts", CommandJournalPreservesRepeatedCommandBursts);
        await RunAsync("semantic corpus fixtures cover all expert source logs", SemanticCorpusFixturesCoverExpertLogs);
        await RunAsync("avendar adapter preserves raw server text for display", AvendarAdapterPreservesRawServerText);
        await RunAsync("avendar adapter follows explicit login input state", AvendarAdapterFollowsExplicitLoginInputState);
        await RunAsync("avendar movement responses correlate denied and successful commands", AvendarMovementResponsesCorrelateCommands);
        await RunAsync("avendar where response emits explicit area", AvendarWhereResponseEmitsExplicitArea);
        await RunAsync("avendar stream recovers from unterminated telemetry", AvendarStreamRecoversFromUnterminatedTelemetry);
        await RunAsync("prompt display filter slurps fragmented telemetry only", PromptDisplayFilterSlurpsFragmentedTelemetry);
        await RunAsync("transcript highlighter preserves text", TranscriptHighlighterPreservesText);
        await RunAsync("transcript display normalizes terminal line endings without double spacing", TranscriptDisplayNormalizesCrLf);
        await RunAsync("transcript typography uses fixed terminal density", TranscriptTypographyUsesFixedTerminalDensity);
        await RunAsync("transcript logger writes verbatim server text", TranscriptLoggerWritesVerbatimServerText);
        await RunAsync("transcript logger supports plain and json formats", TranscriptLoggerSupportsPlainAndJsonFormats);
        await RunAsync("command aliases expand positional arguments", CommandAliasesExpandArguments);
        await RunAsync("alias expansions execute command batches", AliasExpansionsExecuteCommandBatches);
        await RunAsync("command separator batches preserve order and escapes", CommandSeparatorSplitsInput);
        await RunAsync("interaction input pipeline records history and preserves command order", InteractionInputPipelinePreservesHistoryAndOrder);
        await RunAsync("generated interaction commands do not pollute manual history", InteractionGeneratedCommandsDoNotPolluteHistory);
        await RunAsync("interaction history restores in-progress input", InteractionHistoryRestoresInProgressBuffer);
        await RunAsync("interaction completion preserves MUD names and reverses stable cycle", InteractionCompletionPreservesMudNames);
        await RunAsync("interaction keybindings resolve context and expose conflicts", InteractionKeybindingsResolveContextAndConflicts);
        await RunAsync("output transformation preserves source while substituting highlighting and gagging", OutputTransformationPreservesSourceData);
        await RunAsync("output preserves terminal line structure across chunks", OutputPreservesTerminalLineStructure);
        await RunAsync("output highlighting spans ANSI run boundaries", OutputHighlightSpansAnsiRuns);
        await RunAsync("world buffer subscribers cannot fault output processing", WorldBufferSubscriberFailureIsIsolated);
        await RunAsync("interaction replay produces deterministic rendered output", InteractionReplayIsDeterministic);
        await RunAsync("interaction local echo remains separate from server source", InteractionLocalEchoIsSeparate);
        await RunAsync("output rule failures are isolated and diagnosed", OutputRuleFailuresAreIsolated);
        await RunAsync("command provenance enum values remain backward compatible", CommandOriginValuesRemainCompatible);
        await RunAsync("world scrollback is bounded and searchable", WorldScrollbackIsBoundedAndSearchable);
        await RunAsync("automation expressions support OR predicates variables and entity functions", AutomationExpressionsSupportRichPredicates);
        await RunAsync("automation rules yield to the human override window without dropping commands", AutomationRulesWaitForHumanOverride);
        await RunAsync("automation workflow settings round trip", AutomationWorkflowSettingsRoundTrip);
        await RunAsync("automation workflow dispatches sequential commands without blocking its event reader", AutomationWorkflowDispatchesWithoutDeadlock);
        await RunAsync("automation workflow retries are bounded and observable", AutomationWorkflowRetriesAreBounded);
        await RunAsync("automation workflow concurrency is enforced for manual starts", AutomationWorkflowConcurrencyIsEnforced);
        await RunAsync("automation workflow continue mode records recovery without terminal failure", AutomationWorkflowContinueModeCompletes);
        await RunAsync("automation workflows carry event context into navigation", AutomationWorkflowUsesEventContextAndNavigation);
        await RunAsync("avendar room observation parser captures room entities", AvendarRoomContentsParserCapturesRoomBlock);
        await RunAsync("unindented fixture is not classified as a MOB", UnindentedFixtureIsNotOccupant);
        await RunAsync("avendar room parser separates speech from occupants", AvendarRoomParserStopsAtSpeech);
        await RunAsync("room parser models corpses independently", RoomParserModelsCorpse);
        await RunAsync("room parser stops at wrapped speech start", RoomParserStopsAtWrappedSpeechStart);
        await RunAsync("duplicate room names produce distinct observed ids", DuplicateRoomNamesProduceDistinctIds);
        await RunAsync("parenthesized exits preserve unknown qualifier semantics", ParenthesizedExitsPreserveUnknownQualifier);
        await RunAsync("closed door movement failure clears pending traversal", ClosedDoorMovementFailureClearsPendingTraversal);
        await RunAsync("denied movement response cannot poison the next mapper traversal", DeniedMovementResponseDoesNotPoisonNextTraversal);
        await RunAsync("mapper persists failed closed exits as navigation knowledge", MapperPersistsClosedDoorFailure);
        await RunAsync("successful traversal clears stale blocked exit state", SuccessfulTraversalClearsStaleBlockedExitState);
        await RunAsync("golden room transcript fixture parses", GoldenRoomTranscriptFixtureParses);
        await RunAsync("golden room state preserves environment entities and observations", GoldenRoomStatePreservesCanonicalContext);
        await RunAsync("ambient room occupant text is not combat condition", AmbientOccupantTextIsNotCombatCondition);
        await RunAsync("room contents snapshot replaces current room contents", RoomContentsSnapshotReplacesCurrentRoom);
        await RunAsync("room change clears stale contents", RoomChangeClearsStaleContents);
        await RunAsync("enemy death removes one matching room occupant", EnemyDeathRemovesOneMatchingOccupant);
        await RunAsync("occupant departure removes one matching room occupant", OccupantDepartureRemovesOneMatchingOccupant);
        await RunAsync("avendar damage parser normalizes documented tiers", AvendarDamageParserNormalizesTiers);
        await RunAsync("avendar score parser extracts deterministic state", AvendarScoreParserExtractsState);
        await RunAsync("avendar score parser handles populated Randolph score", AvendarScoreParserHandlesRandolphFixture);
        await RunAsync("avendar score core publishes without waiting for a later command", AvendarScoreCorePublishesImmediately);
        await RunAsync("Randolph score reduces to canonical character state", RandolphScoreReducesToCanonicalCharacterState);
        await RunAsync("gameplay projections share canonical character and room state", GameplayProjectionsUseCanonicalState);
        await RunAsync("avendar skills parser spans pager pages", AvendarSkillsParserSpansPagerPages);
        await RunAsync("avendar spells parser models explicit empty catalog", AvendarSpellsParserModelsExplicitEmptyCatalog);
        await RunAsync("avendar equipment parser preserves slots and brands", AvendarEquipmentParserPreservesSlotsAndBrands);
        await RunAsync("avendar inventory parser captures carried items", AvendarInventoryParserCapturesCarriedItems);
        await RunAsync("inventory snapshot populates canonical carried items", InventorySnapshotPopulatesCanonicalItems);
        await RunAsync("avendar item id parser captures common identification", AvendarItemIdParserCapturesCommonIdentification);
        await RunAsync("avendar semantic parser captures item provenance", AvendarSemanticParserCapturesItemProvenance);
        await RunAsync("combat loot burst preserves corpse provenance", CombatLootBurstPreservesCorpseProvenance);
        await RunAsync("corpse destruction removes matching room corpse", CorpseDestructionRemovesMatchingRoomCorpse);
        await RunAsync("avendar ability help parser captures skill reference", AvendarAbilityHelpParserCapturesSkillReference);
        await RunAsync("equipment snapshot preserves duplicate empty slots", EquipmentSnapshotPreservesDuplicateEmptySlots);
        await RunAsync("ability parser classifies only known domains", AbilityParserClassifiesKnownDomains);
        await RunAsync("partial ability snapshots preserve known entries", PartialAbilitySnapshotsPreserveKnownEntries);
        await RunAsync("client settings round trip Jev authority", ClientSettingsRoundTripJevAuthority);
        await RunAsync("client settings allow compact transcript sizes", ClientSettingsAllowCompactTranscriptSizes);
        await RunAsync("runtime Jev save persists before returning", RuntimeJevSavePersistsBeforeReturning);
        await RunAsync("dirt kicking eligibility honors terrain", DirtKickingEligibilityHonorsTerrain);
        await RunAsync("skill eligibility remains unknown before skills are observed", SkillEligibilityUnknownBeforeObservation);
        await RunAsync("dirt eligibility remains unknown before terrain is observed", DirtEligibilityUnknownWithoutTerrain);
        await RunAsync("backstab eligibility stays unknown without weapon type", BackstabEligibilityRequiresWeaponKnowledge);
        await RunAsync("identified equipped weapon resolves backstab legality", IdentifiedEquippedWeaponResolvesBackstabLegality);
        await RunAsync("zero-cost ability tolerates unknown mana", ZeroCostAbilityToleratesUnknownMana);
        await RunAsync("combat Jev request exposes only eligible interventions", CombatJevRequestFiltersCapabilities);
        await RunAsync("combat Jev request decomposes parallel typed questions", CombatJevRequestUsesTypedQuestions);
        await RunAsync("combat Jev request includes bounded persistent context", CombatJevRequestIncludesPersistentContext);
        await RunAsync("idle combat Jev can choose an observed grind target", IdleCombatJevCanChooseObservedTarget);
        await RunAsync("idle combat excludes recent social speakers from grind targets", IdleCombatJevProtectsRecentSpeakers);
        await RunAsync("idle combat protection recognizes occupant target aliases", IdleCombatJevProtectsTargetAlias);
        await RunAsync("idle combat Jev defers to recovery when depleted", IdleCombatJevDefersToRecoveryWhenDepleted);
        await RunAsync("recovery Jev request exposes recover and rest", RecoveryJevRequestExposesActions);
        await RunAsync("navigation Jev request exposes traversable exits", NavigationJevRequestExposesExits);
        await RunAsync("Jev persistent context includes ability help and executed command sources", JevPersistentContextIncludesAbilityHelpAndCommandSources);
        await RunAsync("blank command is dispatched and observable before execution", BlankCommandDispatches);
        await RunAsync("sensitive command is redacted from events", SensitiveCommandIsRedacted);
        await RunAsync("automated stale action is rejected", AutomatedStaleActionIsRejected);
        await RunAsync("deterministic rule action bypasses Jev authority", RuleActionBypassesJevAuthority);
        await RunAsync("semantic gains update known state", SemanticGainsUpdateKnownState);
        await RunAsync("multi-denomination currency updates known state", MultiDenominationCurrencyUpdatesKnownState);
        await RunAsync("queued movement observations build directed topology", QueuedMovementBuildsTopology);
        await RunAsync("mapper browses entities and visual graph from durable knowledge", MapperBrowseSearchAndGraph);
        await RunAsync("mapper neighborhood follows incoming edges from a fresh room", MapperNeighborhoodFollowsIncomingEdges);
        await RunAsync("mapper layout uses cardinal directions as presentation hints", MapperTopologyPreservesDirectionalCoordinates);
        await RunAsync("mapper layout tolerates folded MUD geometry without topology errors", MapperTopologyToleratesFoldedGeometry);
        await RunAsync("mapper does not create exits for non-directional relocation", MapperRelocationDoesNotCreateExit);
        await RunAsync("codex aggregates MOBs by area and tracks loot sources", CodexAggregatesMobsByAreaAndTracksLootSources);
        await RunAsync("skill improvement marks skill snapshot stale", SkillImprovementMarksSkillStale);
        await RunAsync("session parser recognizes sensitive login mode", SessionParserRecognizesLoginModes);
        await RunAsync("command input mode masks password and restores normal input", CommandInputModeTransitionsAreSafe);
        await RunAsync("password input is excluded from command history", PasswordInputIsExcludedFromHistory);
        await RunAsync("prompt telemetry triggers recovery only", PromptTelemetryTriggersRecoveryOnly);
        await RunAsync("already standing feedback repairs posture state", AlreadyStandingFeedbackRepairsPostureState);
        await RunAsync("recovery Jev never guesses stand from unknown posture", RecoveryJevDoesNotGuessStand);
        await RunAsync("autonomy supervisor suppresses repeated stand commands", AutonomySupervisorSuppressesRepeatedStand);
        await RunAsync("navigation Jev suppresses immediate backtracking when alternatives exist", NavigationJevSuppressesImmediateBacktrack);
        await RunAsync("navigation Jev permits backtracking at a dead end", NavigationJevPermitsDeadEndBacktrack);
        await RunAsync("semantic combat changes trigger Jev combat", SemanticCombatTriggersJev);
        await RunAsync("unrelated enemy death does not end current combat", UnrelatedDeathDoesNotEndCombat);
        await RunAsync("combat target change clears stale target state", CombatTargetChangeClearsStaleState);
        await RunAsync("typesafe preserves injected http timeout", TypesafePreservesInjectedHttpTimeout);
        await RunAsync("typesafe choice response maps to trace", TypesafeChoiceMapsToTrace);
        await RunAsync("typesafe parallel score and noul map to trace", TypesafeParallelQuestionsMapToTrace);

        Console.WriteLine($"Passed: {_passed}, Failed: {_failed}");
        return _failed == 0 ? 0 : 1;
    }

    private static Task ScriptPermissionsDenyMissingCapabilities()
    {
        ScriptPermissionSet permissions = new(ScriptCapability.ReadState | ScriptCapability.SubscribeEvents);
        Assert.True(permissions.Allows(ScriptCapability.ReadState), "ReadState should be granted.");
        Assert.False(permissions.Allows(ScriptCapability.SendCommands), "SendCommands should not be granted.");
        Assert.Throws<UnauthorizedAccessException>(() => permissions.Demand(ScriptCapability.SendCommands));
        return Task.CompletedTask;
    }

    private static async Task ScriptExecutionScopeCancellationIsStructured()
    {
        IScriptExecutionScope owner = supervisor.CreateOwner(ScriptOwnerKind.AutomationWorkflow, "test workflow");
        TaskCompletionSource<bool> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task work = owner.RunAsync("wait", async cancellationToken =>
        {
            started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        await started.Task;
        owner.Cancel();
        try
        {
            await work;
        }
        catch (OperationCanceledException)
        {
        }
        Assert.True(owner.IsCancellationRequested, "Cancelling an owner must cancel its outstanding work.");
        await owner.DisposeAsync();
    }

    private static async Task ScriptExecutionSupervisorReportsFaults()
    {
        ScriptTaskFault? observed = null;
        supervisor.TaskFaulted += fault => observed = fault;
        IScriptExecutionScope owner = supervisor.CreateOwner(ScriptOwnerKind.MapperRoute, "fault-test");
        try
        {
            await owner.RunAsync("explode", _ => Task.FromException(new InvalidOperationException("boom")));
        }
        catch (InvalidOperationException exception)
        {
            Assert.Equal("boom", exception.Message);
        }

        ScriptTaskFault fault = observed ?? throw new InvalidOperationException("Owned task fault was not reported.");
        Assert.Equal(ScriptOwnerKind.MapperRoute, fault.OwnerKind);
        Assert.Equal("explode", fault.Operation);
        Assert.Equal("boom", fault.Exception.Message);
        await owner.DisposeAsync();
    }

    private static async Task ScriptEventSubscriptionsAreSequential()
    {
        await using ScriptEventHub hub = new();
        IScriptExecutionScope owner = supervisor.CreateOwner(ScriptOwnerKind.UserScript, "event-test", new ScriptModuleId("test.events"));
        List<long> observed = [];
        TaskCompletionSource<bool> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using IScriptEventSubscription subscription = hub.Subscribe(
            owner,
            ScriptEventFilter.For(ScriptEventTypes.RoomEntered),
            async (envelope, cancellationToken) =>
            {
                observed.Add(envelope.Sequence);
                if (observed.Count == 3) completed.TrySetResult(true);
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
            });

        JsonElement payload = JsonSerializer.SerializeToElement(new { room = "test" });
        for (long sequence = 1; sequence <= 3; sequence++)
        {
            await hub.PublishAsync(new ScriptEventEnvelope(ScriptEventTypes.RoomEntered, sequence, DateTimeOffset.UtcNow, "test", payload));
        }
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.SequenceEqual(new long[] { 1, 2, 3 }, observed);
        await owner.DisposeAsync();
        await hub.PublishAsync(new ScriptEventEnvelope(ScriptEventTypes.RoomEntered, 4, DateTimeOffset.UtcNow, "test", payload));
        await Task.Delay(25);
        Assert.SequenceEqual(new long[] { 1, 2, 3 }, observed);
    }

    private static async Task ScriptEventBridgeFollowsStateReduction()
    {
        await using EventPipeline events = new();
        StateReducer reducer = new(events.StateEvents);
        await using ScriptEventHub hub = new();
        ClientScriptEventBridge bridge = new(events.SubscribeLossless(), reducer, hub);
        await using ScriptExecutionSupervisor supervisor = new();
        IScriptExecutionScope owner = supervisor.CreateOwner(
            ScriptOwnerKind.UserScript,
            "event-state-order",
            new ScriptModuleId("test.event-state-order"));
        TaskCompletionSource<string?> observedRoom = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using IScriptEventSubscription subscription = hub.Subscribe(
            owner,
            ScriptEventFilter.For(ScriptEventTypes.RoomEntered),
            (envelope, _) =>
            {
                observedRoom.TrySetResult(reducer.Current.Room.Id);
                return Task.CompletedTask;
            });

        using CancellationTokenSource cancellation = new();
        Task reducerTask = reducer.RunAsync(cancellation.Token);
        Task bridgeTask = bridge.RunAsync(cancellation.Token);
        await events.PublishAsync(
            new RoomChanged("order:test", "Order Test", new[] { "north" }),
            "test");

        string? roomId = await observedRoom.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("order:test", roomId);

        cancellation.Cancel();
        await IgnoreCancellation(reducerTask);
        await IgnoreCancellation(bridgeTask);
        await owner.DisposeAsync();
    }

    private static async Task ScriptStorageIsNamespaced()
    {
        string databasePath = Path.Combine(Path.GetTempPath(), $"nexmud-script-storage-{Guid.NewGuid():N}.db");
        SqliteScriptStorage first = new(new ScriptModuleId("module.one"), databasePath);
        SqliteScriptStorage second = new(new ScriptModuleId("module.two"), databasePath);
        await first.SetAsync("state", "{\"value\":1}");
        await second.SetAsync("state", "{\"value\":2}");
        Assert.Equal("{\"value\":1}", await first.GetAsync("state"));
        Assert.Equal("{\"value\":2}", await second.GetAsync("state"));
        try { File.Delete(databasePath); } catch { }
        try { File.Delete(databasePath + "-wal"); } catch { }
        try { File.Delete(databasePath + "-shm"); } catch { }
    }

    private static async Task ScriptCommandProvenanceSurvivesDispatch()
    {
        await using EventPipeline events = new();
        ChannelReader<EventEnvelope> observer = events.SubscribeLossless();
        StateReducer reducer = new(events.StateEvents);
        FakeSender sender = new();
        JevAuthorityService authority = new(events);
        ActionProcessor actions = new(sender, reducer, authority, events);
        using CancellationTokenSource cancellation = new();
        Task reducerTask = reducer.RunAsync(cancellation.Token);
        Task actionTask = actions.RunAsync(cancellation.Token);
        ClientScriptCommands commands = new(actions, reducer);

        ScriptCommandResult queued = await commands.SendAsync(new ScriptCommandRequest(
            "look",
            ScriptCommandOrigin.Automation,
            "automation:test",
            "Test automation",
            "Regression test",
            ModuleId: "automation.test"));
        Assert.True(queued.Accepted, "Shared script command adapter should accept the queued command.");

        ActionDispatching? dispatch = null;
        ActionExecuted? executed = null;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        while (dispatch is null || executed is null)
        {
            EventEnvelope envelope = await observer.ReadAsync(timeout.Token);
            if (envelope.Payload is ActionDispatching dispatching && dispatching.ActionId == queued.ActionId) dispatch = dispatching;
            if (envelope.Payload is ActionExecuted actionExecuted && actionExecuted.ActionId == queued.ActionId) executed = actionExecuted;
        }

        CommandProvenance dispatchProvenance = dispatch.Provenance ?? throw new InvalidOperationException("Dispatch provenance was not preserved.");
        CommandProvenance executedProvenance = executed.Provenance ?? throw new InvalidOperationException("Executed provenance was not preserved.");
        Assert.Equal(CommandOrigin.Automation, dispatchProvenance.Origin);
        Assert.Equal("automation:test", dispatchProvenance.OwnerId);
        Assert.Equal("Test automation", dispatchProvenance.OwnerName);
        Assert.Equal("Regression test", dispatchProvenance.Reason);
        Assert.Equal("automation.test", dispatchProvenance.ModuleId);
        Assert.Equal(CommandOrigin.Automation, executedProvenance.Origin);
        Assert.Equal("look", Assert.Single(sender.Commands));

        cancellation.Cancel();
        await IgnoreCancellation(reducerTask);
        await IgnoreCancellation(actionTask);
    }

    private static async Task ScriptCommandAdapterPermitsEmptyMudInput()
    {
        await using EventPipeline events = new();
        StateReducer reducer = new(events.StateEvents);
        FakeSender sender = new();
        JevAuthorityService authority = new(events);
        ActionProcessor actions = new(sender, reducer, authority, events);
        using CancellationTokenSource cancellation = new();
        Task reducerTask = reducer.RunAsync(cancellation.Token);
        Task actionTask = actions.RunAsync(cancellation.Token);
        ClientScriptCommands commands = new(actions, reducer);

        ScriptCommandResult queued = await commands.SendAsync(new ScriptCommandRequest(
            string.Empty,
            ScriptCommandOrigin.User,
            "user",
            "User",
            "Pager continue"));

        Assert.True(queued.Accepted, "Empty input is valid MUD input and must pass through the shared command adapter.");
        await WaitUntilAsync(() => sender.Commands.Count == 1);
        Assert.Equal(string.Empty, sender.Commands[0]);

        cancellation.Cancel();
        await IgnoreCancellation(reducerTask);
        await IgnoreCancellation(actionTask);
    }

    private static async Task ScriptHostBindsCommandProvenance()
    {
        ScriptModuleId moduleId = new("user.test");
        ScriptPermissionSet permissions = new(ScriptCapability.SendCommands);
        RecordingScriptCommands commands = new();
        await using ScriptEventHub events = new();
        string databasePath = Path.Combine(Path.GetTempPath(), $"nexmud-host-binding-{Guid.NewGuid():N}.db");
        SqliteScriptStorage storage = new(moduleId, databasePath);
        CapabilityScriptHost host = new(
            moduleId,
            permissions,
            events,
            commands,
            new StaticScriptState(),
            new NoopScriptMapper(),
            new NoopScriptCodex(),
            storage,
            new ScriptScheduler(),
            new NoopScriptUi(),
            new NoopScriptLog(),
            ScriptCommandOrigin.Script,
            "Test user script");

        ScriptCommandResult result = await host.Commands.SendAsync(new ScriptCommandRequest(
            "look",
            ScriptCommandOrigin.User,
            "spoofed-owner",
            "Spoofed User",
            ModuleId: "spoofed.module",
            AutomationId: "spoofed.automation",
            AutomationType: "alias",
            TriggerId: "spoofed.trigger"));

        Assert.True(result.Accepted, "Bound host should forward an allowed command.");
        ScriptCommandRequest captured = commands.LastRequest ?? throw new InvalidOperationException("Command was not forwarded.");
        Assert.Equal(ScriptCommandOrigin.Script, captured.Origin);
        Assert.Equal(moduleId.Value, captured.OwnerId);
        Assert.Equal("Test user script", captured.OwnerName);
        Assert.Equal(moduleId.Value, captured.ModuleId);
        Assert.Equal<string?>(null, captured.AutomationId);
        Assert.Equal<string?>(null, captured.AutomationType);
        Assert.Equal<string?>(null, captured.TriggerId);

        try { File.Delete(databasePath); } catch { }
        try { File.Delete(databasePath + "-wal"); } catch { }
        try { File.Delete(databasePath + "-shm"); } catch { }
    }

    private static async Task JintRuntimeExecutesHardenedModule()
    {
        await using ScriptExecutionSupervisor localSupervisor = new();
        await using ScriptEventHub events = new();
        ScriptModuleId moduleId = new("test.jint.hardened");
        CapabilityScriptHost host = CreateJintTestHost(moduleId, events, ScriptCapability.ReadState);
        CompiledScriptPackage package = TestJavaScriptPackage(
            moduleId,
            "hardened",
            """
            import { nex } from "@nexmud/api";
            export function activate(context) {
              if (typeof System !== "undefined") throw new Error("CLR System global leaked");
              if (typeof importNamespace !== "undefined") throw new Error("CLR namespace import leaked");
              const state = nex.state.snapshot();
              if (!state.connected) throw new Error("host state bridge failed");
              if (context.apiVersion !== "1") throw new Error("activation context missing");
            }
            """);

        await using JintScriptRuntime runtime = new(localSupervisor);
        await runtime.LoadAsync(package, host);
        ScriptModuleSnapshot loaded = Assert.Single(runtime.Snapshot());
        Assert.Equal(moduleId, loaded.Id);
        Assert.True(loaded.Loaded, "Jint module should be running after successful activation.");
        Assert.Equal("jint-4.16.3", runtime.RuntimeName);
    }

    private static async Task JintRuntimeIsolatesScriptEngines()
    {
        await using ScriptExecutionSupervisor localSupervisor = new();
        await using ScriptEventHub events = new();
        await using JintScriptRuntime runtime = new(localSupervisor);

        foreach (string idText in new[] { "test.jint.isolation.one", "test.jint.isolation.two" })
        {
            ScriptModuleId moduleId = new(idText);
            CapabilityScriptHost host = CreateJintTestHost(moduleId, events, ScriptCapability.ReadState);
            CompiledScriptPackage package = TestJavaScriptPackage(
                moduleId,
                idText,
                """
                export function activate() {
                  if (globalThis.__nexIsolationMarker !== undefined) throw new Error("engine global leaked from another script");
                  globalThis.__nexIsolationMarker = "owned";
                }
                """);
            await runtime.LoadAsync(package, host);
        }

        Assert.Equal(2, runtime.Snapshot().Count);
    }

    private static async Task JintRuntimeRejectsDynamicCompilation()
    {
        await using ScriptExecutionSupervisor localSupervisor = new();
        await using ScriptEventHub events = new();
        await using JintScriptRuntime runtime = new(localSupervisor);
        ScriptModuleId moduleId = new("test.jint.no-eval");
        CapabilityScriptHost host = CreateJintTestHost(moduleId, events, ScriptCapability.ReadState);
        CompiledScriptPackage package = TestJavaScriptPackage(
            moduleId,
            "no-eval",
            """
            export function activate() {
              eval("globalThis.__forbidden = true");
            }
            """);

        bool rejected = false;
        try
        {
            await runtime.LoadAsync(package, host);
        }
        catch
        {
            rejected = true;
        }
        Assert.True(rejected, "Dynamic string compilation must be disabled for production script engines.");
    }

    private static async Task JintRuntimeUsesScriptClock()
    {
        await using ScriptExecutionSupervisor localSupervisor = new();
        await using ScriptEventHub events = new();
        DateTimeOffset now = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_123);
        ScriptScheduler scheduler = new(new FixedScriptClock(now));
        ScriptModuleId moduleId = new("test.jint.clock");
        CapabilityScriptHost host = CreateJintTestHost(moduleId, events, ScriptCapability.ReadState, scheduler);
        CompiledScriptPackage package = TestJavaScriptPackage(
            moduleId,
            "clock",
            """
            export function activate() {
              if (Date.now() !== 1700000000123) throw new Error(`unexpected Date.now ${Date.now()}`);
            }
            """);

        await using JintScriptRuntime runtime = new(localSupervisor);
        await runtime.LoadAsync(package, host);
        Assert.True(Assert.Single(runtime.Snapshot()).Loaded, "Jint Date should use the injected NexMUD script clock.");
    }

    private static async Task JintReloadPreservesOldInstanceOnFailure()
    {
        await using ScriptExecutionSupervisor localSupervisor = new();
        await using ScriptEventHub events = new();
        await using JintScriptRuntime runtime = new(localSupervisor);
        ScriptModuleId moduleId = new("test.jint.reload");
        CapabilityScriptHost host = CreateJintTestHost(moduleId, events, ScriptCapability.ReadState);
        await runtime.LoadAsync(TestJavaScriptPackage(moduleId, "reload", "export function activate() {}"), host);

        bool rejected = false;
        try
        {
            await runtime.ReloadAsync(
                TestJavaScriptPackage(moduleId, "reload-bad", "export function activate() { throw new Error('bad replacement'); }"),
                host);
        }
        catch
        {
            rejected = true;
        }

        Assert.True(rejected, "A failing replacement must reject reload.");
        ScriptModuleSnapshot snapshot = Assert.Single(runtime.Snapshot());
        Assert.Equal(moduleId, snapshot.Id);
        Assert.True(snapshot.Loaded, "The previously running instance must survive failed hot reload.");
    }

    private static Task TypeScriptDeclarationsTrackApiVersion()
    {
        Assert.Equal(ScriptApiVersion.Current, NexMudTypeDeclarations.ApiVersion);
        Assert.True(NexMudTypeDeclarations.Source.Contains("@nexmud/api", StringComparison.Ordinal),
            "TypeScript declarations must expose the NexMUD API module.");
        Assert.True(NexMudTypeDeclarations.Source.Contains("declare const nex", StringComparison.Ordinal),
            "TypeScript declarations must expose the public nex API object.");
        Assert.True(NexMudTypeDeclarations.Source.Contains("\"character.vitalsChanged\"", StringComparison.Ordinal),
            "TypeScript declarations must expose the stable vitals event contract.");
        Assert.True(NexMudTypeDeclarations.Source.Contains("Promise<NexMudCommandResult>", StringComparison.Ordinal),
            "commands.send must expose a typed Promise result.");
        return Task.CompletedTask;
    }

    private static async Task TypeScriptCompilerCompilesReferenceScript()
    {
        CompiledScriptPackage package = await CompileScriptAsync(
            "nexmud.reference.vitals-policy",
            "NexMUD Reference Vitals Policy",
            await File.ReadAllTextAsync(Path.Combine("scripts", "reference", "vitals-policy", "main.ts")),
            ReferenceVitalsCapabilities);
        Assert.Equal("main.js", package.Manifest.Entrypoint);
        CompiledScriptModule module = package.Modules["main.js"];
        Assert.True(!string.IsNullOrWhiteSpace(module.SourceMap), "TypeScript compilation must emit a source map.");
        Assert.Equal("main.ts", module.OriginalSourcePath);

        TypeScriptCompiler compiler = new();
        ScriptCompileResult invalidEvent = await compiler.CompileAsync(new ScriptCompileRequest(
            ReferenceManifest("test.invalid-event", ReferenceVitalsCapabilities),
            ScriptSourceLanguage.TypeScript,
            [new ScriptSourceFile("main.ts", "export function activate(): void { nex.events.on(\"character.notReal\", () => {}); }")]));
        Assert.True(!invalidEvent.Success, "Unknown public event names must fail TypeScript checking.");
    }

    private static async Task ScriptingVerticalSliceRoutesVitalsToCommand()
    {
        CompiledScriptPackage package = await CompileReferenceVitalsPackageAsync();
        await using EventPipeline events = new();
        ChannelReader<EventEnvelope> observer = events.SubscribeLossless();
        StateReducer reducer = new(events.StateEvents);
        FakeSender sender = new();
        JevAuthorityService authority = new(events);
        ActionProcessor actions = new(sender, reducer, authority, events);
        ClientScriptCommands commands = new(actions, reducer);
        await using ScriptEventHub scriptEvents = new();
        ClientScriptEventBridge bridge = new(events.SubscribeLossless(), reducer, scriptEvents);
        await using ScriptExecutionSupervisor localSupervisor = new();
        RecordingDiagnosticsSink diagnostics = new();
        RecordingScriptLog scriptLog = new();
        await using JintScriptRuntime runtime = new(localSupervisor, diagnostics: diagnostics);
        ScriptScheduler scheduler = new();
        CapabilityScriptHost host = new(
            package.Manifest.Id,
            new ScriptPermissionSet(package.Manifest.Permissions),
            scriptEvents,
            commands,
            new ClientScriptStateHost(reducer),
            new NoopScriptMapper(),
            new NoopScriptCodex(),
            new MemoryScriptStorage(package.Manifest.Id),
            scheduler,
            new NoopScriptUi(),
            new NoopScriptLog(),
            ScriptCommandOrigin.Script,
            package.Manifest.Name);

        using CancellationTokenSource cancellation = new();
        Task reducerTask = reducer.RunAsync(cancellation.Token);
        Task actionTask = actions.RunAsync(cancellation.Token);
        Task bridgeTask = bridge.RunAsync(cancellation.Token);
        await runtime.LoadAsync(package, host);
        await events.PublishAsync(new CharacterVitalsChanged(20, 100, 80, 100, 70, 100), "test.replay");

        Guid? vitalsEventId = null;
        ActionDispatching? dispatch = null;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(4));
        while (vitalsEventId is null || dispatch is null)
        {
            EventEnvelope envelope = await observer.ReadAsync(timeout.Token);
            if (envelope.Payload is CharacterVitalsChanged) vitalsEventId = envelope.EventId;
            if (envelope.Payload is ActionDispatching { Command: "flee" } action) dispatch = action;
        }

        Assert.Equal("flee", Assert.Single(sender.Commands));
        CommandProvenance provenance = dispatch.Provenance ?? throw new InvalidOperationException("Script command provenance was missing.");
        Assert.Equal(CommandOrigin.Script, provenance.Origin);
        Assert.Equal(package.Manifest.Id.Value, provenance.ModuleId);
        Assert.Equal(package.Manifest.Version, provenance.ScriptVersion);
        Assert.True(provenance.ScriptInstanceId is not null, "Script instance provenance is required.");
        Assert.True(provenance.InvocationId is not null, "Script invocation provenance is required.");
        Assert.Equal(vitalsEventId, provenance.EventId);
        Assert.True(provenance.ParentOperationId is not null, "Host operation provenance is required.");
        Assert.True(diagnostics.Records.Any(record => record.Kind == ScriptDiagnosticKind.InvocationCompleted),
            "Completed script invocation should be observable.");
        await WaitUntilAsync(() => scriptLog.Entries.Count == 1);
        ScriptLogEntry logEntry = Assert.Single(scriptLog.Entries);
        Assert.Equal(ScriptLogLevel.Warning, logEntry.Level);
        Assert.True(logEntry.DataJson?.Contains("healthPercent", StringComparison.Ordinal) == true,
            "Structured script diagnostics must retain log data.");

        cancellation.Cancel();
        await IgnoreCancellation(reducerTask);
        await IgnoreCancellation(actionTask);
        await IgnoreCancellation(bridgeTask);
    }

    private static async Task ScriptingVerticalSliceDeniesMissingCommandPermission()
    {
        ScriptCapability capabilities = ScriptCapability.SubscribeEvents | ScriptCapability.ReadState | ScriptCapability.Log;
        CompiledScriptPackage package = await CompileScriptAsync(
            "test.vitals.permission-denied",
            "Permission denied vitals policy",
            await File.ReadAllTextAsync(Path.Combine("scripts", "reference", "vitals-policy", "main.ts")),
            capabilities);
        await using ScriptExecutionSupervisor localSupervisor = new();
        await using ScriptEventHub events = new();
        RecordingScriptCommands commands = new();
        RecordingDiagnosticsSink diagnostics = new();
        CapabilityScriptHost host = CreateJintTestHost(package.Manifest.Id, events, capabilities, commands: commands);
        await using JintScriptRuntime runtime = new(localSupervisor, diagnostics: diagnostics);
        await runtime.LoadAsync(package, host);

        await events.PublishAsync(VitalsScriptEvent(20));
        await WaitUntilAsync(() => diagnostics.Records.Any(record => record.Kind == ScriptDiagnosticKind.PermissionDenied));
        Assert.True(commands.LastRequest is null, "Permission denial must occur before central command emission.");
        Assert.True(diagnostics.Records.Any(record => record.Kind == ScriptDiagnosticKind.InvocationFaulted),
            "Denied async host calls must fault only their owning invocation.");
        Assert.True(Assert.Single(runtime.Snapshot()).Loaded, "A handler permission failure must not unload the entire runtime.");
    }

    private static async Task ScriptingUnloadDiscardsLateHostCompletion()
    {
        ScriptCapability capabilities = ScriptCapability.SubscribeEvents | ScriptCapability.SendCommands;
        CompiledScriptPackage package = await CompileScriptAsync(
            "test.vitals.late-completion",
            "Late completion policy",
            "export function activate(): void { nex.events.on(\"character.vitalsChanged\", async () => { await nex.commands.send(\"flee\"); }); }",
            capabilities);
        await using ScriptExecutionSupervisor localSupervisor = new();
        await using ScriptEventHub events = new();
        BlockingScriptCommands commands = new();
        RecordingDiagnosticsSink diagnostics = new();
        CapabilityScriptHost host = CreateJintTestHost(package.Manifest.Id, events, capabilities, commands: commands);
        await using JintScriptRuntime runtime = new(localSupervisor, diagnostics: diagnostics);
        await runtime.LoadAsync(package, host);
        await events.PublishAsync(VitalsScriptEvent(20));
        await commands.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        ScriptInvocationSnapshot invocation = Assert.Single(Assert.Single(runtime.Snapshot()).Invocations ?? []);
        Assert.Equal(ScriptInvocationStatus.WaitingOnHost, invocation.Status);
        Assert.True(await runtime.UnloadAsync(package.Manifest.Id), "Loaded script should unload.");
        await commands.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
        commands.Complete();
        await Task.Delay(50);
        Assert.Equal(0, runtime.Snapshot().Count);
    }

    private static async Task ScriptingReplayProducesDeterministicCommand()
    {
        CompiledScriptPackage package = await CompileReferenceVitalsPackageAsync();
        await using ScriptExecutionSupervisor localSupervisor = new();
        await using ScriptEventHub events = new();
        RecordingScriptCommands commands = new();
        CapabilityScriptHost host = CreateJintTestHost(
            package.Manifest.Id,
            events,
            ReferenceVitalsCapabilities,
            commands: commands);
        await using JintScriptRuntime runtime = new(localSupervisor);
        await runtime.LoadAsync(package, host);

        ScriptEventEnvelope replay = VitalsScriptEvent(20);
        await events.PublishAsync(replay);
        await WaitUntilAsync(() => commands.RequestCount == 1);

        ScriptCommandRequest request = commands.LastRequest
            ?? throw new InvalidOperationException("Replay did not produce a command.");
        Assert.Equal("flee", request.Command);
        Assert.Equal(replay.EventId, request.EventId);
        Assert.Equal(1, commands.RequestCount);
    }

    private static async Task ScriptingHandlerFaultStaysIsolated()
    {
        ScriptCapability capabilities = ScriptCapability.SubscribeEvents | ScriptCapability.SendCommands;
        CompiledScriptPackage faulty = await CompileScriptAsync(
            "test.vitals.faulty",
            "Faulty policy",
            "export function activate(): void { nex.events.on(\"character.vitalsChanged\", () => { throw new Error(\"test\"); }); }",
            capabilities);
        CompiledScriptPackage healthy = await CompileScriptAsync(
            "test.vitals.healthy",
            "Healthy policy",
            "export function activate(): void { nex.events.on(\"character.vitalsChanged\", async () => { await nex.commands.send(\"look\"); }); }",
            capabilities);
        await using ScriptExecutionSupervisor localSupervisor = new();
        await using ScriptEventHub events = new();
        RecordingDiagnosticsSink diagnostics = new();
        RecordingScriptCommands healthyCommands = new();
        await using JintScriptRuntime runtime = new(localSupervisor, diagnostics: diagnostics);
        await runtime.LoadAsync(faulty, CreateJintTestHost(faulty.Manifest.Id, events, capabilities));
        await runtime.LoadAsync(healthy, CreateJintTestHost(healthy.Manifest.Id, events, capabilities, commands: healthyCommands));

        await events.PublishAsync(VitalsScriptEvent(20));
        await WaitUntilAsync(() =>
            diagnostics.Records.Any(record =>
                record.ScriptId == faulty.Manifest.Id && record.Kind == ScriptDiagnosticKind.InvocationFaulted) &&
            healthyCommands.RequestCount == 1);

        Assert.Equal(2, runtime.Snapshot().Count);
        Assert.True(runtime.Snapshot().All(snapshot => snapshot.Loaded),
            "One handler failure must not stop another script instance.");
        Assert.Equal("look", healthyCommands.LastRequest?.Command);
    }

    private static async Task ScriptingRunawayHandlerIsConstrained()
    {
        ScriptCapability capabilities = ScriptCapability.SubscribeEvents;
        CompiledScriptPackage package = await CompileScriptAsync(
            "test.vitals.runaway",
            "Runaway policy",
            "export function activate(): void { nex.events.on(\"character.vitalsChanged\", () => { while (true) {} }); }",
            capabilities,
            ScriptRuntimeProfile.UserScript);
        await using ScriptExecutionSupervisor localSupervisor = new();
        await using ScriptEventHub events = new();
        RecordingDiagnosticsSink diagnostics = new();
        await using JintScriptRuntime runtime = new(localSupervisor, diagnostics: diagnostics);
        await runtime.LoadAsync(package, CreateJintTestHost(package.Manifest.Id, events, capabilities));

        await events.PublishAsync(VitalsScriptEvent(20));
        await WaitUntilAsync(
            () => runtime.Snapshot().Single().Status == ScriptStatus.Faulted,
            TimeSpan.FromSeconds(3));

        Assert.True(diagnostics.Records.Any(record =>
                record.Kind is ScriptDiagnosticKind.Timeout or ScriptDiagnosticKind.ResourceLimitExceeded),
            "A runaway handler must terminate at the Jint execution boundary.");
    }

    private static async Task ScriptingRuntimeDiagnosticsMapToTypeScriptSource()
    {
        CompiledScriptPackage package = await CompileScriptAsync(
            "test.source-map",
            "Source map policy",
            "export function activate(): void {\n  throw new Error(\"source-map-test\");\n}",
            ScriptCapability.None);
        await using ScriptExecutionSupervisor localSupervisor = new();
        await using ScriptEventHub events = new();
        RecordingDiagnosticsSink diagnostics = new();
        await using JintScriptRuntime runtime = new(localSupervisor, diagnostics: diagnostics);

        bool rejected = false;
        try
        {
            await runtime.LoadAsync(package, CreateJintTestHost(package.Manifest.Id, events, ScriptCapability.None));
        }
        catch
        {
            rejected = true;
        }

        Assert.True(rejected, "Activation error must reject script load.");
        ScriptDiagnosticRecord diagnostic = diagnostics.Records.Last(record =>
            record.Kind == ScriptDiagnosticKind.UncaughtException &&
            record.ScriptId == package.Manifest.Id);
        Assert.Equal("main.ts", diagnostic.Location?.SourceFile);
        Assert.True(diagnostic.Location?.Line is > 0, "Runtime diagnostic must include a TypeScript line number.");
    }

    private static Task AutomationCompilerIsDeterministic()
    {
        AutomationProgram[] programs =
        [
            new AutomationProgram(
                "automation.alias.heal",
                "heal",
                AutomationProgramType.Alias,
                new AliasAutomationTrigger("heal"),
                [],
                [
                    new SendCommandsAutomationAction(["quaff potion", "eat herb"]),
                    new DelayAutomationAction(25),
                    new SetStorageAutomationAction("lastAction", "heal")
                ]),
            new AutomationProgram(
                "automation.trigger.guard",
                "guard appeared",
                AutomationProgramType.TextTrigger,
                new TextAutomationTrigger("A guard arrives", HighlightMatchMode.Literal, false, TriggerScope.Line, 250, false, false),
                [new StorageValueAutomationCondition("enabled", "==", true)],
                [new SendCommandAutomationAction("consider guard")],
                Priority: 10)
        ];

        AutomationJavaScriptCompiler compiler = new();
        CompiledAutomationArtifact first = compiler.Compile(programs, ScriptApiVersion.Current);
        CompiledAutomationArtifact second = compiler.Compile(programs, ScriptApiVersion.Current);

        Assert.Equal(first.ContentHash, second.ContentHash);
        Assert.Equal(first.RuntimeGeneration, second.RuntimeGeneration);
        Assert.Equal(first.Package.Modules["main.js"].JavaScript, second.Package.Modules["main.js"].JavaScript);
        ScriptCapability permissions = first.Package.Manifest.Permissions;
        Assert.True(permissions.HasFlag(ScriptCapability.SubscribeEvents), "Automation event matching must derive events.subscribe.");
        Assert.True(permissions.HasFlag(ScriptCapability.SendCommands), "Automation command actions must derive commands.send.");
        Assert.True(permissions.HasFlag(ScriptCapability.CreateTimers), "Automation delay actions must derive timers capability.");
        Assert.True(permissions.HasFlag(ScriptCapability.ReadScriptStorage), "Automation storage conditions must derive storage.read capability.");
        Assert.True(permissions.HasFlag(ScriptCapability.WriteScriptStorage), "Automation storage mutations must derive storage.write capability.");
        string source = first.Package.Modules["main.js"].JavaScript;
        Assert.True(source.Contains("@nexmud/api", StringComparison.Ordinal), "Generated Automation must import only the public SDK.");
        Assert.False(source.Contains("__nex", StringComparison.Ordinal), "Automation compiler must not emit private bridge calls.");
        Assert.False(source.Contains("Jint", StringComparison.Ordinal), "Automation compiler must not emit Jint implementation names.");
        Assert.False(source.Contains("System.", StringComparison.Ordinal), "Automation compiler must not emit CLR names.");
        Assert.True(source.Contains("automation:${program.id}", StringComparison.Ordinal), "Automation storage keys must be scoped by Automation id.");
        Assert.True(first.SourceMap.Any(entry => entry.AutomationId == "automation.alias.heal" && entry.ActionIndex == 0),
            "Compiler output must map generated actions back to Automation IR nodes.");
        Assert.Throws<InvalidOperationException>(() => compiler.Compile(
            [new AutomationProgram(
                "automation.invalid",
                "Invalid",
                AutomationProgramType.Timer,
                new AliasAutomationTrigger("x"),
                [],
                [new SendCommandAutomationAction("look")])],
            ScriptApiVersion.Current));

        AutomationProgram readOnlyStorage = new(
            "automation.storage.read",
            "Storage reader",
            AutomationProgramType.SemanticTrigger,
            new SemanticAutomationTrigger("character.vitalsChanged"),
            [new StorageValueAutomationCondition("armed", "==", true)],
            [new LogAutomationAction("debug", "checked")]);
        ScriptCapability readPermissions = compiler.Compile([readOnlyStorage], ScriptApiVersion.Current).Package.Manifest.Permissions;
        Assert.True(readPermissions.HasFlag(ScriptCapability.ReadScriptStorage), "Storage conditions must derive storage.read.");
        Assert.False(readPermissions.HasFlag(ScriptCapability.WriteScriptStorage), "Read-only storage conditions must not derive storage.write.");

        Dictionary<string, object?> firstValue = new() { ["beta"] = 2, ["alpha"] = 1 };
        Dictionary<string, object?> secondValue = new() { ["alpha"] = 1, ["beta"] = 2 };
        AutomationProgram firstCanonical = new(
            "automation.storage.canonical",
            "Canonical storage",
            AutomationProgramType.SemanticTrigger,
            new SemanticAutomationTrigger("connection.stateChanged"),
            [],
            [new SetStorageAutomationAction("payload", firstValue)]);
        AutomationProgram secondCanonical = firstCanonical with
        {
            Actions = [new SetStorageAutomationAction("payload", secondValue)]
        };
        Assert.Equal(
            compiler.Compile([firstCanonical], ScriptApiVersion.Current).ContentHash,
            compiler.Compile([secondCanonical], ScriptApiVersion.Current).ContentHash);

        Assert.Throws<InvalidOperationException>(() => compiler.Compile(
            [new AutomationProgram(
                "automation.bad-regex",
                "Bad regex",
                AutomationProgramType.TextTrigger,
                new TextAutomationTrigger("[", HighlightMatchMode.Regex, false, TriggerScope.Line, 0, false, false),
                [],
                [new LogAutomationAction("warn", "should not compile")])],
            ScriptApiVersion.Current));
        return Task.CompletedTask;
    }

    private static async Task CompiledAutomationAliasExecutesThroughJint()
    {
        AutomationProgram program = new(
            "automation.alias.kk",
            "Attack alias",
            AutomationProgramType.Alias,
            new AliasAutomationTrigger("kk {target}"),
            [],
            [new SendCommandAutomationAction("kill ${target}")]);
        CompiledAutomationArtifact artifact = new AutomationJavaScriptCompiler().Compile([program], ScriptApiVersion.Current);

        await using ScriptExecutionSupervisor localSupervisor = new();
        await using ScriptEventHub events = new();
        RecordingScriptCommands commands = new();
        CapabilityScriptHost host = CreateJintTestHost(
            artifact.Package.Manifest.Id,
            events,
            artifact.Package.Manifest.Permissions,
            commands: commands,
            commandOrigin: ScriptCommandOrigin.Automation);
        await using JintScriptRuntime runtime = new(localSupervisor);
        await runtime.LoadAsync(artifact.Package, host);

        Guid eventId = Guid.NewGuid();
        await events.PublishAsync(new ScriptEventEnvelope(
            ScriptEventTypes.AutomationAliasMatched,
            1,
            DateTimeOffset.UtcNow,
            "test.automation",
            JsonSerializer.SerializeToElement(new
            {
                automationId = program.Id,
                runtimeGeneration = artifact.RuntimeGeneration,
                triggerId = $"alias:{program.Id}",
                input = "kk guard",
                tail = "guard",
                captures = new[] { "kk guard", "guard" },
                values = new Dictionary<string, string?> { ["target"] = "guard" }
            }),
            EventId: eventId));

        await WaitUntilAsync(() => commands.RequestCount == 1);
        ScriptCommandRequest request = commands.LastRequest ?? throw new InvalidOperationException("Compiled Automation did not emit a command.");
        Assert.Equal("kill guard", request.Command);
        Assert.Equal(ScriptCommandOrigin.Automation, request.Origin);
        Assert.Equal(program.Id, request.AutomationId);
        Assert.Equal("alias", request.AutomationType);
        Assert.Equal($"alias:{program.Id}", request.TriggerId);
        Assert.Equal(eventId, request.EventId);
        Assert.True(request.InvocationId.HasValue, "Automation command must carry the Jint invocation id.");
    }


    private static async Task CompiledAutomationAliasResumesAfterDelayedHostCompletion()
    {
        AutomationProgram program = new(
            "automation.alias.delayed",
            "Delayed alias",
            AutomationProgramType.Alias,
            new AliasAutomationTrigger("zz"),
            [],
            [new SendCommandAutomationAction("look"), new SendCommandAutomationAction("score")]);
        CompiledAutomationArtifact artifact = new AutomationJavaScriptCompiler().Compile([program], ScriptApiVersion.Current);

        await using ScriptExecutionSupervisor localSupervisor = new();
        await using ScriptEventHub events = new();
        DelayedFirstScriptCommands commands = new();
        CapabilityScriptHost host = CreateJintTestHost(
            artifact.Package.Manifest.Id,
            events,
            artifact.Package.Manifest.Permissions,
            commands: commands,
            commandOrigin: ScriptCommandOrigin.Automation);
        await using JintScriptRuntime runtime = new(localSupervisor);
        await runtime.LoadAsync(artifact.Package, host);

        await events.PublishAsync(new ScriptEventEnvelope(
            ScriptEventTypes.AutomationAliasMatched,
            1,
            DateTimeOffset.UtcNow,
            "test.automation",
            JsonSerializer.SerializeToElement(new
            {
                automationId = program.Id,
                runtimeGeneration = artifact.RuntimeGeneration,
                triggerId = $"alias:{program.Id}",
                input = "zz",
                tail = "",
                captures = new[] { "zz" },
                values = new Dictionary<string, string?>()
            }),
            EventId: Guid.NewGuid()));

        await commands.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, commands.RequestCount);
        commands.ReleaseFirst();
        await WaitUntilAsync(() => commands.RequestCount == 2);
        Assert.SequenceEqual(new[] { "look", "score" }, commands.Commands);
    }

    private static async Task CompiledAutomationSemanticConditionReplaysDeterministically()
    {
        AutomationProgram program = new(
            "automation.semantic.emergency-flee",
            "Emergency flee",
            AutomationProgramType.SemanticTrigger,
            new SemanticAutomationTrigger(ScriptEventTypes.CharacterVitalsChanged),
            [new EventFieldComparisonAutomationCondition("health.percent", "<", 25)],
            [new SendCommandAutomationAction("flee")]);
        CompiledAutomationArtifact artifact = new AutomationJavaScriptCompiler().Compile([program], ScriptApiVersion.Current);

        await using ScriptExecutionSupervisor localSupervisor = new();
        await using ScriptEventHub events = new();
        RecordingScriptCommands commands = new();
        CapabilityScriptHost host = CreateJintTestHost(
            artifact.Package.Manifest.Id,
            events,
            artifact.Package.Manifest.Permissions,
            commands: commands,
            commandOrigin: ScriptCommandOrigin.Automation);
        await using JintScriptRuntime runtime = new(localSupervisor);
        await runtime.LoadAsync(artifact.Package, host);

        await events.PublishAsync(VitalsScriptEvent(20));
        await WaitUntilAsync(() => commands.RequestCount == 1);
        Assert.Equal("flee", commands.LastRequest?.Command);
        Assert.Equal(program.Id, commands.LastRequest?.AutomationId);
        Assert.Equal("semanticTrigger", commands.LastRequest?.AutomationType);

        await events.PublishAsync(VitalsScriptEvent(80));
        await Task.Delay(25);
        Assert.Equal(1, commands.RequestCount);
    }

    private static async Task CompiledAutomationTimerUsesVirtualFixedDelay()
    {
        AutomationProgram program = new(
            "automation.timer.score",
            "Score timer",
            AutomationProgramType.Timer,
            new TimerAutomationTrigger(30_000, Repeat: true),
            [],
            [new SendCommandAutomationAction("score")]);
        CompiledAutomationArtifact artifact = new AutomationJavaScriptCompiler().Compile([program], ScriptApiVersion.Current);

        await using ScriptExecutionSupervisor localSupervisor = new();
        await using ScriptEventHub events = new();
        ManualScriptClock clock = new(DateTimeOffset.UnixEpoch);
        ScriptScheduler scheduler = new(clock);
        RecordingScriptCommands commands = new();
        CapabilityScriptHost host = CreateJintTestHost(
            artifact.Package.Manifest.Id,
            events,
            artifact.Package.Manifest.Permissions,
            scheduler,
            commands,
            commandOrigin: ScriptCommandOrigin.Automation);
        await using JintScriptRuntime runtime = new(localSupervisor);
        await runtime.LoadAsync(artifact.Package, host);

        await WaitUntilAsync(() => clock.PendingDelayCount == 1);
        clock.Advance(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => commands.RequestCount == 1);
        await WaitUntilAsync(() => clock.PendingDelayCount == 1);
        Assert.Equal("score", commands.LastRequest?.Command);

        clock.Advance(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => commands.RequestCount == 2);
        await WaitUntilAsync(() => clock.PendingDelayCount == 1);

        Assert.True(await runtime.UnloadAsync(artifact.Package.Manifest.Id), "Automation runtime should unload cleanly.");
        clock.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(25);
        Assert.Equal(2, commands.RequestCount);
    }

    private static async Task AutomationSdkTypeChecksTimersStorageAndState()
    {
        ScriptCapability capabilities =
            ScriptCapability.ReadState |
            ScriptCapability.CreateTimers |
            ScriptCapability.ReadScriptStorage |
            ScriptCapability.WriteScriptStorage |
            ScriptCapability.Log;
        CompiledScriptPackage package = await CompileScriptAsync(
            "test.automation.sdk",
            "Automation SDK type surface",
            """
            import { nex } from "@nexmud/api";
            export function activate(): void {
              const roomId: string | null = nex.state.room.id;
              const combatActive: boolean = nex.state.combat.active;
              const handle = nex.timers.after(10, async () => {
                await nex.storage.set("room", roomId);
                const exists: boolean = await nex.storage.has("room");
                if (exists && combatActive) await nex.log.info("combat room stored");
              });
              nex.timers.cancel(handle);
            }
            """,
            capabilities);
        Assert.Equal("main.js", package.Manifest.Entrypoint);
    }



    private static async Task MapperCommandProvenanceAndAuthoritySurviveDispatch()
    {
        await using EventPipeline events = new();
        ChannelReader<EventEnvelope> observer = events.SubscribeLossless();
        StateReducer reducer = new(events.StateEvents);
        FakeSender sender = new();
        JevAuthorityService jevAuthority = new(events);
        ActionProcessor actions = new(sender, reducer, jevAuthority, events);
        MapperNavigationAuthority navigationAuthority = new();
        Guid routeExecutionId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        Assert.True(navigationAuthority.TryAcquire(routeExecutionId), "Mapper route should acquire navigation authority.");
        using CancellationTokenSource cancellation = new();
        Task reducerTask = reducer.RunAsync(cancellation.Token);
        Task actionTask = actions.RunAsync(cancellation.Token);
        ClientScriptCommands commands = new(actions, reducer, navigationAuthority: navigationAuthority);

        ScriptCommandResult queued = await commands.SendAsync(new ScriptCommandRequest(
            "north",
            ScriptCommandOrigin.Mapper,
            "mapper-route",
            "Mapper route",
            "route movement",
            RouteExecutionId: routeExecutionId,
            RouteId: "route-a-b",
            RouteStep: 2,
            RouteTotalSteps: 5));
        Assert.True(queued.Accepted, "Owned Mapper movement should enter central dispatch.");

        ActionDispatching? dispatch = null;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        while (dispatch is null)
        {
            EventEnvelope envelope = await observer.ReadAsync(timeout.Token);
            if (envelope.Payload is ActionDispatching dispatching && dispatching.ActionId == queued.ActionId) dispatch = dispatching;
        }

        CommandProvenance provenance = dispatch.Provenance ?? throw new InvalidOperationException("Mapper provenance was not preserved.");
        Assert.Equal(CommandOrigin.Mapper, provenance.Origin);
        Assert.Equal<Guid?>(routeExecutionId, provenance.RouteExecutionId);
        Assert.Equal("route-a-b", provenance.RouteId);
        Assert.Equal<int?>(2, provenance.RouteStep);
        Assert.Equal<int?>(5, provenance.RouteTotalSteps);
        await WaitUntilAsync(() => sender.Commands.Count == 1);
        Assert.Equal("north", sender.Commands[0]);

        navigationAuthority.Release(routeExecutionId);
        ScriptCommandResult stale = await commands.SendAsync(new ScriptCommandRequest(
            "east",
            ScriptCommandOrigin.Mapper,
            "mapper-route",
            "Mapper route",
            RouteExecutionId: routeExecutionId,
            RouteId: "route-a-b",
            RouteStep: 3,
            RouteTotalSteps: 5));
        Assert.False(stale.Accepted, "A Mapper movement from a released route must not pass dispatch.");
        Assert.Equal(1, sender.Commands.Count);

        cancellation.Cancel();
        await IgnoreCancellation(reducerTask);
        await IgnoreCancellation(actionTask);
    }

    private static async Task MapperSdkTypeChecksRouteOperations()
    {
        ScriptCapability capabilities =
            ScriptCapability.ReadMapper |
            ScriptCapability.MapperPathfind |
            ScriptCapability.MapperMove;
        CompiledScriptPackage package = await CompileScriptAsync(
            "test.mapper.sdk.types",
            "Mapper SDK type surface",
            """
            import { nex, NexMudMovementResult } from "@nexmud/api";
            export async function activate(): Promise<void> {
              const room = await nex.mapper.currentRoom();
              if (!room) return;
              const plan = await nex.mapper.findPath({ roomId: room.id });
              if (!plan || plan.steps.length === 0) return;
              const step = plan.steps[0];
              const result: NexMudMovementResult = await nex.mapper.move(step.direction, {
                fromRoomId: step.fromRoomId,
                expectedRoomId: step.expectedRoomId,
                routeId: plan.routeId,
                routeStep: step.sequence,
                routeTotalSteps: plan.steps.length
              });
              if (result.kind === "moved") {
                const destination: string | null = result.toRoomId;
                void destination;
              }
            }
            """,
            capabilities);
        Assert.Equal("main.js", package.Manifest.Entrypoint);
    }

    private static async Task MapperSdkRoutesStructuredOperationsThroughJint()
    {
        await using ScriptExecutionSupervisor supervisor = new();
        await using ScriptEventHub events = new();
        RecordingMapperHost mapper = new("room-a", "room-b", "north");
        ScriptCapability capabilities = ScriptCapability.ReadMapper | ScriptCapability.MapperPathfind | ScriptCapability.MapperMove | ScriptCapability.CreateTimers;
        ScriptModuleId moduleId = new("test.mapper.sdk");
        CapabilityScriptHost host = CreateJintTestHost(
            moduleId,
            events,
            capabilities,
            scheduler: new ScriptScheduler(new FixedScriptClock(DateTimeOffset.UnixEpoch)),
            mapper: mapper);
        CompiledScriptPackage package = TestJavaScriptPackage(
            moduleId,
            "mapper-sdk",
            """
            import { nex } from "@nexmud/api";
            export function activate() {
              nex.timers.after(0, async () => {
                const room = await nex.mapper.currentRoom();
                if (!room || room.id !== "room-a") throw new Error("currentRoom failed");
                const plan = await nex.mapper.findPath({ roomId: "room-b" });
                if (!plan || plan.steps.length !== 1) throw new Error("findPath failed");
                const result = await nex.mapper.move(plan.steps[0].direction, {
                  fromRoomId: plan.steps[0].fromRoomId,
                  expectedRoomId: plan.steps[0].expectedRoomId,
                  routeExecutionId: "11111111-1111-1111-1111-111111111111",
                  routeId: plan.routeId,
                  routeStep: plan.steps[0].sequence,
                  routeTotalSteps: plan.steps.length
                });
                if (result.kind !== "moved" || result.toRoomId !== "room-b") throw new Error("move failed");
              });
            }
            """,
            capabilities);

        await using JintScriptRuntime runtime = new(supervisor);
        await runtime.LoadAsync(package, host);
        await WaitUntilAsync(() => mapper.MoveRequests.Count == 1);
        ScriptMoveRequest request = Assert.Single(mapper.MoveRequests);
        Assert.Equal("room-a", request.FromRoomId);
        Assert.Equal("room-b", request.ExpectedRoomId);
        Assert.Equal("route-room-a-room-b", request.RouteId);
        Assert.Equal(1, request.RouteStep);
    }

    private static Task MapperRouteCompilerGrantsGeneratedSdkCapabilities()
    {
        CompiledScriptPackage package = MapperRouteScriptCompiler.Compile(
            Guid.Parse("66666666-6666-6666-6666-666666666666"),
            "D",
            new MapperPreferences(AutoMoveStepDelayMilliseconds: 0));

        ScriptCapability permissions = package.Manifest.Permissions;
        ScriptCapability required =
            ScriptCapability.ReadMapper |
            ScriptCapability.MapperPathfind |
            ScriptCapability.MapperMove |
            ScriptCapability.CreateTimers |
            ScriptCapability.SubscribeEvents |
            ScriptCapability.MapperRouteObserve;

        Assert.Equal(required, permissions & required);
        Assert.True(permissions.HasFlag(ScriptCapability.MapperRouteObserve),
            "Generated Mapper route modules subscribe to mapper.route.* events and must be granted MapperRouteObserve.");
        return Task.CompletedTask;
    }

    private static async Task MapperRouteExplicitStartSignalBeginsOrchestration()
    {
        await using ScriptExecutionSupervisor supervisor = new();
        await using ScriptEventHub events = new();
        ReplayRouteMapper mapper = ReplayRouteMapper.SimpleScenario();
        MapperRouteControl control = new();
        Guid executionId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        MapperRouteExecutionContext context = new(
            executionId,
            "D",
            "Destination D",
            false,
            2,
            control,
            (_, _) => Task.CompletedTask);
        RouteBoundScriptMapperHost routeMapper = new(mapper, context);
        ManualScriptClock clock = new(DateTimeOffset.UnixEpoch);
        ScriptScheduler scheduler = new(clock);
        CompiledScriptPackage package = MapperRouteScriptCompiler.Compile(
            executionId,
            "D",
            new MapperPreferences(AutoMoveStepDelayMilliseconds: 0));
        CapabilityScriptHost host = CreateJintTestHost(
            package.Manifest.Id,
            events,
            package.Manifest.Permissions,
            scheduler: scheduler,
            mapper: routeMapper,
            commandOrigin: ScriptCommandOrigin.Mapper);

        await using JintScriptRuntime runtime = new(supervisor);
        await runtime.LoadAsync(package, host);
        await Task.Delay(25);
        Assert.Equal(0, mapper.MoveRequests.Count);
        Assert.True(clock.PendingDelayCount > 0, "Fallback timer should remain pending until explicit start is signaled.");

        await events.PublishAsync(new ScriptEventEnvelope(
            MapperRouteScriptCompiler.StartEventType,
            1,
            DateTimeOffset.UnixEpoch,
            "navigator",
            JsonSerializer.SerializeToElement(new { routeExecutionId = executionId.ToString("D") }),
            EventId: Guid.NewGuid()));

        await WaitUntilAsync(() => mapper.CurrentRoomId == "D");
        Assert.True(mapper.MoveRequests.Count > 0, "Explicit route-start signal must begin route execution without advancing timer time.");
    }

    private static async Task MapperRouteResumesAfterDelayedPathfinding()
    {
        await using ScriptExecutionSupervisor supervisor = new();
        await using ScriptEventHub events = new();
        DelayedFindPathMapper mapper = new();
        MapperRouteControl control = new();
        Guid executionId = Guid.Parse("77777777-7777-7777-7777-777777777777");
        MapperRouteExecutionContext context = new(
            executionId,
            "D",
            "Destination D",
            false,
            2,
            control,
            (_, _) => Task.CompletedTask);
        RouteBoundScriptMapperHost routeMapper = new(mapper, context);
        ManualScriptClock clock = new(DateTimeOffset.UnixEpoch);
        ScriptScheduler scheduler = new(clock);
        CompiledScriptPackage package = MapperRouteScriptCompiler.Compile(
            executionId,
            "D",
            new MapperPreferences(AutoMoveStepDelayMilliseconds: 0));
        CapabilityScriptHost host = CreateJintTestHost(
            package.Manifest.Id,
            events,
            package.Manifest.Permissions,
            scheduler: scheduler,
            mapper: routeMapper,
            commandOrigin: ScriptCommandOrigin.Mapper);

        await using JintScriptRuntime runtime = new(supervisor);
        await runtime.LoadAsync(package, host);
        await events.PublishAsync(new ScriptEventEnvelope(
            MapperRouteScriptCompiler.StartEventType,
            1,
            DateTimeOffset.UnixEpoch,
            "navigator",
            JsonSerializer.SerializeToElement(new { routeExecutionId = executionId.ToString("D") }),
            EventId: Guid.NewGuid()));

        await mapper.FindPathStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, mapper.MoveRequests.Count);
        mapper.CompleteFindPath();
        await WaitUntilAsync(() => mapper.MoveRequests.Count == 1);
        Assert.Equal("north", mapper.MoveRequests[0].Direction);
        Assert.Equal("D", mapper.CurrentRoomId);
    }

    private static async Task MapperRouteReplayReplansAndCompletes()
    {
        await using ScriptExecutionSupervisor supervisor = new();
        await using ScriptEventHub events = new();
        ReplayRouteMapper mapper = ReplayRouteMapper.BlockedReplanScenario();
        MapperRouteControl control = new();
        List<MapperRouteRuntimeUpdate> updates = [];
        Guid executionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        MapperRouteExecutionContext context = new(
            executionId,
            "D",
            "Destination D",
            false,
            3,
            control,
            (update, _) => { lock (updates) updates.Add(update); return Task.CompletedTask; });
        RouteBoundScriptMapperHost routeMapper = new(mapper, context);
        MapperPreferences settings = new(AutoMoveStepDelayMilliseconds: 0, AutoMoveMaximumReplans: 3);
        CompiledScriptPackage package = MapperRouteScriptCompiler.Compile(executionId, "D", settings);
        CapabilityScriptHost host = CreateJintTestHost(
            package.Manifest.Id,
            events,
            package.Manifest.Permissions,
            scheduler: new ScriptScheduler(new FixedScriptClock(DateTimeOffset.UnixEpoch)),
            mapper: routeMapper,
            commandOrigin: ScriptCommandOrigin.Mapper);

        await using JintScriptRuntime runtime = new(supervisor);
        await runtime.LoadAsync(package, host);
        await WaitUntilAsync(() => mapper.CurrentRoomId == "D");

        Assert.SequenceEqual(new[] { "north", "east", "west", "south" }, mapper.MoveRequests.Select(request => request.Direction).ToArray());
        lock (updates)
        {
            Assert.True(updates.Any(update => update.Kind == MapperRouteLifecycleKind.Blocked), "Blocked movement should be observable.");
            Assert.True(updates.Any(update => update.Kind == MapperRouteLifecycleKind.Replanning), "Blocked movement should trigger bounded replanning.");
            Assert.True(updates.Any(update => update.Kind == MapperRouteLifecycleKind.Completed), "Route completion must follow confirmed arrival.");
        }
    }

    private static async Task MapperRoutePauseResumeAndAbortAreDeterministic()
    {
        await using ScriptExecutionSupervisor supervisor = new();
        await using ScriptEventHub events = new();
        ReplayRouteMapper mapper = ReplayRouteMapper.PauseResumeScenario();
        MapperRouteControl control = new();
        Assert.True(control.Pause(), "Route should enter paused state before execution.");
        Guid executionId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        MapperRouteExecutionContext context = new(
            executionId,
            "D",
            "Destination D",
            false,
            2,
            control,
            (_, _) => Task.CompletedTask);
        RouteBoundScriptMapperHost routeMapper = new(mapper, context);
        CompiledScriptPackage package = MapperRouteScriptCompiler.Compile(
            executionId,
            "D",
            new MapperPreferences(AutoMoveStepDelayMilliseconds: 0));
        CapabilityScriptHost host = CreateJintTestHost(
            package.Manifest.Id,
            events,
            package.Manifest.Permissions,
            scheduler: new ScriptScheduler(new FixedScriptClock(DateTimeOffset.UnixEpoch)),
            mapper: routeMapper,
            commandOrigin: ScriptCommandOrigin.Mapper);

        await using JintScriptRuntime runtime = new(supervisor);
        await runtime.LoadAsync(package, host);
        await Task.Delay(25);
        Assert.Equal(0, mapper.MoveRequests.Count);

        mapper.SetCurrentRoom("X");
        Assert.True(control.Resume(), "Paused route should resume.");
        await WaitUntilAsync(() => mapper.CurrentRoomId == "D");
        ScriptMoveRequest resumedMove = Assert.Single(mapper.MoveRequests);
        Assert.Equal("east", resumedMove.Direction);
        Assert.Equal("X", resumedMove.FromRoomId);

        ReplayRouteMapper abortedMapper = ReplayRouteMapper.PauseResumeScenario();
        MapperRouteControl abortedControl = new();
        Assert.True(abortedControl.Pause(), "Second route should start paused for abort replay.");
        Guid abortedExecutionId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        MapperRouteExecutionContext abortedContext = new(
            abortedExecutionId,
            "D",
            "Destination D",
            false,
            2,
            abortedControl,
            (_, _) => Task.CompletedTask);
        RouteBoundScriptMapperHost abortedRouteMapper = new(abortedMapper, abortedContext);
        CompiledScriptPackage abortedPackage = MapperRouteScriptCompiler.Compile(
            abortedExecutionId,
            "D",
            new MapperPreferences(AutoMoveStepDelayMilliseconds: 0));
        CapabilityScriptHost abortedHost = CreateJintTestHost(
            abortedPackage.Manifest.Id,
            events,
            abortedPackage.Manifest.Permissions,
            scheduler: new ScriptScheduler(new FixedScriptClock(DateTimeOffset.UnixEpoch)),
            mapper: abortedRouteMapper,
            commandOrigin: ScriptCommandOrigin.Mapper);

        await runtime.ReloadAsync(abortedPackage, abortedHost);
        abortedControl.Abort();
        await Task.Delay(25);
        Assert.Equal(0, abortedMapper.MoveRequests.Count);
    }

    private static ScriptEventEnvelope VitalsScriptEvent(double percent) => new(
        ScriptEventTypes.CharacterVitalsChanged,
        1,
        DateTimeOffset.UtcNow,
        "test.replay",
        JsonSerializer.SerializeToElement(new
        {
            health = new { current = (int)percent, maximum = 100, percent },
            mana = new { current = 100, maximum = 100, percent = 100.0 },
            movement = new { current = 100, maximum = 100, percent = 100.0 },
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        }),
        EventId: Guid.NewGuid());

    private static readonly ScriptCapability ReferenceVitalsCapabilities =
        ScriptCapability.SubscribeEvents | ScriptCapability.ReadState | ScriptCapability.SendCommands | ScriptCapability.Log;

    private static ScriptManifest ReferenceManifest(string id, ScriptCapability capabilities) => new(
        new ScriptModuleId(id),
        id,
        "1.0.0",
        ScriptApiVersion.Current,
        "main.ts",
        capabilities,
        ScriptOwnerKind.System,
        ScriptRuntimeProfile.InternalTrusted);

    private static async Task<CompiledScriptPackage> CompileReferenceVitalsPackageAsync() =>
        await CompileScriptAsync(
            "nexmud.reference.vitals-policy",
            "NexMUD Reference Vitals Policy",
            await File.ReadAllTextAsync(Path.Combine("scripts", "reference", "vitals-policy", "main.ts")),
            ReferenceVitalsCapabilities);

    private static async Task<CompiledScriptPackage> CompileScriptAsync(
        string id,
        string name,
        string source,
        ScriptCapability capabilities,
        ScriptRuntimeProfile runtimeProfile = ScriptRuntimeProfile.InternalTrusted)
    {
        ScriptManifest manifest = new(
            new ScriptModuleId(id),
            name,
            "1.0.0",
            ScriptApiVersion.Current,
            "main.ts",
            capabilities,
            ScriptOwnerKind.System,
            runtimeProfile);
        TypeScriptCompiler compiler = new();
        ScriptCompileResult result = await compiler.CompileAsync(new ScriptCompileRequest(
            manifest,
            ScriptSourceLanguage.TypeScript,
            [new ScriptSourceFile("main.ts", source)]));
        if (!result.Success || result.Package is null)
            throw new InvalidOperationException("TypeScript compilation failed: " + string.Join(" | ", result.Diagnostics.Select(value => $"{value.Code}: {value.Message}")));
        return result.Package;
    }

    private static CapabilityScriptHost CreateJintTestHost(
        ScriptModuleId moduleId,
        IScriptEvents events,
        ScriptCapability capabilities,
        IScriptScheduler? scheduler = null,
        IScriptCommands? commands = null,
        IScriptState? state = null,
        IScriptLog? log = null,
        ScriptCommandOrigin commandOrigin = ScriptCommandOrigin.Script,
        IScriptStorage? storage = null,
        IScriptMapper? mapper = null)
    {
        return new CapabilityScriptHost(
            moduleId,
            new ScriptPermissionSet(capabilities),
            events,
            commands ?? new RecordingScriptCommands(),
            state ?? new StaticScriptState(),
            mapper ?? new NoopScriptMapper(),
            new NoopScriptCodex(),
            storage ?? new MemoryScriptStorage(moduleId),
            scheduler ?? new ScriptScheduler(),
            new NoopScriptUi(),
            log ?? new NoopScriptLog(),
            commandOrigin,
            moduleId.Value);
    }

    private static CompiledScriptPackage TestJavaScriptPackage(
        ScriptModuleId moduleId,
        string name,
        string source,
        ScriptCapability capabilities = ScriptCapability.ReadState)
    {
        ScriptManifest manifest = new(
            moduleId,
            name,
            "1.0.0",
            ScriptApiVersion.Current,
            "main.js",
            capabilities,
            ScriptOwnerKind.UserScript,
            ScriptRuntimeProfile.UserScript);
        return new CompiledScriptPackage(
            manifest,
            [new CompiledScriptModule("main.js", source, OriginalSourcePath: "main.ts")],
            "test-javascript",
            Guid.NewGuid().ToString("N"));
    }

    private sealed class ManualScriptClock : IScriptClock
    {
        private sealed record DelayWaiter(DateTimeOffset Due, TaskCompletionSource Completion, CancellationToken CancellationToken);
        private readonly object _gate = new();
        private readonly List<DelayWaiter> _waiters = [];
        private DateTimeOffset _utcNow;

        public ManualScriptClock(DateTimeOffset initial) => _utcNow = initial;

        public DateTimeOffset UtcNow
        {
            get { lock (_gate) return _utcNow; }
        }

        public int PendingDelayCount
        {
            get { lock (_gate) return _waiters.Count(waiter => !waiter.Completion.Task.IsCompleted); }
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (delay <= TimeSpan.Zero) return Task.CompletedTask;
            TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            DelayWaiter waiter;
            lock (_gate)
            {
                waiter = new DelayWaiter(_utcNow + delay, completion, cancellationToken);
                _waiters.Add(waiter);
            }
            if (cancellationToken.CanBeCanceled)
                cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            return completion.Task;
        }

        public void Advance(TimeSpan delta)
        {
            if (delta < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delta));
            DelayWaiter[] ready;
            lock (_gate)
            {
                _utcNow += delta;
                ready = _waiters.Where(waiter => waiter.Due <= _utcNow).ToArray();
                _waiters.RemoveAll(waiter => waiter.Due <= _utcNow || waiter.Completion.Task.IsCompleted);
            }
            foreach (DelayWaiter waiter in ready)
            {
                if (waiter.CancellationToken.IsCancellationRequested)
                    waiter.Completion.TrySetCanceled(waiter.CancellationToken);
                else
                    waiter.Completion.TrySetResult();
            }
        }
    }

    private sealed class FixedScriptClock(DateTimeOffset now) : IScriptClock
    {
        public DateTimeOffset UtcNow { get; } = now;
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private static Task ConnectionOptionsRejectBadPort()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MudConnectionOptions("example.org", 0).Validate());
        return Task.CompletedTask;
    }

    private static Task DefaultTelnetIdentityIsNeutral()
    {
        MudConnectionOptions options = new("example.org", 4000);
        Assert.Equal("xterm-256color", options.TerminalType);
        Assert.False(options.TerminalType.Contains("jev", StringComparison.OrdinalIgnoreCase),
            "Server-facing terminal identity must not expose Jev integration.");
        return Task.CompletedTask;
    }

    private static Task InitialStateAuthorityIsOff()
    {
        StateSnapshot initial = StateSnapshot.Initial;

        Assert.Equal(JevPreset.Off, initial.JevAuthority.Preset);
        foreach (JevDomain domain in Enum.GetValues<JevDomain>())
        {
            Assert.Equal(JevAuthority.Off, initial.JevAuthority.Domains[domain]);
        }

        return Task.CompletedTask;
    }

    private static Task CopilotPresetConfiguresMatrix()
    {
        JevAuthoritySnapshot preset = JevAuthorityService.CreatePreset(JevPreset.Copilot);
        Assert.Equal(JevAuthority.Suggest, preset.Domains[JevDomain.Combat]);
        Assert.Equal(JevAuthority.Off, preset.Domains[JevDomain.Social]);
        Assert.Equal(JevAuthority.Observe, preset.Domains[JevDomain.Inventory]);
        return Task.CompletedTask;
    }

    private static async Task ManualEditMarksCustom()
    {
        await using EventPipeline events = new();
        JevAuthorityService authority = new(events);
        await authority.ApplyPresetAsync(JevPreset.Copilot);
        await authority.SetDomainAsync(JevDomain.Combat, JevAuthority.Auto);
        Assert.Equal(JevPreset.Custom, authority.Current.Preset);
        Assert.Equal(JevAuthority.Auto, authority.Current.Domains[JevDomain.Combat]);
    }

    private static async Task ProfileApplicationIsAtomic()
    {
        await using EventPipeline events = new();
        ChannelReader<EventEnvelope> observer = events.SubscribeLossless();
        JevAuthorityService authority = new(events);
        JevAuthoritySnapshot profile = JevAuthorityService.CreatePreset(JevPreset.Autonomous);

        await authority.ApplyProfileAsync(profile);

        EventEnvelope envelope = await observer.ReadAsync();
        JevAuthorityProfileChanged changed = Assert.IsType<JevAuthorityProfileChanged>(envelope.Payload);
        Assert.Equal(JevPreset.Autonomous, changed.Snapshot.Preset);
        Assert.Equal(JevAuthority.Auto, changed.Snapshot.Domains[JevDomain.Combat]);
        Assert.Equal(JevAuthority.Observe, changed.Snapshot.Domains[JevDomain.Social]);
        Assert.False(observer.TryRead(out _), "Applying one profile should publish one authority event.");
    }

    private static async Task MasterJevTogglePreservesAuthorityProfile()
    {
        await using EventPipeline events = new();
        JevAuthorityService authority = new(events);
        JevAuthoritySnapshot profile = JevAuthorityService.CreatePreset(JevPreset.Autonomous);
        await authority.ApplyProfileAsync(profile);

        await authority.SetEnabledAsync(false);
        Assert.False(authority.Enabled, "Master Jev control should disable Jev without mutating domain policy.");
        Assert.Equal(profile.Preset, authority.Current.Preset);
        foreach (JevDomain domain in Enum.GetValues<JevDomain>())
        {
            Assert.Equal(profile.Domains[domain], authority.Current.Domains[domain]);
        }

        await authority.SetEnabledAsync(true);
        Assert.True(authority.Enabled, "Master Jev control should restore the existing authority profile.");
        Assert.Equal(profile.Preset, authority.Current.Preset);
    }

    private static Task StateReducerIsDeterministic()
    {
        EventEnvelope envelope = Envelope(1, new CharacterVitalsChanged(50, 100, null, null, null, null));

        StateSnapshot first = StateReducer.Reduce(StateSnapshot.Initial, envelope);
        StateSnapshot second = StateReducer.Reduce(StateSnapshot.Initial, envelope);
        Assert.Equal(first, second);
        Assert.Equal(1L, first.Version);
        Assert.Equal(50, first.Character.HitPoints.Current);
        Assert.Equal(100, first.Character.HitPoints.Maximum);
        return Task.CompletedTask;
    }

    private static Task ObservabilityEventsDoNotAdvanceStateVersion()
    {
        StateSnapshot initial = StateSnapshot.Initial;
        StateSnapshot afterText = StateReducer.Reduce(initial, Envelope(1, new TextReceived("look\n")));
        StateSnapshot afterDisplay = StateReducer.Reduce(afterText, Envelope(2, new GameTextReceived("look\n")));
        StateSnapshot afterGmcp = StateReducer.Reduce(afterDisplay, Envelope(3, new GmcpMessageReceived("Char.Vitals", "{}")));
        StateSnapshot afterJev = StateReducer.Reduce(
            afterGmcp,
            Envelope(4, new JevEvaluationStarted(Guid.NewGuid(), JevDomain.Combat, 0)));

        Assert.True(ReferenceEquals(initial, afterText), "Raw text must not create a state snapshot.");
        Assert.True(ReferenceEquals(initial, afterDisplay), "Display text must not create a state snapshot.");
        Assert.True(ReferenceEquals(initial, afterGmcp), "Raw GMCP must not create a state snapshot before semantic extraction.");
        Assert.True(ReferenceEquals(initial, afterJev), "Observability events must not create a state snapshot.");
        Assert.Equal(0L, afterJev.Version);
        return Task.CompletedTask;
    }

    private static Task IdenticalPromptDoesNotAdvanceStateVersion()
    {
        CharacterPromptObserved prompt = Prompt(exits: new ExitState(true, ["north", "west"]));
        StateSnapshot first = StateReducer.Reduce(StateSnapshot.Initial, Envelope(1, prompt));
        StateSnapshot second = StateReducer.Reduce(first, Envelope(2, prompt));

        Assert.Equal(1L, first.Version);
        Assert.True(ReferenceEquals(first, second), "An identical fast telemetry prompt must not create a new state version.");
        return Task.CompletedTask;
    }

    private static async Task LosslessEventSubscriptionPreservesBurst()
    {
        await using EventPipeline events = new();
        ChannelReader<EventEnvelope> observer = events.SubscribeLossless();
        const int count = 1_024;

        for (int index = 0; index < count; index++)
        {
            await events.PublishAsync(new TextReceived($"line-{index}\n"), "test");
        }

        for (int index = 0; index < count; index++)
        {
            EventEnvelope envelope = await observer.ReadAsync();
            Assert.Equal(index + 1L, envelope.Sequence);
        }
    }

    private static async Task ConcurrentEventPublishingPreservesSequenceOrder()
    {
        await using EventPipeline events = new();
        ChannelReader<EventEnvelope> observer = events.SubscribeLossless();
        const int count = 256;

        Task[] publishers = Enumerable.Range(0, count)
            .Select(index => events.PublishAsync(new TextReceived($"event-{index}"), "test").AsTask())
            .ToArray();
        await Task.WhenAll(publishers);

        for (int expected = 1; expected <= count; expected++)
        {
            EventEnvelope envelope = await observer.ReadAsync();
            Assert.Equal((long)expected, envelope.Sequence);
        }
    }

    private static Task TelnetParserHandlesFragmentedGmcp()
    {
        TelnetParser parser = new(new MudConnectionOptions("example.org", 4000));
        byte[] payload = Encoding.UTF8.GetBytes("Char.Vitals {\"hp\":42}");
        List<byte> frame = [255, 250, 201];
        frame.AddRange(payload);
        frame.AddRange([255, 240]);
        byte[] bytes = frame.ToArray();

        TelnetParseResult first = parser.Process(bytes.AsSpan(0, 7));
        TelnetParseResult second = parser.Process(bytes.AsSpan(7));

        Assert.Equal(0, first.GmcpFrames.Count);
        Assert.Equal(1, second.GmcpFrames.Count);
        Assert.Equal("Char.Vitals", second.GmcpFrames[0].Module);
        Assert.Equal("{\"hp\":42}", second.GmcpFrames[0].Payload);
        return Task.CompletedTask;
    }

    private static Task TelnetNegotiationDeduplicatesOffers()
    {
        TelnetParser parser = new(new MudConnectionOptions("example.org", 4000));
        byte[] offer = [255, 251, 201]; // IAC WILL GMCP

        TelnetParseResult first = parser.Process(offer);
        TelnetParseResult second = parser.Process(offer);

        Assert.Equal(1, first.ProtocolTransitions.Count);
        Assert.Equal("GMCP", first.ProtocolTransitions[0].Protocol);
        Assert.True(first.ProtocolTransitions[0].Enabled, "GMCP should become enabled after WILL/DO negotiation.");
        Assert.Equal(3, first.Responses.Count); // DO + Core.Hello + Core.Supports.Set
        Assert.Equal(0, second.Responses.Count);
        Assert.Equal(0, second.ProtocolTransitions.Count);
        return Task.CompletedTask;
    }

    private static Task TelnetTerminalTypeFollowsMttsSequence()
    {
        MudConnectionOptions options = new("example.org", 4000, TerminalType: "xterm-256color", ClientVersion: "0.15.0-alpha.21");
        TelnetParser parser = new(options);
        parser.Process([255, 253, 24]); // IAC DO TTYPE
        byte[] send = [255, 250, 24, 1, 255, 240];

        TelnetParseResult first = parser.Process(send);
        TelnetParseResult second = parser.Process(send);
        TelnetParseResult third = parser.Process(send);

        Assert.True(FrameContainsAscii(first.Responses.Single(), "NexMUD 0.18.0"), "First TTYPE response should identify NexMUD.");
        Assert.True(FrameContainsAscii(second.Responses.Single(), "xterm-256color"), "Second TTYPE response should identify the terminal type.");
        Assert.True(FrameContainsAscii(third.Responses.Single(), "MTTS 781"), "Third TTYPE response should provide ANSI, UTF-8, 256-color, truecolor, and MNES capabilities.");
        return Task.CompletedTask;
    }

    private static Task TelnetCharsetSelectsUtf8()
    {
        TelnetParser parser = new(new MudConnectionOptions("example.org", 4000));
        parser.Process([255, 253, 42]); // IAC DO CHARSET
        List<byte> request = [255, 250, 42, 1, (byte)';'];
        request.AddRange(Encoding.ASCII.GetBytes("US-ASCII;UTF-8;ISO-8859-1"));
        request.AddRange([255, 240]);

        TelnetParseResult result = parser.Process(request.ToArray());

        Assert.Equal(1, result.Responses.Count);
        Assert.True(FrameContainsAscii(result.Responses[0], "UTF-8"), "CHARSET should accept UTF-8 when offered.");
        Assert.True(result.Responses[0].Contains((byte)2), "CHARSET response should use ACCEPTED.");
        return Task.CompletedTask;
    }

    private static Task TelnetNewEnvironmentFiltersVariables()
    {
        TelnetParser parser = new(new MudConnectionOptions("example.org", 4000));
        parser.Process([255, 253, 39]); // IAC DO NEW-ENVIRON
        List<byte> request = [255, 250, 39, 1, 0]; // SB NEW-ENVIRON SEND VAR
        request.AddRange(Encoding.ASCII.GetBytes("TERM"));
        request.AddRange([255, 240]);

        TelnetParseResult result = parser.Process(request.ToArray());

        Assert.Equal(1, result.Responses.Count);
        Assert.True(FrameContainsAscii(result.Responses[0], "TERM"), "Requested TERM should be returned.");
        Assert.True(FrameContainsAscii(result.Responses[0], "xterm-256color"), "TERM value should reflect connection configuration.");
        Assert.False(FrameContainsAscii(result.Responses[0], "CLIENT_NAME"), "Unrequested NEW-ENVIRON variables should not be returned.");
        return Task.CompletedTask;
    }

    private static Task MsdpParserPreservesNestedStructures()
    {
        TelnetParser parser = new(new MudConnectionOptions("example.org", 4000));
        List<byte> frame = [255, 250, 69, 1];
        frame.AddRange(Encoding.ASCII.GetBytes("ROOM"));
        frame.AddRange([2, 3, 1]);
        frame.AddRange(Encoding.ASCII.GetBytes("VNUM"));
        frame.AddRange([2]);
        frame.AddRange(Encoding.ASCII.GetBytes("123"));
        frame.AddRange([1]);
        frame.AddRange(Encoding.ASCII.GetBytes("EXITS"));
        frame.AddRange([2, 5, 2]);
        frame.AddRange(Encoding.ASCII.GetBytes("north"));
        frame.AddRange([2]);
        frame.AddRange(Encoding.ASCII.GetBytes("south"));
        frame.AddRange([6, 4, 255, 240]);

        TelnetParseResult result = parser.Process(frame.ToArray());

        Assert.Equal(1, result.MsdpFrames.Count);
        MsdpTable room = Assert.IsType<MsdpTable>(result.MsdpFrames[0].Values["ROOM"]);
        MsdpScalar vnum = Assert.IsType<MsdpScalar>(room.Values["VNUM"]);
        Assert.Equal("123", vnum.Value);
        MsdpArray exits = Assert.IsType<MsdpArray>(room.Values["EXITS"]);
        Assert.Equal(2, exits.Values.Count);
        Assert.Equal("north", Assert.IsType<MsdpScalar>(exits.Values[0]).Value);
        Assert.Equal("south", Assert.IsType<MsdpScalar>(exits.Values[1]).Value);
        return Task.CompletedTask;
    }

    private static Task TelnetParserBoundsOversizedSubnegotiation()
    {
        TelnetParser parser = new(new MudConnectionOptions("example.org", 4000));
        byte[] frame = new byte[(64 * 1024) + 6];
        frame[0] = 255;
        frame[1] = 250;
        frame[2] = 201;
        Array.Fill(frame, (byte)'x', 3, 64 * 1024 + 1);
        frame[^2] = 255;
        frame[^1] = 240;

        TelnetParseResult result = parser.Process(frame);

        Assert.Equal(1, result.Issues.Count);
        Assert.Equal("GMCP", result.Issues[0].Protocol);
        Assert.Equal(0, result.GmcpFrames.Count);
        return Task.CompletedTask;
    }

    private static Task TelnetPromptBoundariesAreObservable()
    {
        TelnetParser parser = new(new MudConnectionOptions("example.org", 4000));
        parser.Process([255, 251, 25]); // IAC WILL EOR
        TelnetParseResult result = parser.Process([255, 249, 255, 239]); // GA, EOR

        Assert.SequenceEqual(new[] { "GA", "EOR" }, result.PromptBoundaries);
        return Task.CompletedTask;
    }

    private static Task MsdpDiscoveryReportsDesiredVariables()
    {
        TelnetParser parser = new(new MudConnectionOptions("example.org", 4000));
        parser.Process([255, 251, 69]); // IAC WILL MSDP

        List<byte> frame = [255, 250, 69, 1];
        frame.AddRange(Encoding.ASCII.GetBytes("REPORTABLE_VARIABLES"));
        frame.AddRange([2, 5, 2]);
        frame.AddRange(Encoding.ASCII.GetBytes("HEALTH"));
        frame.AddRange([2]);
        frame.AddRange(Encoding.ASCII.GetBytes("ROOM_NAME"));
        frame.AddRange([2]);
        frame.AddRange(Encoding.ASCII.GetBytes("SERVER_ID"));
        frame.AddRange([6, 255, 240]);

        TelnetParseResult result = parser.Process(frame.ToArray());

        Assert.Equal(2, result.Responses.Count);
        Assert.True(result.Responses.Any(response => FrameContainsAscii(response, "REPORT") && FrameContainsAscii(response, "HEALTH")),
            "MSDP should REPORT supported desired HEALTH data.");
        Assert.True(result.Responses.Any(response => FrameContainsAscii(response, "REPORT") && FrameContainsAscii(response, "ROOM_NAME")),
            "MSDP should REPORT supported desired ROOM_NAME data.");
        Assert.False(result.Responses.Any(response => FrameContainsAscii(response, "SERVER_ID")),
            "MSDP should not REPORT server variables outside NexMUD's desired data set.");
        return Task.CompletedTask;
    }

    private static Task TelnetCharsetRejectsTranslationTable()
    {
        TelnetParser parser = new(new MudConnectionOptions("example.org", 4000));
        parser.Process([255, 253, 42]); // IAC DO CHARSET
        TelnetParseResult result = parser.Process([255, 250, 42, 4, (byte)'x', 255, 240]);

        Assert.Equal(1, result.Responses.Count);
        Assert.True(result.Responses[0].Contains((byte)5), "Unsupported CHARSET translation tables should be rejected explicitly.");
        return Task.CompletedTask;
    }

    private static Task TelnetTerminalTypeSequenceResetsOnDont()
    {
        TelnetParser parser = new(new MudConnectionOptions("example.org", 4000, ClientVersion: "0.15.0-alpha.21"));
        byte[] send = [255, 250, 24, 1, 255, 240];

        parser.Process([255, 253, 24]); // IAC DO TTYPE
        parser.Process(send);
        parser.Process(send);
        parser.Process([255, 254, 24]); // IAC DONT TTYPE
        parser.Process([255, 253, 24]); // IAC DO TTYPE again
        TelnetParseResult restarted = parser.Process(send);

        Assert.True(FrameContainsAscii(restarted.Responses.Single(), "NexMUD 0.18.0"),
            "TTYPE sequence should restart with the client identity after renegotiation.");
        return Task.CompletedTask;
    }

    private static bool FrameContainsAscii(byte[] frame, string value)
    {
        return Encoding.ASCII.GetString(frame).Contains(value, StringComparison.Ordinal);
    }

    private static Task AnsiStripperHandlesFragmentedCsi()
    {
        AnsiStripper stripper = new();
        string first = stripper.Process(Encoding.ASCII.GetBytes("A\u001b[31"));
        string second = stripper.Process(Encoding.ASCII.GetBytes("mB\u001b[0mC"));
        Assert.Equal("A", first);
        Assert.Equal("BC", second);
        return Task.CompletedTask;
    }

    private static Task AnsiParserPreservesFragmentedStyles()
    {
        AnsiTextParser parser = new();
        IReadOnlyList<AnsiTextSegment> first = parser.Process("A\u001b[31");
        IReadOnlyList<AnsiTextSegment> second = parser.Process("mB\u001b[0mC");

        Assert.Equal(1, first.Count);
        Assert.Equal("A", first[0].Text);
        Assert.True(first[0].Style.Foreground is null, "Default text should not have an explicit foreground.");
        Assert.Equal(2, second.Count);
        Assert.Equal("B", second[0].Text);
        Assert.Equal(new AnsiColor(170, 0, 0), second[0].Style.Foreground);
        Assert.Equal("C", second[1].Text);
        Assert.True(second[1].Style.Foreground is null, "SGR reset should restore the default foreground.");
        return Task.CompletedTask;
    }

    private static Task AvendarPromptParserHandlesWrappedTelemetry()
    {
        const string text = "[J|114/132|108/116|115/132|4880|1869|standing|The Northeast Corner of the First Floor|NSW|inside|\nlight]";
        bool parsed = AvendarPromptParser.TryParse(text, out CharacterPromptObserved? prompt);

        Assert.True(parsed && prompt is not null, "Wrapped Avendar telemetry prompt did not parse.");
        Assert.Equal(114, prompt!.HitPoints);
        Assert.Equal(132, prompt.MaxHitPoints);
        Assert.Equal("The Northeast Corner of the First Floor", prompt.RoomName);
        Assert.SequenceEqual(new[] { "north", "south", "west" }, prompt.Exits.Directions);
        Assert.Equal("inside", prompt.Terrain);
        Assert.Equal("light", prompt.Light);
        return Task.CompletedTask;
    }

    private static Task AvendarPromptMarksUnknownExits()
    {
        const string text = "[J|114/132|108/116|116/132|4855|1894|sleeping|The Southeast Corner of the First Floor|???|inside|light]";
        bool parsed = AvendarPromptParser.TryParse(text, out CharacterPromptObserved? prompt);

        Assert.True(parsed && prompt is not null, "Sleeping Avendar prompt did not parse.");
        Assert.False(prompt!.Exits.IsKnown, "The ??? exits marker must mean unavailable/unknown, not an empty known set.");
        Assert.Equal(0, prompt.Exits.Directions.Count);
        return Task.CompletedTask;
    }


    private static Task AvendarPromptRejectsUnknownExitCodes()
    {
        const string text = "[J|114/132|108/116|116/132|4855|1894|standing|Room|NX|inside|light]";
        bool parsed = AvendarPromptParser.TryParse(text, out CharacterPromptObserved? prompt);

        Assert.True(parsed && prompt is not null, "Prompt with an unknown exit code should still parse telemetry.");
        Assert.False(prompt!.Exits.IsKnown, "Unknown exit codes must not be silently discarded into a known exit set.");
        return Task.CompletedTask;
    }

    private static Task AvendarPromptModelsNoneExits()
    {
        const string text = "[J|141/141|119/119|212/212|5342|1407|standing|Amidst A Colorless Fog|none|city|light]";
        bool parsed = AvendarPromptParser.TryParse(text, out CharacterPromptObserved? prompt);

        Assert.True(parsed && prompt is not null, "Prompt with none exits did not parse.");
        Assert.True(prompt!.Exits.IsKnown, "The none exits marker must mean a known empty exit set.");
        Assert.Equal(0, prompt.Exits.Directions.Count);
        return Task.CompletedTask;
    }

    private static Task AvendarStreamRecoversFromUnterminatedTelemetry()
    {
        AvendarPromptStreamProcessor processor = new();
        string malformed = "before\n[J|" + new string('x', 4097) + "after\n";
        IReadOnlyList<AvendarStreamToken> tokens = processor.Process(malformed);
        string displayed = string.Concat(tokens.OfType<AvendarDisplayToken>().Select(token => token.Text));

        Assert.True(displayed.StartsWith("before\n[J|", StringComparison.Ordinal), "Malformed telemetry must fall back to display text.");
        Assert.True(displayed.EndsWith("after\n", StringComparison.Ordinal), "Stream processing must resume after malformed telemetry.");
        return Task.CompletedTask;
    }

    private static Task AvendarStreamExtractsTelemetryWithoutLosingBytes()
    {
        AvendarPromptStreamProcessor processor = new();
        const string firstText = "hello\n[J|132/132|116/116|";
        const string secondText = "132/132|4905|1844|standing|Room|ES|inside|\nlight]after\n";
        IReadOnlyList<AvendarStreamToken> first = processor.Process(firstText);
        IReadOnlyList<AvendarStreamToken> second = processor.Process(secondText);
        AvendarStreamToken[] tokens = first.Concat(second).ToArray();
        AvendarPromptToken prompt = Assert.Single(tokens.OfType<AvendarPromptToken>());
        string reconstructed = string.Concat(tokens.Select(token => token switch
        {
            AvendarDisplayToken display => display.Text,
            AvendarPromptToken telemetry => telemetry.Text,
            _ => string.Empty
        }));

        Assert.Equal(firstText + secondText, reconstructed);
        Assert.True(prompt.Text.StartsWith("[J|", StringComparison.Ordinal), "Prompt token lost its marker.");
        Assert.True(prompt.Text.EndsWith(']'), "Prompt token was not collected through the closing bracket.");
        return Task.CompletedTask;
    }

    private static Task GameObservationsPreserveSourceAndOrder()
    {
        AvendarObservationFactory factory = new();
        DateTimeOffset receivedAt = DateTimeOffset.Parse("2026-09-28T00:00:00Z");
        GameObservation first = factory.Create("\u001b[31mred", receivedAt);
        GameObservation local = factory.CreateEvidence(
            "look",
            receivedAt.AddMilliseconds(1),
            ObservationKind.LocalCommandEcho,
            new ObservationMetadata(IsLocal: true));
        GameObservation second = factory.Create(" text", receivedAt.AddMilliseconds(2));

        Assert.Equal(1L, first.Sequence);
        Assert.Equal(2L, local.Sequence);
        Assert.Equal(3L, second.Sequence);
        Assert.Equal("\u001b[31mred", first.RawText);
        Assert.Equal("red", first.PlainText);
        Assert.Equal("look", local.PlainText);
        Assert.Equal(ObservationKind.LocalCommandEcho, local.Kind);
        Assert.True(local.Metadata.IsLocal, "Local source metadata was not preserved.");
        Assert.Equal(" text", second.PlainText);
        Assert.True(first.AnsiRuns.Count > 0, "ANSI evidence should be preserved as style runs.");
        Assert.True(
            second.AnsiRuns.Count > 0 && second.AnsiRuns[0].Style.Foreground is not null,
            "Synthetic evidence must not mutate fragmented server ANSI state.");

        factory.Reset();
        GameObservation nextSession = factory.Create("next", receivedAt.AddSeconds(1));
        Assert.Equal(1L, nextSession.Sequence);
        Assert.False(first.SessionId == nextSession.SessionId, "Observation sequence must be scoped to a distinct session id.");
        return Task.CompletedTask;
    }

    private static async Task AvendarReplayInjectsEquivalentObservations()
    {
        await using EventPipeline events = new();
        ChannelReader<EventEnvelope> observer = events.SubscribeLossless();
        AvendarGameAdapter adapter = new(events.SubscribeLossless(), events);
        GameObservation observation = new AvendarObservationFactory().Create(
            "Buffer cleared.\n",
            DateTimeOffset.Parse("2026-09-28T00:00:00Z"),
            kind: ObservationKind.ReplayMarker);

        await adapter.ReplayObservationAsync(observation);

        GameObservationReceived? source = null;
        GameCommandQueueCleared? cleared = null;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        while (source is null || cleared is null)
        {
            EventEnvelope envelope = await observer.ReadAsync(timeout.Token);
            source ??= envelope.Payload as GameObservationReceived;
            cleared ??= envelope.Payload as GameCommandQueueCleared;
        }

        Assert.Equal(observation, source!.Observation);
        Assert.Equal(observation.Sequence, cleared!.SourceSequence);
    }

    private static async Task AvendarReplayInfersLegacyStructure()
    {
        await using EventPipeline events = new();
        ChannelReader<EventEnvelope> observer = events.SubscribeLossless();
        AvendarGameAdapter adapter = new(events.SubscribeLossless(), events);
        const string raw =
            "Yisharja's group:\n" +
            "[51 Brd] Olyeasa           -19/1154 hp  929/1041 mana  490/ 490 mv\n" +
            "[51 ETe] A large hen        25/  25 hp  704/ 704 mana  467/ 467 mv\n" +
            "<1043(1028)hp 580(590)m 448(448)mv 8794ep |inside|light|\n" +
            "You feel a slight tingling.\n" +
            "A Black-Stoned Passage\n" +
            "  You stand in a low tunnel walled with rough, black stones.\n" +
            "\n" +
            "[Exits: east south west]\n" +
            "<1030(1044)hp 580(580)m 453(453)mv 8504ep |inside|light|\n";
        GameObservation observation = new AvendarObservationFactory().Create(
            raw,
            DateTimeOffset.Parse("2026-09-28T00:00:00Z"),
            kind: ObservationKind.ReplayMarker);

        await adapter.ReplayObservationAsync(observation);

        GroupSnapshotObserved? group = null;
        RoomObservationObserved? room = null;
        MovementObserved? teleport = null;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        while (group is null || room is null || teleport is null)
        {
            EventEnvelope envelope = await observer.ReadAsync(timeout.Token);
            group ??= envelope.Payload as GroupSnapshotObserved;
            room ??= envelope.Payload as RoomObservationObserved;
            if (envelope.Payload is MovementObserved movement &&
                movement.Movement.Cause == MovementCause.Teleport &&
                movement.Movement.Result == MovementResult.Teleported)
            {
                teleport = movement;
            }
        }

        Assert.Equal(-19, group!.Snapshot.Members[0].Health.Current);
        Assert.Equal("A large hen", group.Snapshot.Members[1].DisplayName);
        Assert.Equal("A Black-Stoned Passage", room!.RoomName);
        Assert.True(room.ObservationId != Guid.Empty, "Room observation identity was not retained.");
        Assert.Equal(observation.Sequence, room.SourceSequence);
        Assert.Equal(RoomVisibilityQuality.Normal, room.VisibilityQuality);
        Assert.Equal(MovementResult.Teleported, teleport!.Movement.Result);
        Assert.Equal(room.RoomId, teleport.Movement.DestinationRoomId);
    }

    private static Task LegacyPromptPreservesObservedResources()
    {
        string sourcePrompt = ReadSemanticFixture("prompt-current-over-max.txt")[0];
        bool parsed = AvendarPromptSnapshotParser.TryParseResourceLine(
            sourcePrompt,
            17,
            out CharacterPromptSnapshot? snapshot);

        Assert.True(parsed && snapshot is not null, "Extended legacy prompt did not parse.");
        Assert.Equal(1043, snapshot!.Health!.Current);
        Assert.Equal(1028, snapshot.Health.Maximum);
        Assert.Equal(580, snapshot.Mana!.Current);
        Assert.Equal(448, snapshot.Movement!.Current);
        Assert.Equal("inside", snapshot.Terrain);
        Assert.Equal("light", snapshot.Light);
        Assert.Equal(17L, snapshot.SourceSequence);

        Assert.True(
            AvendarPromptSnapshotParser.TryParseResourceLine(
                "<-2(100)hp 10(10)m 5(5)mv",
                18,
                out CharacterPromptSnapshot? signed) && signed is not null,
            "Signed prompt resource did not parse.");
        Assert.Equal(-2, signed!.Health!.Current);

        Assert.True(
            AvendarPromptSnapshotParser.TryParseResourceLine(
                "<1(2)hp 3(4)m 5(6)mv 7tnl 8ep future |inside|light|opaque|",
                19,
                out CharacterPromptSnapshot? extended) && extended is not null,
            "Unknown prompt extensions should not invalidate the prompt.");
        Assert.True(
            extended!.EffectiveUnknownFields.Contains("future") &&
            extended.EffectiveUnknownFields.Contains("opaque"),
            "Unknown prompt fields were not preserved.");

        Assert.True(
            AvendarPromptSnapshotParser.TryExtractGameClockPrefix(
                "22:30> gr", 20, out CharacterPromptSnapshot? clock, out string remainder),
            "Game-clock prefix did not parse.");
        Assert.Equal(22, clock!.GameClock!.Hour);
        Assert.Equal(30, clock.GameClock.Minute);
        Assert.Equal("gr", remainder);
        return Task.CompletedTask;
    }

    private static Task GroupParserAcceptsNegativeHp()
    {
        string[] lines = ReadSemanticFixture("group-negative-hp.txt");
        Assert.True(
            AvendarGroupParser.TryParse(lines, 21, out GroupSnapshot? group) && group is not null,
            "Negative-HP group fixture did not parse.");

        GroupMemberSnapshot olyeasa = group!.Members.Single(member => member.DisplayName == "Olyeasa");
        Assert.Equal(-19, olyeasa.Health.Current);
        GroupMemberSnapshot transformed = group.Members.Single(member => member.DisplayName == "A large hen");
        Assert.Equal("A large hen", transformed.DisplayName);
        return Task.CompletedTask;
    }

    private static Task EffectsParserPreservesDurationsAndModifiers()
    {
        string[] lines = ReadSemanticFixture("effects-multiline.txt");
        Assert.True(
            AvendarEffectsParser.TryParse(lines, 31, out ActiveEffectsSnapshot? snapshot) && snapshot is not null,
            "Effects fixture did not parse.");

        ActiveEffect sanctuary = snapshot!.Effects.Single(effect => effect.Name == "sanctuary");
        Assert.Equal(EffectDurationKind.Hours, sanctuary.Duration.Kind);
        Assert.Equal(0m, sanctuary.Duration.Hours!.Value);

        ActiveEffect stoneSkin = snapshot.Effects.Single(effect => effect.Name == "stone skin");
        Assert.Equal(2, stoneSkin.Modifiers.Count);
        Assert.Equal(17m, stoneSkin.Duration.Hours!.Value);

        ActiveEffect detectHidden = snapshot.Effects.Single(effect => effect.Name == "detect hidden");
        Assert.Equal(EffectDurationKind.Permanent, detectHidden.Duration.Kind);
        return Task.CompletedTask;
    }

    private static Task SemanticParserEmitsGameplaySemantics()
    {
        AvendarSemanticParser parser = new();
        GameCommandQueueCleared cleared = Assert.Single(
            parser.ParseLine("Buffer cleared.", 41).OfType<GameCommandQueueCleared>());
        Assert.Equal(41L, cleared.SourceSequence);

        MovementObserved crawl = Assert.Single(
            parser.ParseLine(
                "You crawl south, squeezing between the bier and the ceiling on your hands and knees.",
                42).OfType<MovementObserved>());
        Assert.Equal(MovementCause.Crawl, crawl.Movement.Cause);
        Assert.Equal(MovementResult.SucceededUnknownRoom, crawl.Movement.Result);
        Assert.Equal("south", crawl.Movement.Direction);

        MovementObserved blocked = Assert.Single(
            parser.ParseLine("Alas, you cannot go that way.", 43).OfType<MovementObserved>());
        Assert.Equal(MovementResult.Blocked, blocked.Movement.Result);

        MovementObserved combatRestricted = Assert.Single(
            parser.ParseLine("No way!  You are still fighting!", 44).OfType<MovementObserved>());
        Assert.Equal(MovementResult.CombatRestricted, combatRestricted.Movement.Result);

        MovementObserved follow = Assert.Single(
            parser.ParseLine("You follow Ialoes.", 45).OfType<MovementObserved>());
        Assert.Equal(MovementCause.Follow, follow.Movement.Cause);

        MovementObserved flee = Assert.Single(
            parser.ParseLine("You flee from combat!", 46).OfType<MovementObserved>());
        Assert.Equal(MovementCause.Flee, flee.Movement.Cause);

        MovementObserved teleport = Assert.Single(
            parser.ParseLine("You feel a slight tingling.", 47).OfType<MovementObserved>());
        Assert.Equal(MovementCause.Teleport, teleport.Movement.Cause);
        Assert.Equal(MovementResult.Unknown, teleport.Movement.Result);

        MovementObserved summon = Assert.Single(
            parser.ParseLine("Nyogthua has summoned you!", 48).OfType<MovementObserved>());
        Assert.Equal(MovementCause.Summon, summon.Movement.Cause);

        CombatTargetConditionObserved condition = Assert.Single(
            parser.ParseLine("A yreg looks pretty hurt. [15%-30%]", 49)
                .OfType<CombatTargetConditionObserved>());
        Assert.Equal(15, condition.Range!.MinPercent);
        Assert.Equal(30, condition.Range.MaxPercent);

        CombatTargetConditionObserved scratches = Assert.Single(
            parser.ParseLine("Atthagth has a few scratches.", 50)
                .OfType<CombatTargetConditionObserved>());
        Assert.Equal(90, scratches.Range!.MinPercent);
        Assert.Equal(100, scratches.Range.MaxPercent);
        return Task.CompletedTask;
    }

    private static async Task OpaqueSpecialMovementPreservesUnknownDestination()
    {
        await using EventPipeline events = new();
        ChannelReader<EventEnvelope> observer = events.SubscribeLossless();
        AvendarGameAdapter adapter = new(events.SubscribeLossless(), events);
        GameObservation observation = new AvendarObservationFactory().Create(
            "You follow Ialoes.\nIt is pitch black ...\n",
            DateTimeOffset.Parse("2026-09-28T00:00:00Z"),
            kind: ObservationKind.ReplayMarker);

        await adapter.ReplayObservationAsync(observation);

        MovementObserved? opaque = null;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        while (opaque is null)
        {
            EventEnvelope envelope = await observer.ReadAsync(timeout.Token);
            if (envelope.Payload is MovementObserved movement &&
                movement.Movement.Cause == MovementCause.Follow &&
                movement.Movement.Result == MovementResult.SucceededUnknownRoom &&
                movement.Movement.Detail?.Contains("pitch black", StringComparison.OrdinalIgnoreCase) == true)
            {
                opaque = movement;
            }
        }

        StateSnapshot known = StateReducer.Reduce(
            StateSnapshot.Initial,
            Envelope(1, new RoomChanged("known", "Known Room", ["east"])));
        StateSnapshot reduced = StateReducer.Reduce(known, Envelope(2, opaque!));
        Assert.Equal<string?>(null, reduced.Room.Id);
        Assert.Equal<string?>(null, reduced.Room.Name);
        Assert.Equal(RoomVisibilityQuality.Opaque, reduced.Room.VisibilityQuality);
        Assert.Equal(observation.Sequence, reduced.Room.LastObservedSequence);
    }

    private static Task ScanSemanticsDoNotPolluteRoom()
    {
        AvendarRoomContentsParser roomParser = new();
        roomParser.ObserveLine("Current Room");
        roomParser.ObserveLine("  A test chamber.");
        roomParser.ObserveLine(string.Empty);
        roomParser.ObserveLine("[Exits: east]");
        roomParser.ObserveLine("A local guard stands here.");
        Assert.True(
            roomParser.TryComplete("Current Room", out RoomObservationObserved? room) && room is not null,
            "Current room fixture did not parse.");

        StateSnapshot state = StateReducer.Reduce(StateSnapshot.Initial, Envelope(1, room!));
        int currentOccupants = state.Room.Contents.Count;

        string[] lines = ReadSemanticFixture("scan-distance-tiers.txt");
        Assert.True(
            AvendarScanParser.TryParse(lines, 52, out ScanObservation? scan) && scan is not null,
            "Scan fixture did not parse.");
        StateSnapshot next = StateReducer.Reduce(state, Envelope(2, new ScanUpdated(scan!)));

        Assert.Equal(currentOccupants, next.Room.Contents.Count);
        Assert.Equal(3, next.LastScan!.Tiers.Count);
        Assert.Equal(2, next.LastScan.Tiers[1].Entities.Count);
        return Task.CompletedTask;
    }

    private static async Task CommandJournalDoesNotInferPromptAcknowledgement()
    {
        Channel<EventEnvelope> channel = Channel.CreateUnbounded<EventEnvelope>();
        OutboundCommandJournal journal = new(channel.Reader, 8);
        Guid id = Guid.NewGuid();
        await channel.Writer.WriteAsync(Envelope(1, new ActionDispatching(id, "clear")));
        await channel.Writer.WriteAsync(Envelope(2, new ActionExecuted(id, "clear")));
        await channel.Writer.WriteAsync(Envelope(3, new CharacterPromptSnapshotObserved(
            new CharacterPromptSnapshot(
                new ResourceValue(10, 10),
                new ResourceValue(10, 10),
                new ResourceValue(10, 10),
                SourceSequence: 2))));
        channel.Writer.TryComplete();
        await journal.RunAsync(CancellationToken.None);

        OutboundCommandRecord record = Assert.Single(journal.Snapshot());
        Assert.Equal("clear", record.Text);
        Assert.Equal(OutboundCommandState.TransportWritten, record.State);

        Channel<EventEnvelope> queueClearedChannel = Channel.CreateUnbounded<EventEnvelope>();
        OutboundCommandJournal queueClearedJournal = new(queueClearedChannel.Reader, 8);
        await queueClearedChannel.Writer.WriteAsync(Envelope(4, new ActionDispatching(id, "clear")));
        await queueClearedChannel.Writer.WriteAsync(Envelope(5, new ActionExecuted(id, "clear")));
        await queueClearedChannel.Writer.WriteAsync(Envelope(6, new GameCommandQueueCleared(3)));
        queueClearedChannel.Writer.TryComplete();
        await queueClearedJournal.RunAsync(CancellationToken.None);

        OutboundCommandRecord retained = Assert.Single(queueClearedJournal.Snapshot());
        Assert.Equal("clear", retained.Text);
        Assert.Equal(OutboundCommandState.ServerQueueCleared, retained.State);
    }

    private static async Task CommandJournalPreservesRepeatedCommandBursts()
    {
        Channel<EventEnvelope> channel = Channel.CreateUnbounded<EventEnvelope>();
        OutboundCommandJournal journal = new(channel.Reader, 8);
        string[] commands = ReadSemanticFixture("command-burst.txt");
        for (int index = 0; index < commands.Length; index++)
        {
            await channel.Writer.WriteAsync(Envelope(
                index + 1,
                new ActionDispatching(Guid.NewGuid(), commands[index])));
        }
        channel.Writer.TryComplete();
        await journal.RunAsync(CancellationToken.None);

        IReadOnlyList<OutboundCommandRecord> records = journal.Snapshot();
        Assert.Equal(commands.Length, records.Count);
        Assert.True(records.All(record => record.Text == "cc"), "Repeated command text was altered.");
        Assert.Equal(records.Count, records.Select(record => record.CommandId).Distinct().Count());
    }

    private static Task SemanticCorpusFixturesCoverExpertLogs()
    {
        string fixtureRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures", "semantics");
        string[] expectedSources =
        [
            "Mines 3_31_15.txt",
            "Xiganath.txt",
            "Void Drake Fight.txt",
            "Xiganath 06.27.2015.txt"
        ];
        string[] corpusFiles = Directory.GetFiles(fixtureRoot, "*.txt");
        foreach (string source in expectedSources)
        {
            Assert.True(
                corpusFiles.Any(path => File.ReadAllText(path).Contains(source, StringComparison.Ordinal)),
                $"Semantic regression corpus is missing source coverage for {source}.");
        }

        string[] roomLines = ReadSemanticFixture("room-duplicate-corpses.txt");
        AvendarRoomContentsParser roomParser = new();
        foreach (string line in roomLines) roomParser.ObserveLine(line);
        Assert.True(
            roomParser.TryComplete("Underneath a Suction Tube", out RoomObservationObserved? room) && room is not null,
            "Duplicate corpse fixture did not parse.");
        RoomContentObservation yregCorpses = room!.Contents.Single(content =>
            content.Description.Contains("corpse of a yreg", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, yregCorpses.Count);
        Assert.True(yregCorpses.OccurrenceId != Guid.Empty, "Room occurrence identity was not retained.");

        Assert.True(
            AvendarItemIdentificationParser.TryParse(
                ReadSemanticFixture("item-id-structured.txt"),
                out ItemIdentified? identified) && identified is not null,
            "Structured item fixture did not parse.");
        Assert.Equal("retained", identified!.Item.RawFields["mystery field"]);
        Assert.Equal("-45", identified.Item.RawFields["effect armor class"]);
        Assert.Equal("tiny", identified.Item.Size);
        Assert.True(identified.Item.Flags.Contains("hum"), "Item flags were not tokenized.");
        Assert.True(identified.Item.Flags.Contains("nodestroy"), "Item flags lost a token.");

        AvendarSemanticParser semantic = new();
        string specialMovement = ReadSemanticFixture("movement-special.txt")[0];
        Assert.True(
            semantic.ParseLine(specialMovement, 71).OfType<MovementObserved>()
                .Any(movement => movement.Movement.Cause == MovementCause.Crawl),
            "Crawl movement fixture did not produce movement semantics.");
        Assert.True(
            ReadSemanticFixture("movement-blocked.txt")
                .SelectMany(line => semantic.ParseLine(line, 72))
                .OfType<MovementObserved>()
                .Any(movement => movement.Movement.Result == MovementResult.CombatRestricted),
            "Blocked movement fixture lost combat restriction semantics.");
        Assert.True(
            ReadSemanticFixture("movement-follow.txt")
                .SelectMany(line => semantic.ParseLine(line, 73))
                .OfType<MovementObserved>()
                .Any(movement => movement.Movement.Cause == MovementCause.Follow),
            "Follow movement fixture did not produce movement semantics.");
        Assert.True(
            ReadSemanticFixture("movement-flee.txt")
                .SelectMany(line => semantic.ParseLine(line, 74))
                .OfType<MovementObserved>()
                .Any(movement => movement.Movement.Cause == MovementCause.Flee),
            "Flee movement fixture did not produce movement semantics.");
        Assert.True(
            ReadSemanticFixture("movement-teleport.txt")
                .SelectMany(line => semantic.ParseLine(line, 75))
                .OfType<MovementObserved>()
                .Any(movement => movement.Movement.Cause == MovementCause.Teleport),
            "Teleport movement fixture did not produce movement semantics.");
        Assert.True(
            ReadSemanticFixture("movement-summon.txt")
                .SelectMany(line => semantic.ParseLine(line, 76))
                .OfType<MovementObserved>()
                .Any(movement => movement.Movement.Cause == MovementCause.Summon),
            "Summon movement fixture did not produce movement semantics.");
        Assert.True(
            ReadSemanticFixture("command-clear-buffer.txt")
                .SelectMany(line => semantic.ParseLine(line, 77))
                .OfType<GameCommandQueueCleared>()
                .Any(),
            "Server queue clear fixture did not produce semantics.");
        Assert.True(
            ReadSemanticFixture("target-condition-ranges.txt")
                .SelectMany(line => semantic.ParseLine(line, 78))
                .OfType<CombatTargetConditionObserved>()
                .All(condition => condition.Range is not null),
            "Target condition fixture lost coarse ranges.");
        Assert.True(
            ReadSemanticFixture("high-volume-combat.txt")
                .SelectMany(line => semantic.ParseLine(line, 79))
                .OfType<CombatTargetConditionObserved>()
                .Any(),
            "High-volume combat fixture produced no target condition semantics.");
        return Task.CompletedTask;
    }

    private static string[] ReadSemanticFixture(string fileName) =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "semantics", fileName))
            .Where(line => !line.StartsWith("# Source:", StringComparison.Ordinal))
            .ToArray();

    private static async Task AvendarAdapterPreservesRawServerText()
    {
        await using EventPipeline events = new();
        ChannelReader<EventEnvelope> adapterInput = events.SubscribeLossless();
        ChannelReader<EventEnvelope> observer = events.SubscribeLossless();
        AvendarGameAdapter adapter = new(adapterInput, events);
        using CancellationTokenSource cancellation = new();
        Task worker = adapter.RunAsync(cancellation.Token);
        const string raw = "\u001b[36mRoom\u001b[0m\n[J|132/132|116/116|132/132|4905|1844|standing|Room|ES|inside|light]";

        await events.PublishAsync(new TextReceived(raw), "test.transport");

        GameTextReceived? display = null;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        while (display is null)
        {
            EventEnvelope envelope = await observer.ReadAsync(timeout.Token);
            display = envelope.Payload as GameTextReceived;
        }

        cancellation.Cancel();
        await worker;
        Assert.Equal(raw, display.Text);
    }


    private static async Task AvendarAdapterFollowsExplicitLoginInputState()
    {
        await using EventPipeline events = new();
        ChannelReader<EventEnvelope> adapterInput = events.SubscribeLossless();
        ChannelReader<EventEnvelope> observer = events.SubscribeLossless();
        AvendarGameAdapter adapter = new(adapterInput, events);
        using CancellationTokenSource cancellation = new();
        Task worker = adapter.RunAsync(cancellation.Token);

        await events.PublishAsync(
            new TextReceived("Under what name shall your deeds be recorded?\n"),
            "test.transport");
        await events.PublishAsync(new TextReceived("Password:"), "test.transport");
        await events.PublishAsync(
            new ProtocolStateChanged("ECHO", false, "server restored normal echo state"),
            "test.transport");

        List<SessionInputMode> observedModes = [];
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        while (!observedModes.Contains(SessionInputMode.Normal))
        {
            EventEnvelope envelope = await observer.ReadAsync(timeout.Token);
            if (envelope.Payload is SessionInputModeChanged changed)
            {
                observedModes.Add(changed.Mode);
            }
        }

        cancellation.Cancel();
        await worker;

        Assert.SequenceEqual(
            [SessionInputMode.LoginName, SessionInputMode.LoginPassword, SessionInputMode.Normal],
            observedModes);
    }

    private static async Task AvendarMovementResponsesCorrelateCommands()
    {
        await using EventPipeline events = new();
        ChannelReader<EventEnvelope> adapterInput = events.SubscribeLossless();
        ChannelReader<EventEnvelope> observer = events.SubscribeLossless();
        AvendarGameAdapter adapter = new(adapterInput, events);
        using CancellationTokenSource cancellation = new();
        Task worker = adapter.RunAsync(cancellation.Token);

        Guid deniedAction = Guid.NewGuid();
        Guid successfulAction = Guid.NewGuid();
        await events.PublishAsync(new ActionDispatching(deniedAction, "east"), "test.action");
        await events.PublishAsync(new ActionDispatching(successfulAction, "west"), "test.action");

        const string denied = "Shiguln says, 'You aren't branded with the Sigil of the School of Heroes.'\n" +
                              "[J|132/132|116/116|132/132|4905|1844|standing|The Entrance to the Hall of Victors|EW|inside|light]";
        await events.PublishAsync(new TextReceived(denied), "test.transport");

        const string successful = "The Bar in the Adventurer's Lounge\n\n" +
                                  "  A broad lounge opens around a well-polished bar.\n\n" +
                                  "[Exits: north east west]\n\n" +
                                  "[J|132/132|116/116|132/132|4905|1844|standing|The Bar in the Adventurer's Lounge|NEW|inside|light]";
        await events.PublishAsync(new TextReceived(successful), "test.transport");

        List<NavigationResponseCompleted> responses = [];
        RoomObservationObserved? room = null;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        while (responses.Count < 2 || room is null)
        {
            EventEnvelope envelope = await observer.ReadAsync(timeout.Token);
            if (envelope.Payload is NavigationResponseCompleted response) responses.Add(response);
            if (envelope.Payload is RoomObservationObserved observed) room = observed;
        }

        cancellation.Cancel();
        await worker;

        Assert.Equal(2, responses.Count);
        Assert.Equal(deniedAction, responses[0].ActionId);
        Assert.Equal("east", responses[0].Direction);
        Assert.False(responses[0].RoomObserved, "Denied movement must close without inventing a room transition.");
        Assert.Equal(successfulAction, responses[1].ActionId);
        Assert.Equal("west", responses[1].Direction);
        Assert.True(responses[1].RoomObserved, "Successful movement must correlate with the room block completed by its response.");
        Assert.Equal("The Bar in the Adventurer's Lounge", room!.RoomName);
    }

    private static async Task AvendarWhereResponseEmitsExplicitArea()
    {
        await using EventPipeline events = new();
        ChannelReader<EventEnvelope> adapterInput = events.SubscribeLossless();
        ChannelReader<EventEnvelope> observer = events.SubscribeLossless();
        AvendarGameAdapter adapter = new(adapterInput, events);
        using CancellationTokenSource cancellation = new();
        Task worker = adapter.RunAsync(cancellation.Token);

        await events.PublishAsync(new ActionDispatching(Guid.NewGuid(), "where"), "test.action");
        const string response = "Notables in Avendar (currently in The School of Heroes):\n" +
                                " Randolph                   The Foyer of the School of Heroes [present]\n" +
                                "[J|132/132|116/116|132/132|4905|1844|standing|The Foyer of the School of Heroes|NESWUD|inside|light]";
        await events.PublishAsync(new TextReceived(response), "test.transport");

        AreaObserved? observed = null;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        while (observed is null)
        {
            EventEnvelope envelope = await observer.ReadAsync(timeout.Token);
            observed = envelope.Payload as AreaObserved;
        }

        cancellation.Cancel();
        await worker;
        Assert.Equal("The School of Heroes", observed.Area);
    }

    private static Task PromptDisplayFilterSlurpsFragmentedTelemetry()
    {
        AvendarPromptDisplayFilter filter = new();
        string first = filter.Process("before [J|10/10|9/9");
        string second = filter.Process("|8/8|0|0|standing|A Room|N|inside|light] after\n");

        Assert.Equal("before ", first);
        Assert.Equal(" after\n", second);
        return Task.CompletedTask;
    }

    private static Task TranscriptDisplayNormalizesCrLf()
    {
        TranscriptLineEndingNormalizer normalizer = new();
        Assert.Equal("one\ntwo\n", normalizer.Process("one\r\ntwo\r\n"));
        Assert.Equal("three\nfour\n", normalizer.Process("three\n\rfour\n\r"));

        TranscriptLineEndingNormalizer fragmented = new();
        Assert.Equal("one", fragmented.Process("one\r"));
        Assert.Equal("\ntwo\n", fragmented.Process("\ntwo\r\n"));

        TranscriptLineEndingNormalizer fragmentedLfCr = new();
        Assert.Equal("one\n", fragmentedLfCr.Process("one\n"));
        Assert.Equal("two\n", fragmentedLfCr.Process("\rtwo\n\r"));

        TranscriptLineEndingNormalizer explicitBlankLine = new();
        Assert.Equal("one\n\ntwo\n", explicitBlankLine.Process("one\n\r\n\rtwo\n\r"));

        TranscriptLineEndingNormalizer nvtCarriageReturn = new();
        Assert.Equal("onetwo", nvtCarriageReturn.Process("one\r\0two"));
        return Task.CompletedTask;
    }

    private static Task TranscriptTypographyUsesFixedTerminalDensity()
    {
        Assert.Equal(14d, NexTranscriptTypography.DefaultFontSize);
        Assert.Equal(16d, NexTranscriptTypography.LineHeight(14));
        Assert.Equal(320d, NexTranscriptTypography.HeightForRows(20, 14));
        Assert.True(
            NexTranscriptTypography.LineHeight(14) / 14 is >= 1.10 and <= 1.20,
            "Transcript line-height ratio must remain terminal-dense.");
        return Task.CompletedTask;
    }

    private static Task TranscriptHighlighterPreservesText()
    {
        AnsiTextStyle ansi = AnsiTextStyle.Default with { Italic = true };
        TranscriptHighlighter highlighter = new([
            new TranscriptHighlightRule("danger", "#FF0000", Bold: true),
            new TranscriptHighlightRule(@"\b\d+\b", "#00FF00", HighlightMatchMode.Regex, Underline: true)
        ]);
        const string text = "DANGER at 42 health";
        IReadOnlyList<TranscriptPresentationSegment> pieces = highlighter.Apply(new AnsiTextSegment(text, ansi));

        Assert.Equal(text, string.Concat(pieces.Select(piece => piece.Text)));
        Assert.True(pieces.All(piece => piece.AnsiStyle == ansi), "Highlighting must preserve ANSI style state.");
        Assert.True(pieces.Any(piece => piece.Highlight?.Pattern == "danger"), "Literal highlight did not apply.");
        Assert.True(pieces.Any(piece => piece.Highlight?.MatchMode == HighlightMatchMode.Regex), "Regex highlight did not apply.");
        return Task.CompletedTask;
    }

    private static Task TranscriptLoggerWritesVerbatimServerText()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nexmud-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using TranscriptLogWriter logger = new();
            string path = logger.Start(directory, "avendar.net:9999");
            const string raw = "\u001b[31mred\u001b[0m\r\n[J|1/2|3/4|5/6|7|8|standing|Room|N|inside|light]";
            logger.Write(raw);
            logger.Stop();
            Assert.Equal(raw, File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        return Task.CompletedTask;
    }


    private static Task TranscriptLoggerSupportsPlainAndJsonFormats()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nexmud-tests", Guid.NewGuid().ToString("N"));
        try
        {
            const string raw = "\u001b[31mred\u001b[0m\r\n";
            using TranscriptLogWriter logger = new();

            string plainPath = logger.Start(directory, "avendar.net:9999", TranscriptLogFormat.PlainText);
            logger.Write(raw);
            logger.Stop();
            Assert.Equal("red\r\n", File.ReadAllText(plainPath));

            string jsonPath = logger.Start(directory, "avendar.net:9999", TranscriptLogFormat.JsonLines);
            logger.Write(raw);
            logger.Stop();
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(jsonPath));
            JsonElement root = document.RootElement;
            Assert.Equal("server", root.GetProperty("source").GetString());
            Assert.Equal(raw, root.GetProperty("text").GetString());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        return Task.CompletedTask;
    }

    private static Task CommandAliasesExpandArguments()
    {
        CommandAlias[] aliases =
        [
            new("kk", "kill $*"),
            new("tell2", "tell $1 $2")
        ];

        Assert.Equal("kill oversized rat", CommandAliasExpander.Expand("kk oversized rat", aliases));
        Assert.Equal("tell bob hello", CommandAliasExpander.Expand("tell2 bob hello", aliases));
        Assert.Equal("look", CommandAliasExpander.Expand("look", aliases));
        return Task.CompletedTask;
    }

    private static Task AliasExpansionsExecuteCommandBatches()
    {
        CommandAlias[] aliases =
        [
            new("cake", "open pack;get cake pack;eat cake;close pack"),
            new("sayx", "say $*")
        ];

        IReadOnlyList<string> commands = CommandInputExpander.Expand("cake", ";", aliases);
        Assert.Equal(4, commands.Count);
        Assert.Equal("open pack", commands[0]);
        Assert.Equal("get cake pack", commands[1]);
        Assert.Equal("eat cake", commands[2]);
        Assert.Equal("close pack", commands[3]);

        IReadOnlyList<string> mixed = CommandInputExpander.Expand("cake;look", ";", aliases);
        Assert.Equal(5, mixed.Count);
        Assert.Equal("look", mixed[4]);

        IReadOnlyList<string> escaped = CommandInputExpander.Expand(@"sayx hello\;world", ";", aliases);
        Assert.Equal("say hello;world", Assert.Single(escaped));
        return Task.CompletedTask;
    }

    private static Task CommandSeparatorSplitsInput()
    {
        IReadOnlyList<string> commands = CommandBatchSplitter.Split(@"n;n;n;n;w;open chest", ";");
        Assert.Equal(6, commands.Count);
        Assert.Equal("n", commands[0]);
        Assert.Equal("open chest", commands[5]);
        IReadOnlyList<string> escaped = CommandBatchSplitter.Split(@"say hello\;world;n", ";");
        Assert.Equal(2, escaped.Count);
        Assert.Equal("say hello;world", escaped[0]);
        Assert.Equal("n", escaped[1]);
        Assert.Equal(1, CommandBatchSplitter.Split("look", string.Empty).Count);
        return Task.CompletedTask;
    }

    private static async Task InteractionInputPipelinePreservesHistoryAndOrder()
    {
        ClientSettings settings = ClientSettings.Default with { CommandSeparator = ";" };
        CommandHistoryService history = new(maximumEntries: 10);
        CompletionService completion = new();
        InputPipeline pipeline = new(() => settings, history, completion);
        List<string> dispatched = [];

        InputSubmissionResult result = await pipeline.SubmitAsync(
            new InputRequest(InputSourceKind.Keyboard, @"n;say one\;two;look", DateTimeOffset.UtcNow, Guid.NewGuid()),
            SessionInputMode.Normal,
            (command, _) =>
            {
                dispatched.Add(command);
                return Task.FromResult(new LocalCommandResult(false));
            });

        Assert.SequenceEqual(new[] { "n", "say one;two", "look" }, result.Commands);
        Assert.SequenceEqual(result.Commands, dispatched);
        Assert.Equal(@"n;say one\;two;look", Assert.Single(history.Snapshot()));
    }

    private static async Task InteractionGeneratedCommandsDoNotPolluteHistory()
    {
        ClientSettings settings = ClientSettings.Default;
        CommandHistoryService history = new(maximumEntries: 10);
        CompletionService completion = new();
        InputPipeline pipeline = new(() => settings, history, completion);

        await pipeline.SubmitAsync(
            new InputRequest(InputSourceKind.Mapper, "north", DateTimeOffset.UtcNow, Guid.NewGuid()),
            SessionInputMode.Normal,
            (_, _) => Task.FromResult(new LocalCommandResult(false)));

        Assert.Equal(0, history.Snapshot().Count);
    }

    private static Task InteractionHistoryRestoresInProgressBuffer()
    {
        CommandHistoryService history = new(maximumEntries: 3, deduplicateConsecutive: true);
        history.Add("look");
        history.Add("north");
        history.Add("north");
        history.Add("score");

        Assert.Equal("score", history.Previous("say unfinished"));
        Assert.Equal("north", history.Previous("ignored"));
        Assert.Equal("score", history.Next());
        Assert.Equal("say unfinished", history.Next());
        Assert.SequenceEqual(new[] { "look", "north", "score" }, history.Snapshot());
        return Task.CompletedTask;
    }

    private static Task InteractionCompletionPreservesMudNames()
    {
        CompletionService completion = new(tokenLimit: 100);
        completion.IndexText("Laoris hands you the axe Xchilwnsdhg'rta");
        completion.IndexText("Xcali waits nearby");

        IReadOnlyList<string> candidates = completion.Find(new CompletionContext("xc"));
        Assert.True(candidates.Contains("Xchilwnsdhg'rta", StringComparer.OrdinalIgnoreCase), "Apostrophe-containing MUD name was split during completion indexing.");
        Assert.Equal("Xcali", candidates[0]);

        string? first = completion.Cycle("xc", candidates, reverse: false);
        string? second = completion.Cycle("xc", candidates, reverse: false);
        string? reversed = completion.Cycle("xc", candidates, reverse: true);
        Assert.Equal(candidates[0], first);
        Assert.Equal(candidates[1], second);
        Assert.Equal(candidates[0], reversed);
        return Task.CompletedTask;
    }

    private static Task InteractionKeybindingsResolveContextAndConflicts()
    {
        KeybindingService service = new();
        CommandKeyBinding global = new("Ctrl+L", "look", Context: KeybindingContext.Global, Action: KeybindingActionKind.SendCommand, Priority: 1);
        CommandKeyBinding input = new("Ctrl+L", "clear", Context: KeybindingContext.Input, Action: KeybindingActionKind.ClearInput, Priority: 5);
        service.Configure([global, input]);

        KeybindingResolution exact = service.Resolve("Ctrl+L", KeybindingContext.Input);
        Assert.Equal(input, exact.Binding);
        KeybindingResolution fallback = service.Resolve("Ctrl+L", KeybindingContext.Mapper);
        Assert.Equal(global, fallback.Binding);

        service.Configure([
            global,
            global with { Command = "score", Name = "duplicate" }
        ]);
        KeybindingResolution conflict = service.Resolve("Ctrl+L", KeybindingContext.Global);
        Assert.True(conflict.HasConflict, "Equal-priority keybinding collision should be explicit.");
        Assert.Equal(1, service.Conflicts().Count);
        return Task.CompletedTask;
    }

    private static Task OutputTransformationPreservesSourceData()
    {
        OutputTransformationRule[] rules =
        [
            new("capture", "Tell capture", @"^(\w+) tells you '(.*)'$", OutputRuleMatchType.Regex, Actions: [new OutputRuleAction(OutputRuleActionKind.Capture)]),
            new("sub", "Guard substitution", "A cityguard says 'Halt!'", Actions: [new OutputRuleAction(OutputRuleActionKind.Substitute, "[Guard] Halt!")]),
            new("highlight", "Guard highlight", "[Guard]", Priority: 10, Actions: [new OutputRuleAction(OutputRuleActionKind.Highlight, Foreground: "#FFD166", Bold: true)]),
            new("gag", "Hunger gag", "You are hungry.", Actions: [new OutputRuleAction(OutputRuleActionKind.Gag)])
        ];
        ClientSettings settings = ClientSettings.Default with { OutputRules = rules };
        ClientInteractionRuntime runtime = new(() => settings);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        WorldBufferEntry substituted = runtime.ProcessServerOutput("A cityguard says 'Halt!'\n", now)!;
        OutputFrame source = runtime.SourceFrames.Find(substituted.SourceFrameId)!;
        Assert.Equal("A cityguard says 'Halt!'\n", source.RawText);
        Assert.True(substituted.RenderedText.Contains("[Guard] Halt!", StringComparison.Ordinal), "Substitution did not affect rendered projection.");
        Assert.True(substituted.StyledRuns.Any(run => run.Override?.Foreground == "#FFD166"), "Highlight did not compose over substituted text.");

        WorldBufferEntry capture = runtime.ProcessServerOutput("Laoris tells you 'hello'", now.AddSeconds(1))!;
        Assert.True(capture.Captures.Any(item => item.Values.TryGetValue("1", out string? speaker) && speaker == "Laoris"), "Regex capture groups were not preserved as structured data.");

        WorldBufferEntry gagged = runtime.ProcessServerOutput("You are hungry.\n", now.AddSeconds(2))!;
        Assert.True(gagged.IsGagged, "Gag rule did not suppress display projection.");
        Assert.True(runtime.SourceFrames.Find(gagged.SourceFrameId) is not null, "Gagging destroyed the source output frame.");
        Assert.False(runtime.World.Snapshot().Any(entry => entry.EntryId == gagged.EntryId), "Gagged entry leaked into visible scrollback.");
        Assert.True(runtime.World.Snapshot(includeGagged: true).Any(entry => entry.EntryId == gagged.EntryId), "Gagged entry was not retained as logical source-linked scrollback metadata.");
        return Task.CompletedTask;
    }

    private static Task OutputPreservesTerminalLineStructure()
    {
        ClientInteractionRuntime runtime = new(() => ClientSettings.Default);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        runtime.ProcessServerOutput("line one", now);
        runtime.ProcessServerOutput(" continued\n\rline two\n\r", now.AddMilliseconds(1));
        runtime.ProcessServerOutput("foo\n\r\n\rbar\n\r", now.AddMilliseconds(2));

        string rendered = string.Concat(runtime.World.Snapshot().Select(entry => entry.RenderedText));
        Assert.Equal("line one continued\nline two\nfoo\n\nbar\n", rendered);
        return Task.CompletedTask;
    }

    private static Task OutputHighlightSpansAnsiRuns()
    {
        OutputTransformationService service = new();
        service.Configure(
        [
            new OutputTransformationRule(
                "cross-run",
                "Cross run",
                "guard",
                Actions: [new OutputRuleAction(OutputRuleActionKind.Highlight, Foreground: "#FFD166")])
        ],
        legacyHighlights: null);

        OutputFrame frame = new(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            "guard",
            "guard",
            [
                new AnsiTextSegment("gu", AnsiTextStyle.Default with { Bold = true }),
                new AnsiTextSegment("ard", AnsiTextStyle.Default)
            ],
            false,
            OutputFrameSource.Server,
            1);

        WorldBufferEntry entry = service.Transform(frame).Entry;
        Assert.Equal("guard", string.Concat(entry.StyledRuns.Select(run => run.Text)));
        Assert.True(entry.StyledRuns.Where(run => run.Text.Length > 0).All(run => run.Override?.Foreground == "#FFD166"),
            "Highlight did not span source ANSI run boundaries.");
        return Task.CompletedTask;
    }

    private static Task WorldBufferSubscriberFailureIsIsolated()
    {
        WorldBuffer buffer = new();
        int observed = 0;
        buffer.Appended += _ => throw new InvalidOperationException("test subscriber fault");
        buffer.Appended += _ => observed++;
        string text = "still visible";
        buffer.Append(new WorldBufferEntry(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            text,
            [new WorldStyledRun(text, AnsiTextStyle.Default)],
            false,
            false,
            false,
            OutputFrameSource.Server,
            [],
            []));

        Assert.Equal(1, observed);
        Assert.Equal(1, buffer.Snapshot().Count);
        return Task.CompletedTask;
    }

    private static Task InteractionReplayIsDeterministic()
    {
        OutputTransformationRule[] rules =
        [
            new("sub", "Guard substitution", "A cityguard says 'Halt!'", Actions: [new OutputRuleAction(OutputRuleActionKind.Substitute, "[Guard] Halt!")]),
            new("gag", "Hunger gag", "You are hungry.", Actions: [new OutputRuleAction(OutputRuleActionKind.Gag)]),
            new("capture", "Tell capture", @"^(\w+) tells you '(.*)'$", OutputRuleMatchType.Regex, Actions: [new OutputRuleAction(OutputRuleActionKind.Capture)])
        ];
        ClientSettings settings = ClientSettings.Default with { OutputRules = rules };
        ClientInteractionRuntime first = new(() => settings);
        ClientInteractionRuntime second = new(() => settings);
        DateTimeOffset timestamp = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

        WorldBufferEntry firstEntry = first.ProcessServerOutput("A cityguard says 'Halt!'\n", timestamp, replay: true)!;
        WorldBufferEntry secondEntry = second.ProcessServerOutput("A cityguard says 'Halt!'\n", timestamp, replay: true)!;
        Assert.Equal(firstEntry.RenderedText, secondEntry.RenderedText);
        Assert.Equal(firstEntry.IsGagged, secondEntry.IsGagged);
        Assert.Equal(OutputFrameSource.Replay, firstEntry.Source);
        Assert.Equal(OutputFrameSource.Replay, secondEntry.Source);

        WorldBufferEntry firstCapture = first.ProcessServerOutput("Laoris tells you 'hello'", timestamp.AddSeconds(1), replay: true)!;
        WorldBufferEntry secondCapture = second.ProcessServerOutput("Laoris tells you 'hello'", timestamp.AddSeconds(1), replay: true)!;
        Assert.Equal(
            firstCapture.Captures.Single().Values["1"],
            secondCapture.Captures.Single().Values["1"]);
        return Task.CompletedTask;
    }

    private static Task InteractionLocalEchoIsSeparate()
    {
        ClientInteractionRuntime runtime = new(() => ClientSettings.Default);
        DateTimeOffset timestamp = DateTimeOffset.UtcNow;
        WorldBufferEntry entry = runtime.AppendLocalEcho("> look\n", timestamp);
        OutputFrame source = runtime.SourceFrames.Find(entry.SourceFrameId)!;

        Assert.True(entry.IsLocalEcho, "Local echo entry was not marked as local echo.");
        Assert.Equal(OutputFrameSource.LocalEcho, entry.Source);
        Assert.Equal(OutputFrameSource.LocalEcho, source.Source);
        Assert.Equal("> look\n", source.RawText);
        return Task.CompletedTask;
    }

    private static Task OutputRuleFailuresAreIsolated()
    {
        ClientSettings settings = ClientSettings.Default with
        {
            OutputRules =
            [
                new("invalid", "Invalid regex", "(", OutputRuleMatchType.Regex, Actions: [new OutputRuleAction(OutputRuleActionKind.Gag)]),
                new("valid", "Valid highlight", "guard", Actions: [new OutputRuleAction(OutputRuleActionKind.Highlight, Foreground: "#FFD166")])
            ]
        };
        ClientInteractionRuntime runtime = new(() => settings);
        List<OutputRuleDiagnostic> diagnostics = [];
        runtime.RuleDiagnostic += diagnostics.Add;
        runtime.Configure(settings);

        WorldBufferEntry entry = runtime.ProcessServerOutput("A guard arrives.\n", DateTimeOffset.UtcNow)!;
        Assert.True(diagnostics.Any(item => item.RuleId == "invalid"), "Invalid output rule was not diagnosed.");
        Assert.True(entry.StyledRuns.Any(run => run.Override?.Foreground == "#FFD166"), "A failed output rule prevented unrelated rules from running.");
        return Task.CompletedTask;
    }

    private static Task CommandOriginValuesRemainCompatible()
    {
        Assert.Equal(0, (int)CommandOrigin.User);
        Assert.Equal(1, (int)CommandOrigin.Automation);
        Assert.Equal(2, (int)CommandOrigin.Jev);
        Assert.Equal(3, (int)CommandOrigin.Mapper);
        Assert.Equal(4, (int)CommandOrigin.Script);
        Assert.Equal(5, (int)CommandOrigin.System);
        Assert.Equal(6, (int)CommandOrigin.Alias);
        Assert.Equal(7, (int)CommandOrigin.Keybinding);
        Assert.Equal(0, (int)ScriptCommandOrigin.User);
        Assert.Equal(1, (int)ScriptCommandOrigin.Automation);
        Assert.Equal(2, (int)ScriptCommandOrigin.Jev);
        Assert.Equal(3, (int)ScriptCommandOrigin.Mapper);
        Assert.Equal(4, (int)ScriptCommandOrigin.Script);
        Assert.Equal(5, (int)ScriptCommandOrigin.System);
        Assert.Equal(6, (int)ScriptCommandOrigin.Alias);
        Assert.Equal(7, (int)ScriptCommandOrigin.Keybinding);
        return Task.CompletedTask;
    }

    private static Task WorldScrollbackIsBoundedAndSearchable()
    {
        WorldBuffer buffer = new(maximumEntries: 250);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        for (int index = 0; index < 260; index++)
        {
            string text = index == 259 ? "Laoris arrives from the north." : $"line {index}";
            buffer.Append(new WorldBufferEntry(
                Guid.NewGuid(), now.AddSeconds(index), Guid.NewGuid(), text,
                [new WorldStyledRun(text, AnsiTextStyle.Default)], false, false, false,
                OutputFrameSource.Server, [], []));
        }

        Assert.Equal(250, buffer.Snapshot().Count);
        Assert.Equal(1, buffer.Search("laoris").Count);
        Assert.Equal(1, buffer.Search(@"Laoris\s+arrives", new WorldBufferSearchOptions(Regex: true)).Count);
        return Task.CompletedTask;
    }

    private static Task UnindentedFixtureIsNotOccupant()
    {
        AvendarRoomContentsParser parser = new();
        parser.ObserveLine("A Test Room");
        parser.ObserveLine("  A simple room.");
        parser.ObserveLine(string.Empty);
        parser.ObserveLine("[Exits: north]");
        parser.ObserveLine("A gurgling fountain bubbles here.");

        Assert.True(parser.TryComplete("A Test Room", out RoomObservationObserved? observed) && observed is not null,
            "Fixture room did not parse.");
        RoomContentObservation fixture = Assert.Single(observed!.Contents);
        Assert.Equal(RoomEntityKind.Fixture, fixture.Kind);
        Assert.True(fixture.Traits.HasFlag(RoomEntityTraits.Drinkable), "Fountain should be drinkable.");
        return Task.CompletedTask;
    }

    private static Task AvendarRoomContentsParserCapturesRoomBlock()
    {
        AvendarRoomContentsParser parser = new();
        parser.ObserveLine("The Southeast Corner of the First Floor");
        parser.ObserveLine("  A rough stone chamber lies here.");
        parser.ObserveLine(string.Empty);
        parser.ObserveLine("A sign stands near the stairwell.");
        parser.ObserveLine(string.Empty);
        parser.ObserveLine("[Exits: north west]");
        parser.ObserveLine("     The sliced-off leg of an oversized rat is lying here.");
        parser.ObserveLine("An oversized rat stretches out lazily on top of a broken stone.");
        parser.ObserveLine("An oversized rat squeaks as it scampers about.");

        bool completed = parser.TryComplete(
            "The Southeast Corner of the First Floor",
            out RoomObservationObserved? observed);

        Assert.True(completed && observed is not null, "Room observation did not complete.");
        Assert.Equal(2, observed!.Contents.Count(item => item.Kind == RoomEntityKind.Occupant));
        Assert.Equal(1, observed.Contents.Count(item => item.Kind == RoomEntityKind.Object));
        Assert.Equal(1, observed.Contents.Count(item => item.Kind == RoomEntityKind.Fixture));
        Assert.True(observed.Contents.Single(item => item.Kind == RoomEntityKind.Fixture).Traits.HasFlag(RoomEntityTraits.Readable),
            "Room sign should be modeled as readable.");
        Assert.Equal(ObservationCompleteness.Complete, observed.ContentsCompleteness);
        return Task.CompletedTask;
    }

    private static Task AvendarRoomParserStopsAtSpeech()
    {
        AvendarRoomContentsParser parser = new();
        parser.ObserveLine("The Bar in the Adventurer's Lounge");
        parser.ObserveLine("  A bar occupies the south wall.");
        parser.ObserveLine(string.Empty);
        parser.ObserveLine("[Exits: north east west]");
        parser.ObserveLine("An aelin rogue puts a modest tip on the bar.");
        parser.ObserveLine("A tall, outgoing alatharya woman polishes the bar.");
        parser.ObserveLine("Laoti tells you, 'Hi Randolph.'", claimedByAnotherParser: true);
        parser.ObserveLine("There is 1 change waiting to be read.");

        bool completed = parser.TryComplete(
            "The Bar in the Adventurer's Lounge",
            out RoomObservationObserved? observed);

        Assert.True(completed && observed is not null, "Room observation did not complete.");
        Assert.Equal(2, observed!.Contents.Count);
        Assert.Equal(0, observed.Contents.Count(item => item.Description.Contains("tells you", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(0, observed.Contents.Count(item => item.Description.Contains("change waiting", StringComparison.OrdinalIgnoreCase)));
        return Task.CompletedTask;
    }


    private static Task RoomParserModelsCorpse()
    {
        AvendarRoomContentsParser parser = new();
        parser.ObserveLine("A Test Room");
        parser.ObserveLine("  A plain test room.");
        parser.ObserveLine(string.Empty);
        parser.ObserveLine("[Exits: north]");
        parser.ObserveLine("The corpse of a human pupil is lying here.");

        Assert.True(parser.TryComplete("A Test Room", out RoomObservationObserved? observed) && observed is not null,
            "Corpse room did not parse.");
        RoomContentObservation corpse = Assert.Single(observed!.Contents);
        Assert.Equal(RoomEntityKind.Corpse, corpse.Kind);
        Assert.True(corpse.Traits.HasFlag(RoomEntityTraits.Container), "Corpse should be a container.");
        Assert.True(corpse.Traits.HasFlag(RoomEntityTraits.Lootable), "Corpse should be lootable.");
        Assert.True(corpse.Traits.HasFlag(RoomEntityTraits.Sacrificable), "Corpse should be sacrificable.");
        return Task.CompletedTask;
    }

    private static Task RoomParserStopsAtWrappedSpeechStart()
    {
        AvendarRoomContentsParser parser = new();
        parser.ObserveLine("A Test Room");
        parser.ObserveLine("  A plain test room.");
        parser.ObserveLine(string.Empty);
        parser.ObserveLine("[Exits: north]");
        parser.ObserveLine("A city guard stands here.");
        parser.ObserveLine("Jimacha tells you, 'There is a dragon in the Hall of Trials and I believe");
        parser.ObserveLine("Kajach takes care of it.'");

        Assert.True(parser.TryComplete("A Test Room", out RoomObservationObserved? observed) && observed is not null,
            "Wrapped speech room did not parse.");
        Assert.Equal(1, observed!.Contents.Count);
        Assert.Equal(RoomEntityKind.Occupant, observed.Contents[0].Kind);
        return Task.CompletedTask;
    }

    private static Task DuplicateRoomNamesProduceDistinctIds()
    {
        AvendarRoomContentsParser firstParser = new();
        firstParser.ObserveLine("The Adventurer's Lounge");
        firstParser.ObserveLine("  This corner contains a fountain and hallway west.");
        firstParser.ObserveLine(string.Empty);
        firstParser.ObserveLine("[Exits: east south west]");
        Assert.True(firstParser.TryComplete("The Adventurer's Lounge", out RoomObservationObserved? first) && first is not null,
            "First room did not parse.");

        AvendarRoomContentsParser secondParser = new();
        secondParser.ObserveLine("The Adventurer's Lounge");
        secondParser.ObserveLine("  This corner contains chairs and stairs leading up.");
        secondParser.ObserveLine(string.Empty);
        secondParser.ObserveLine("[Exits: north east south up]");
        Assert.True(secondParser.TryComplete("The Adventurer's Lounge", out RoomObservationObserved? second) && second is not null,
            "Second room did not parse.");

        Assert.False(first!.RoomId == second!.RoomId, "Duplicate display names must not collapse distinct descriptions into one room id.");
        return Task.CompletedTask;
    }

    private static Task ParenthesizedExitsPreserveUnknownQualifier()
    {
        AvendarRoomContentsParser parser = new();
        parser.ObserveLine("The Hall of Fates");
        parser.ObserveLine("  Doors lead in several directions.");
        parser.ObserveLine(string.Empty);
        parser.ObserveLine("[Exits: (north) (east) west down]");
        Assert.True(parser.TryComplete("The Hall of Fates", out RoomObservationObserved? observed) && observed is not null,
            "Hall of Fates did not parse.");

        RoomExitObservation north = observed!.ExitDetails.Single(exit => exit.Direction == "north");
        RoomExitObservation west = observed.ExitDetails.Single(exit => exit.Direction == "west");
        Assert.Equal(ExitDoorState.Unknown, north.DoorState);
        Assert.Equal(ExitTraversability.Unknown, north.Traversability);
        Assert.Equal("(north)", north.RawToken);
        Assert.True(north.Qualifiers.Contains("parenthesized"), "Parenthesized exit qualifier was lost.");
        Assert.Equal(ExitTraversability.Traversable, west.Traversability);
        return Task.CompletedTask;
    }


    private static Task ClosedDoorMovementFailureClearsPendingTraversal()
    {
        AvendarSemanticParser parser = new();
        NavigationFailed failed = Assert.Single(
            parser.ParseLine("The golden door is closed.").OfType<NavigationFailed>());
        Assert.True(failed.Direction is null, "Closed-door output does not identify a direction by itself.");

        StateSnapshot current = StateReducer.Reduce(
            StateSnapshot.Initial,
            Envelope(1, new NavigationAttempted("east")));
        StateSnapshot next = StateReducer.Reduce(current, Envelope(2, failed));

        Assert.Equal(0, next.World.PendingDirections.Count);
        RoomExitObservation east = Assert.Single(next.Room.Exits.Details ?? Array.Empty<RoomExitObservation>());
        Assert.Equal("east", east.Direction);
        Assert.Equal(ExitDoorState.Closed, east.DoorState);
        Assert.Equal(ExitTraversability.Blocked, east.Traversability);

        NavigationFailed lockedFailure = Assert.Single(
            parser.ParseLine("The golden door is locked.").OfType<NavigationFailed>());
        StateSnapshot lockedState = StateReducer.Reduce(
            StateReducer.Reduce(StateSnapshot.Initial, Envelope(3, new NavigationAttempted("north"))),
            Envelope(4, lockedFailure));
        RoomExitObservation north = Assert.Single(lockedState.Room.Exits.Details ?? Array.Empty<RoomExitObservation>());
        Assert.Equal(ExitDoorState.Locked, north.DoorState);
        Assert.Equal(ExitTraversability.Blocked, north.Traversability);
        return Task.CompletedTask;
    }

    private static async Task DeniedMovementResponseDoesNotPoisonNextTraversal()
    {
        Guid deniedAction = Guid.NewGuid();
        Guid successfulAction = Guid.NewGuid();

        StateSnapshot state = StateSnapshot.Initial;
        state = StateReducer.Reduce(state, Envelope(1, new NavigationAttempted("east", deniedAction)));
        state = StateReducer.Reduce(state, Envelope(2, new NavigationResponseCompleted(deniedAction, "east", false)));
        Assert.Equal(0, state.World.PendingDirections.Count);
        state = StateReducer.Reduce(state, Envelope(3, new NavigationAttempted("west", successfulAction)));
        Assert.Equal("west", state.World.PendingDirection);

        string databasePath = Path.Combine(Path.GetTempPath(), $"nexmud-map-denied-move-test-{Guid.NewGuid():N}.db");
        await using EventPipeline events = new();
        StateReducer reducer = new(events.StateEvents);
        WorldKnowledgeStore knowledge = new(events.SubscribeLossless(), reducer, databasePath);
        using CancellationTokenSource cts = new();
        Task reducerTask = reducer.RunAsync(cts.Token);
        Task knowledgeTask = knowledge.RunAsync(cts.Token);

        RoomObservationObserved entrance = new(
            "hall:entrance", "The Entrance to the Hall of Victors", "hall-entrance", "An ancient archway.",
            [new RoomExitObservation("east", true, ExitDoorState.Unknown, ExitTraversability.Traversable),
             new RoomExitObservation("west", true, ExitDoorState.Unknown, ExitTraversability.Traversable)],
            Array.Empty<RoomContentObservation>(), ObservationCompleteness.Complete);
        RoomObservationObserved bar = new(
            "lounge:bar", "The Bar in the Adventurer's Lounge", "lounge-bar", "A broad lounge.",
            [new RoomExitObservation("east", true, ExitDoorState.Unknown, ExitTraversability.Traversable)],
            Array.Empty<RoomContentObservation>(), ObservationCompleteness.Complete);

        await events.PublishAsync(entrance, "test");
        await events.PublishAsync(new NavigationAttempted("east", deniedAction), "test");
        await events.PublishAsync(new NavigationResponseCompleted(deniedAction, "east", false), "test");
        await events.PublishAsync(new NavigationAttempted("west", successfulAction), "test");
        await events.PublishAsync(bar, "test");
        await events.PublishAsync(new NavigationResponseCompleted(successfulAction, "west", true), "test");
        await WaitUntilAsync(() => knowledge.Summary.Rooms >= 2);

        RoomKnowledge? rememberedEntrance = await knowledge.GetRoomAsync("hall:entrance");
        KnowledgeExit learned = Assert.Single(rememberedEntrance!.Exits.Where(exit => exit.ToRoomId == "lounge:bar"));
        Assert.Equal("west", learned.Direction);
        Assert.False(rememberedEntrance.Exits.Any(exit => exit.Direction == "east" && exit.ToRoomId == "lounge:bar"),
            "A denied east attempt must never be attached to the later west room transition.");

        cts.Cancel();
        await IgnoreCancellation(reducerTask);
        await IgnoreCancellation(knowledgeTask);
        foreach (string path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static async Task MapperPersistsClosedDoorFailure()
    {
        string databasePath = Path.Combine(Path.GetTempPath(), $"nexmud-map-door-test-{Guid.NewGuid():N}.db");
        await using EventPipeline events = new();
        StateReducer reducer = new(events.StateEvents);
        WorldKnowledgeStore knowledge = new(events.SubscribeLossless(), reducer, databasePath);
        using CancellationTokenSource cts = new();
        Task reducerTask = reducer.RunAsync(cts.Token);
        Task knowledgeTask = knowledge.RunAsync(cts.Token);

        RoomObservationObserved west = new(
            "door:west", "West Room", "door-west", "A west room.",
            [new RoomExitObservation("east", true, ExitDoorState.Unknown, ExitTraversability.Traversable)],
            Array.Empty<RoomContentObservation>(), ObservationCompleteness.Complete);
        RoomObservationObserved east = new(
            "door:east", "East Room", "door-east", "An east room.",
            [new RoomExitObservation("west", true, ExitDoorState.Unknown, ExitTraversability.Traversable)],
            Array.Empty<RoomContentObservation>(), ObservationCompleteness.Complete);

        await events.PublishAsync(west, "test");
        await events.PublishAsync(new NavigationAttempted("east"), "test");
        await events.PublishAsync(east, "test");
        await events.PublishAsync(new NavigationAttempted("west"), "test");
        await events.PublishAsync(west, "test");
        await events.PublishAsync(new NavigationAttempted("east"), "test");
        await events.PublishAsync(new NavigationFailed(null, "The golden door is closed."), "test");

        RoomKnowledge? learned = null;
        for (int attempt = 0; attempt < 50; attempt++)
        {
            learned = await knowledge.GetRoomAsync("door:west");
            if (learned?.Exits.Any(exit => exit.Direction == "east" && string.Equals(exit.DoorState, ExitDoorState.Closed.ToString(), StringComparison.OrdinalIgnoreCase)) == true) break;
            await Task.Delay(20);
        }

        KnowledgeExit closed = Assert.Single(learned!.Exits.Where(exit => exit.Direction == "east"));
        Assert.Equal(ExitDoorState.Closed.ToString(), closed.DoorState);
        Assert.Equal(ExitTraversability.Blocked.ToString(), closed.Traversability);

        MapperReadRepository mapper = new(databasePath);
        RoutePlanningOptions blockedByDefault = new(
            20,
            AvoidBlockedExits: true,
            AllowUnknownTraversability: true,
            AvoidClosedDoors: false,
            PreferKnownTraversableExits: true,
            AvoidAreas: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            AvoidTerrains: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            AvoidMobNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Assert.True(await mapper.FindRouteAsync("door:west", "door:east", blockedByDefault) is null,
            "A closed/blocked exit should be excluded unless route planning knows auto-open is available.");

        RoutePlanningOptions allowClosed = blockedByDefault with { CanOpenClosedDoors = true };
        KnowledgeRoute? route = await mapper.FindRouteAsync("door:west", "door:east", allowClosed);
        KnowledgeRouteStep step = Assert.Single(route!.Steps);
        Assert.Equal(ExitDoorState.Closed, step.DoorState);
        Assert.Equal(ExitTraversability.Blocked, step.Traversability);

        MapperGraphSnapshot? doorGraph = await mapper.LoadNeighborhoodAsync("door:west", 4, 20);
        MapperGraphEdge doorEdge = Assert.Single(doorGraph!.Edges.Where(edge => edge.FromRoomId == "door:west" && edge.Direction == "east"));
        Assert.Equal(ExitDoorState.Closed, doorEdge.DoorState);
        Assert.Equal(ExitTraversability.Blocked.ToString(), doorEdge.Traversability);

        cts.Cancel();
        await IgnoreCancellation(reducerTask);
        await IgnoreCancellation(knowledgeTask);
        foreach (string path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static async Task SuccessfulTraversalClearsStaleBlockedExitState()
    {
        string databasePath = Path.Combine(Path.GetTempPath(), $"nexmud-map-heal-block-test-{Guid.NewGuid():N}.db");
        await using EventPipeline events = new();
        StateReducer reducer = new(events.StateEvents);
        WorldKnowledgeStore knowledge = new(events.SubscribeLossless(), reducer, databasePath);
        using CancellationTokenSource cts = new();
        Task reducerTask = reducer.RunAsync(cts.Token);
        Task knowledgeTask = knowledge.RunAsync(cts.Token);

        RoomObservationObserved lounge = new(
            "heal:lounge", "The Adventurer's Lounge", "heal-lounge", "A lounge.",
            [new RoomExitObservation("east", true, ExitDoorState.Unknown, ExitTraversability.Traversable)],
            Array.Empty<RoomContentObservation>(), ObservationCompleteness.Complete);
        RoomObservationObserved bar = new(
            "heal:bar", "The Bar in the Adventurer's Lounge", "heal-bar", "A bar.",
            [new RoomExitObservation("west", true, ExitDoorState.Unknown, ExitTraversability.Traversable)],
            Array.Empty<RoomContentObservation>(), ObservationCompleteness.Complete);

        await events.PublishAsync(lounge, "test");
        await events.PublishAsync(new NavigationAttempted("east"), "test");
        await events.PublishAsync(new NavigationFailed(null, "Alas, you cannot go that way."), "test");

        for (int attempt = 0; attempt < 50; attempt++)
        {
            RoomKnowledge? blocked = await knowledge.GetRoomAsync("heal:lounge");
            if (blocked?.Exits.Any(exit => exit.Direction == "east" && string.Equals(exit.Traversability, ExitTraversability.Blocked.ToString(), StringComparison.OrdinalIgnoreCase)) == true) break;
            await Task.Delay(20);
        }

        await events.PublishAsync(new NavigationAttempted("east"), "test");
        await events.PublishAsync(bar, "test");

        KnowledgeExit? healed = null;
        for (int attempt = 0; attempt < 50; attempt++)
        {
            RoomKnowledge? remembered = await knowledge.GetRoomAsync("heal:lounge");
            healed = remembered?.Exits.FirstOrDefault(exit => exit.Direction == "east");
            if (healed is { ToRoomId: "heal:bar" } &&
                string.Equals(healed.Traversability, ExitTraversability.Traversable.ToString(), StringComparison.OrdinalIgnoreCase)) break;
            await Task.Delay(20);
        }

        Assert.True(healed is not null, "Expected learned east exit after successful traversal.");
        Assert.Equal(ExitTraversability.Traversable.ToString(), healed!.Traversability);
        Assert.Equal(ExitDoorState.Unknown.ToString(), healed.DoorState);
        Assert.True(string.IsNullOrWhiteSpace(healed.BlockReason), "Successful traversal must clear stale block reason.");
        Assert.Equal("heal:bar", healed.ToRoomId);

        cts.Cancel();
        await IgnoreCancellation(reducerTask);
        await IgnoreCancellation(knowledgeTask);
        foreach (string path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static Task GoldenRoomTranscriptFixtureParses()
    {
        IReadOnlyList<RoomObservationObserved> rooms = ParseGoldenRoomFixture();

        Assert.Equal(3, rooms.Count);
        RoomObservationObserved lounge = rooms[0];
        Assert.Equal(1, lounge.Contents.Count(item => item.Kind == RoomEntityKind.Occupant));
        Assert.Equal(2, lounge.Contents.Count(item => item.Kind == RoomEntityKind.Fixture));
        Assert.True(lounge.Contents.Any(item => item.TargetKeywords?.Contains("board") == true),
            "Bulletin board should be an interactable fixture.");
        Assert.True(lounge.Contents.Any(item => item.TargetKeywords?.Contains("fountain") == true && item.Traits.HasFlag(RoomEntityTraits.Drinkable)),
            "Fountain should be drinkable.");

        RoomObservationObserved office = rooms[1];
        Assert.Equal(2, office.Contents.Count(item => item.Kind == RoomEntityKind.Occupant));
        Assert.Equal(1, office.Contents.Count(item => item.Kind == RoomEntityKind.Fixture));
        Assert.True(office.Contents.Single(item => item.Kind == RoomEntityKind.Fixture).Traits.HasFlag(RoomEntityTraits.Container),
            "Bins should be a container fixture.");

        RoomObservationObserved fates = rooms[2];
        Assert.Equal(1, fates.Contents.Count(item => item.Kind == RoomEntityKind.Occupant));
        Assert.True(fates.Contents.Single().Decorators?.Contains("(!)") == true,
            "Occupant decorator should be preserved.");
        Assert.Equal(ExitTraversability.Blocked, fates.ExitDetails.Single(exit => exit.Direction == "north").Traversability);
        return Task.CompletedTask;
    }

    private static Task GoldenRoomStatePreservesCanonicalContext()
    {
        RoomObservationObserved lounge = ParseGoldenRoomFixture()[0];
        StateSnapshot state = StateSnapshot.Initial;
        state = StateReducer.Reduce(state, Envelope(1, new CharacterPromptObserved(
            195,
            195,
            148,
            148,
            280,
            280,
            12858,
            1727,
            "standing",
            "The Adventurer's Lounge",
            new ExitState(true, ["east", "south", "west"]),
            "inside",
            "light")));
        state = StateReducer.Reduce(state, Envelope(2, lounge));

        Assert.Equal("The Adventurer's Lounge", state.Room.Name);
        Assert.Equal("inside", state.Room.Terrain);
        Assert.Equal("light", state.Room.Light);
        Assert.SequenceEqual(["east", "south", "west"], state.Room.Exits.Directions);
        Assert.Equal("A kankoran student", Assert.Single(state.Room.Occupants).CanonicalName);
        string[] fixtures = state.Room.Interactables
            .Select(item => item.CanonicalName ?? item.Description)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.SequenceEqual(["A prominent bulletin board", "A small fountain"], fixtures);
        Assert.SequenceEqual(
            [
                "A small fountain gurgles here.",
                "A kankoran student looks consideringly to the western stairs."
            ],
            state.Room.RecentObservations);
        return Task.CompletedTask;
    }

    private static IReadOnlyList<RoomObservationObserved> ParseGoldenRoomFixture()
    {
        string fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "avendar-room-observations.txt");
        string[] lines = File.ReadAllLines(fixturePath);
        AvendarCommunicationParser communication = new();
        List<RoomObservationObserved> rooms = [];
        AvendarRoomContentsParser parser = new();
        string? roomName = null;

        foreach (string line in lines)
        {
            if (line.StartsWith("@@ ", StringComparison.Ordinal) && line != "@@ END")
            {
                roomName = line[3..];
                parser.Reset();
                continue;
            }

            if (line == "@@ END")
            {
                Assert.True(roomName is not null, "Fixture room name was missing.");
                Assert.True(parser.TryComplete(roomName!, out RoomObservationObserved? observed) && observed is not null,
                    $"Fixture room '{roomName}' did not parse.");
                rooms.Add(observed!);
                roomName = null;
                continue;
            }

            bool claimed = communication.TryParse(line, out _);
            parser.ObserveLine(line, claimed);
        }

        return rooms;
    }

    private static Task AmbientOccupantTextIsNotCombatCondition()
    {
        AvendarSemanticParser parser = new();
        IReadOnlyList<IMudEvent> events = parser.ParseLine(
            "An oversized rat looks about on its hind legs, black eyes shining in the torchlight.");

        Assert.Equal(0, events.OfType<CombatTargetConditionObserved>().Count());
        Assert.Equal(0, parser.ParseLine("Randolph is in excellent condition.").OfType<CombatTargetConditionObserved>().Count());

        CombatTargetConditionObserved condition = Assert.Single(
            parser.ParseLine("An oversized rat looks pretty hurt.").OfType<CombatTargetConditionObserved>());
        Assert.Equal("An oversized rat", condition.TargetName);
        Assert.Equal("pretty hurt", condition.Condition);
        return Task.CompletedTask;
    }

    private static Task RoomContentsSnapshotReplacesCurrentRoom()
    {
        StateSnapshot current = StateReducer.Reduce(
            StateSnapshot.Initial,
            Envelope(1, Prompt(new ExitState(true, ["north", "west"]))));

        RoomContentsObserved contents = new(
            current.Room.Name!,
            [
                new RoomContentObservation("An oversized rat stretches out lazily on top of a broken stone."),
                new RoomContentObservation("An oversized rat squeaks as it scampers about.")
            ],
            [new RoomContentObservation("The sliced-off leg of an oversized rat is lying here.")],
            []);

        StateSnapshot first = StateReducer.Reduce(current, Envelope(2, contents));
        StateSnapshot second = StateReducer.Reduce(first, Envelope(3, contents));

        Assert.Equal(2, first.Room.Occupants.Count);
        Assert.Equal(1, first.Room.Objects.Count);
        Assert.Equal(0, first.Room.UnknownContents.Count);
        Assert.True(ReferenceEquals(first, second), "Identical room contents must not advance state version.");
        return Task.CompletedTask;
    }

    private static Task RoomChangeClearsStaleContents()
    {
        CharacterPromptObserved initialPrompt = Prompt(new ExitState(true, ["north", "west"])) with
        {
            RoomName = "The Southeast Corner of the First Floor"
        };
        StateSnapshot current = StateReducer.Reduce(
            StateSnapshot.Initial,
            Envelope(1, initialPrompt));
        current = StateReducer.Reduce(
            current,
            Envelope(2, new RoomContentsObserved(
                current.Room.Name!,
                [new RoomContentObservation("An oversized rat waits here.")],
                [],
                [])));

        CharacterPromptObserved nextPrompt = Prompt(new ExitState(true, ["south", "west"])) with
        {
            RoomName = "The Northeast Corner of the First Floor"
        };
        Assert.False(
            string.Equals(current.Room.Name, nextPrompt.RoomName, StringComparison.Ordinal),
            "Room-change test requires distinct source and destination room names.");

        StateSnapshot next = StateReducer.Reduce(current, Envelope(3, nextPrompt));

        Assert.Equal(0, next.Room.Occupants.Count);
        Assert.Equal(0, next.Room.Objects.Count);
        Assert.Equal("The Northeast Corner of the First Floor", next.Room.Name);
        return Task.CompletedTask;
    }

    private static Task EnemyDeathRemovesOneMatchingOccupant()
    {
        StateSnapshot current = StateReducer.Reduce(
            StateSnapshot.Initial,
            Envelope(1, Prompt(new ExitState(true, ["north", "west"]))));
        current = StateReducer.Reduce(
            current,
            Envelope(2, new RoomContentsObserved(
                current.Room.Name!,
                [
                    new RoomContentObservation("An oversized rat stretches out lazily."),
                    new RoomContentObservation("An oversized rat squeaks loudly."),
                    new RoomContentObservation("A city guard stands watch.")
                ],
                [],
                [])));

        StateSnapshot next = StateReducer.Reduce(current, Envelope(3, new EnemyKilled("An oversized rat")));

        Assert.Equal(2, next.Room.Occupants.Count);
        Assert.Equal(1, next.Room.Occupants.Count(item =>
            item.Description.StartsWith("An oversized rat ", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(1, next.Room.Occupants.Count(item =>
            item.Description.StartsWith("A city guard ", StringComparison.OrdinalIgnoreCase)));
        return Task.CompletedTask;
    }

    private static Task OccupantDepartureRemovesOneMatchingOccupant()
    {
        AvendarSemanticParser parser = new();
        RoomOccupantDeparted departed = Assert.Single(
            parser.ParseLine("An oversized rat leaves south.").OfType<RoomOccupantDeparted>());

        StateSnapshot current = StateReducer.Reduce(
            StateSnapshot.Initial,
            Envelope(1, Prompt(new ExitState(true, ["north", "west"]))));
        current = StateReducer.Reduce(
            current,
            Envelope(2, new RoomContentsObserved(
                current.Room.Name!,
                [new RoomContentObservation("An oversized rat looks about on its hind legs.")],
                [],
                [])));
        StateSnapshot next = StateReducer.Reduce(current, Envelope(3, departed));

        Assert.Equal("An oversized rat", departed.TargetName);
        Assert.Equal("south", departed.Direction);
        Assert.Equal(0, next.Room.Occupants.Count);
        return Task.CompletedTask;
    }

    private static Task AvendarDamageParserNormalizesTiers()
    {
        AvendarSemanticParser parser = new();
        CombatDamageObserved outgoing = Assert.Single(parser.ParseLine("Your feeble stab sunders an oversized rat.").OfType<CombatDamageObserved>());
        CombatDamageObserved finalBlow = Assert.Single(parser.ParseLine("Your weak stab slaughters an oversized rat.").OfType<CombatDamageObserved>());
        CombatDamageObserved incoming = Assert.Single(parser.ParseLine("An oversized rat's pathetic bite hurts you.").OfType<CombatDamageObserved>());

        Assert.Equal(CombatActor.Player, outgoing.Damage.Source);
        Assert.Equal("feeble", outgoing.Damage.AbsoluteTerm);
        Assert.Equal(1, outgoing.Damage.AbsoluteTier);
        Assert.Equal("sunders", outgoing.Damage.RelativeTerm);
        Assert.Equal(4, outgoing.Damage.RelativeTier);
        Assert.Equal("slaughters", finalBlow.Damage.RelativeTerm);
        Assert.Equal(5, finalBlow.Damage.RelativeTier);

        Assert.Equal(CombatActor.Opponent, incoming.Damage.Source);
        Assert.Equal("pathetic", incoming.Damage.AbsoluteTerm);
        Assert.Equal(1, incoming.Damage.AbsoluteTier);
        Assert.Equal("hurts", incoming.Damage.RelativeTerm);
        Assert.Equal(1, incoming.Damage.RelativeTier);
        return Task.CompletedTask;
    }

    private static Task AvendarScoreParserExtractsState()
    {
        string[] lines = ScoreFixture.Split('\n');
        bool parsed = AvendarScoreParser.TryParse(lines, out CharacterScoreObserved? score);

        Assert.True(parsed && score is not null, "Score fixture did not parse.");
        Assert.Equal("Leland", score!.Profile.Name);
        Assert.Equal("the Filcher", score.Profile.Title);
        Assert.Equal("thief", score.Profile.ClassName);
        Assert.Equal(3, score.Profile.Level);
        Assert.Equal(18, score.Attributes["Dex"].Current);
        Assert.Equal(132, score.HitPoints.Current);
        Assert.Equal(4905L, score.Experience);
        Assert.Equal(43, score.Exploration);
        Assert.Equal(9, score.CombatStats.Hitroll);
        Assert.Equal(100, score.CombatStats.ArmorClass);
        Assert.Equal("vulnerable", score.CombatStats.ArmorClassDescriptor);
        Assert.Equal(129, score.Inventory.Copper);
        Assert.Equal(0, score.Inventory.Silver);
        Assert.Equal(0, score.Inventory.Gold);
        Assert.True(score.Effects is not null && score.Effects.Count == 0, "Explicit no-spells line should produce a known-empty effect list.");
        return Task.CompletedTask;
    }

    private static Task AvendarScoreParserHandlesRandolphFixture()
    {
        CharacterScoreObserved score = ParseRandolphScoreFixture();

        Assert.Equal("Randolph", score.Profile.Name);
        Assert.Equal("the Smuggler", score.Profile.Title);
        Assert.Equal("human", score.Profile.Lineage);
        Assert.Equal("thief", score.Profile.ClassName);
        Assert.Equal(6, score.Profile.Level);
        Assert.Equal("male", score.Profile.Gender);
        Assert.Equal(18, score.Profile.Age);
        Assert.Equal("youthful", score.Profile.AgeDescriptor);
        Assert.Equal(8, score.Profile.Hours);
        Assert.Equal("None", score.Profile.Resonance);
        Assert.Equal("Chaotic Neutral", score.Profile.Alignment);

        AssertAttribute(score, "Str", 15, 15);
        AssertAttribute(score, "Int", 15, 15);
        AssertAttribute(score, "Wis", 15, 15);
        AssertAttribute(score, "Dex", 20, 18);
        AssertAttribute(score, "Con", 16, 15);
        AssertAttribute(score, "Chr", 15, 15);

        Assert.Equal(195, score.HitPoints.Current);
        Assert.Equal(195, score.HitPoints.Maximum);
        Assert.Equal(148, score.Mana.Current);
        Assert.Equal(148, score.Mana.Maximum);
        Assert.Equal(280, score.Movement.Current);
        Assert.Equal(280, score.Movement.Maximum);
        Assert.Equal(12858L, score.Experience);
        Assert.Equal(1727L, score.ExperienceToLevel);
        Assert.Equal(190, score.Exploration);
        Assert.Equal(15, score.CombatStats.Hitroll);
        Assert.Equal(6, score.CombatStats.Damroll);
        Assert.Equal(-5, score.CombatStats.Saves);
        Assert.Equal(87, score.CombatStats.ArmorClass);
        Assert.Equal("vulnerable", score.CombatStats.ArmorClassDescriptor);
        Assert.Equal(14, score.Inventory.Items);
        Assert.Equal(100, score.Inventory.MaxItems);
        Assert.Equal(73, score.Inventory.Weight);
        Assert.Equal(352, score.Inventory.MaxWeight);
        Assert.Equal(2, score.Inventory.Gold);
        Assert.Equal(0, score.Inventory.Silver);
        Assert.Equal(111, score.Inventory.Copper);
        return Task.CompletedTask;
    }

    private static async Task AvendarScoreCorePublishesImmediately()
    {
        await using EventPipeline events = new();
        ChannelReader<EventEnvelope> adapterInput = events.SubscribeLossless();
        ChannelReader<EventEnvelope> observer = events.SubscribeLossless();
        AvendarGameAdapter adapter = new(adapterInput, events);
        using CancellationTokenSource cancellation = new();
        Task worker = adapter.RunAsync(cancellation.Token);

        string fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "avendar-score-randolph.txt");
        string scoreText = File.ReadAllText(fixturePath);
        await events.PublishAsync(new ActionDispatching(Guid.NewGuid(), "score"), "test.action");
        await events.PublishAsync(new TextReceived(scoreText), "test.transport");

        CharacterScoreObserved? observed = null;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        while (observed is null)
        {
            EventEnvelope envelope = await observer.ReadAsync(timeout.Token);
            observed = envelope.Payload as CharacterScoreObserved;
        }

        cancellation.Cancel();
        await worker;

        Assert.Equal("Randolph", observed.Profile.Name);
        Assert.Equal("the Smuggler", observed.Profile.Title);
        Assert.Equal(195, observed.HitPoints.Current);
        Assert.Equal(12858L, observed.Experience);
    }

    private static Task RandolphScoreReducesToCanonicalCharacterState()
    {
        CharacterScoreObserved score = ParseRandolphScoreFixture();
        StateSnapshot state = StateReducer.Reduce(StateSnapshot.Initial, Envelope(1, score));
        CharacterState character = state.Character;

        Assert.Equal("Randolph", character.Profile.Name);
        Assert.Equal("the Smuggler", character.Profile.Title);
        Assert.Equal("human", character.Profile.Lineage);
        Assert.Equal("thief", character.Profile.ClassName);
        Assert.Equal(6, character.Profile.Level);
        Assert.Equal("male", character.Profile.Gender);
        Assert.Equal(18, character.Profile.Age);
        Assert.Equal("youthful", character.Profile.AgeDescriptor);
        Assert.Equal(8, character.Profile.Hours);
        Assert.Equal("None", character.Profile.Resonance);
        Assert.Equal("Chaotic Neutral", character.Profile.Alignment);
        AssertAttribute(character.Attributes, "Str", 15, 15);
        AssertAttribute(character.Attributes, "Int", 15, 15);
        AssertAttribute(character.Attributes, "Wis", 15, 15);
        AssertAttribute(character.Attributes, "Dex", 20, 18);
        AssertAttribute(character.Attributes, "Con", 16, 15);
        AssertAttribute(character.Attributes, "Chr", 15, 15);
        Assert.Equal(195, character.HitPoints.Current);
        Assert.Equal(195, character.HitPoints.Maximum);
        Assert.Equal(148, character.Mana.Current);
        Assert.Equal(148, character.Mana.Maximum);
        Assert.Equal(280, character.Movement.Current);
        Assert.Equal(280, character.Movement.Maximum);
        Assert.Equal(12858L, character.Experience);
        Assert.Equal(1727L, character.ExperienceToLevel);
        Assert.Equal(190, character.Exploration);
        Assert.Equal(15, character.CombatStats.Hitroll);
        Assert.Equal(6, character.CombatStats.Damroll);
        Assert.Equal(-5, character.CombatStats.Saves);
        Assert.Equal(87, character.CombatStats.ArmorClass);
        Assert.Equal("vulnerable", character.CombatStats.ArmorClassDescriptor);
        Assert.Equal(14, character.Inventory.Items);
        Assert.Equal(100, character.Inventory.MaxItems);
        Assert.Equal(73, character.Inventory.Weight);
        Assert.Equal(352, character.Inventory.MaxWeight);
        Assert.Equal(2, character.Inventory.Gold);
        Assert.Equal(111, character.Inventory.Copper);
        return Task.CompletedTask;
    }

    private static CharacterScoreObserved ParseRandolphScoreFixture()
    {
        string fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "avendar-score-randolph.txt");
        string[] lines = File.ReadAllLines(fixturePath);
        bool parsed = AvendarScoreParser.TryParse(lines, out CharacterScoreObserved? score);
        Assert.True(parsed && score is not null, "Randolph score fixture did not parse.");
        return score!;
    }

    private static void AssertAttribute(CharacterScoreObserved score, string name, int current, int baseValue)
    {
        Assert.True(score.Attributes.TryGetValue(name, out AttributeScore? attribute) && attribute is not null,
            $"Attribute {name} was not parsed.");
        Assert.Equal(current, attribute!.Current);
        Assert.Equal(baseValue, attribute.BaseValue);
    }

    private static void AssertAttribute(IReadOnlyDictionary<string, AttributeScore> attributes, string name, int current, int baseValue)
    {
        Assert.True(attributes.TryGetValue(name, out AttributeScore? attribute) && attribute is not null,
            $"Canonical attribute {name} was not populated.");
        Assert.Equal(current, attribute!.Current);
        Assert.Equal(baseValue, attribute.BaseValue);
    }

    private static Task GameplayProjectionsUseCanonicalState()
    {
        CharacterScoreObserved score = ParseRandolphScoreFixture();
        RoomObservationObserved lounge = ParseGoldenRoomFixture()[0];
        StateSnapshot state = StateReducer.Reduce(StateSnapshot.Initial, Envelope(1, score));
        state = StateReducer.Reduce(state, Envelope(2, new CharacterPromptObserved(
            195,
            195,
            148,
            148,
            280,
            280,
            12858,
            1727,
            "standing",
            "The Adventurer's Lounge",
            new ExitState(true, ["east", "south", "west"]),
            "inside",
            "light")));
        state = StateReducer.Reduce(state, Envelope(3, lounge));

        CharacterHudViewModel character = CharacterHudViewModel.From(state, jevEnabled: false, jevText: "OFF");
        RoomContextViewModel room = RoomContextViewModel.From(state, memory: null, metadata: null);

        Assert.Equal("Randolph the Smuggler", character.Name);
        Assert.Equal("Human • Thief • Level 6", character.Identity);
        Assert.Equal("Chaotic Neutral", character.Alignment);
        Assert.Equal("6", character.Level);
        Assert.Equal("12,858 XP", character.ExperienceText);
        Assert.Equal("1,727 XP", character.ExperienceRemainingText);
        Assert.Equal("195/195", character.HitPoints.Display);
        Assert.Equal("148/148", character.Mana.Display);
        Assert.Equal("280/280", character.Movement.Display);
        Assert.Equal("+15", character.Hitroll);
        Assert.Equal("+6", character.Damroll);
        Assert.Equal("-5", character.Saves);
        Assert.Equal("87 / Vulnerable", character.ArmorClass);
        Assert.Equal("190", character.Exploration);
        Assert.Equal("2g 111c", character.Wealth);
        Assert.Equal("14 / 100", character.Items);
        Assert.Equal("73 / 352", character.Weight);
        Assert.Equal("Equipment not observed", character.EquipmentSummary);
        Assert.Equal(0, character.Equipment.Count);
        Assert.Equal("20 (18)", character.Attributes.Single(attribute => attribute.Name == "DEX").Display);

        Assert.Equal("The Adventurer's Lounge", room.Name);
        Assert.Equal("inside • light", room.Metadata);
        Assert.SequenceEqual(["east", "south", "west"], room.Exits.Select(exit => exit.Direction));
        Assert.Equal("A kankoran student", Assert.Single(room.People).Description);
        Assert.SequenceEqual(
            ["A prominent bulletin board", "A small fountain"],
            room.Fixtures.Select(item => item.Description).OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        Assert.SequenceEqual(
            [
                "A small fountain gurgles here.",
                "A kankoran student looks consideringly to the western stairs."
            ],
            room.RecentObservations);
        return Task.CompletedTask;
    }

    private static Task AvendarSkillsParserSpansPagerPages()
    {
        string[] lines = SkillsFixture.Split('\n');
        bool parsed = AvendarSkillsParser.TryParse(lines, out SkillsSnapshotObserved? observed);

        Assert.True(parsed && observed is not null, "Skills fixture did not parse.");
        SkillState dagger = observed!.Skills.Single(skill => skill.Name == "dagger");
        SkillState dirt = observed.Skills.Single(skill => skill.Name == "dirt kicking");
        SkillState dualWield = observed.Skills.Single(skill => skill.Name == "dual wield");
        SkillState loot = observed.Skills.Single(skill => skill.Name == "loot");

        Assert.Equal(1, dagger.RequiredLevel);
        Assert.Equal(75, dagger.ProficiencyPercent);
        Assert.Equal(SkillAvailability.Available, dirt.Availability);
        Assert.Equal(3, dirt.RequiredLevel);
        Assert.Equal(SkillAvailability.Unavailable, dualWield.Availability);
        Assert.True(dualWield.ProficiencyPercent is null, "n/a must not be modeled as zero proficiency.");
        Assert.Equal(27, loot.RequiredLevel);
        return Task.CompletedTask;
    }


    private static Task AvendarSpellsParserModelsExplicitEmptyCatalog()
    {
        bool parsed = AvendarSpellsParser.TryParse(
            ["No spells found."],
            out SpellsSnapshotObserved? observed);

        Assert.True(parsed && observed is not null, "Explicit empty spells response must parse.");
        Assert.Equal(0, observed!.Spells.Count);
        Assert.Equal(ObservationCompleteness.Complete, observed.Completeness);
        return Task.CompletedTask;
    }

    private static Task AvendarEquipmentParserPreservesSlotsAndBrands()
    {
        string[] lines =
        [
            "You are using:",
            "<worn on finger>    a copper ring",
            "<worn on finger>    [nothing]",
            "<worn around neck>  [nothing]",
            "<worn around neck>  [nothing]",
            "<worn on torso>     a copper chainmail shirt",
            "<branded>           the Sigil of the Black Staff",
            "<wielded>           a rawhide whip",
            "<dual wielded>      [nothing]"
        ];

        bool parsed = AvendarEquipmentParser.TryParse(lines, out IReadOnlyList<EquipmentSlotState>? slots);
        Assert.True(parsed, "Equipment fixture did not parse.");
        Assert.Equal(8, slots!.Count);
        Assert.Equal("worn on finger", slots[0].Slot);
        Assert.Equal(1, slots[0].Ordinal);
        Assert.Equal("a copper ring", slots[0].Item);
        Assert.Equal(2, slots[1].Ordinal);
        Assert.True(slots[1].IsEmpty, "Second finger slot should be preserved as empty.");
        Assert.Equal("branded", slots[5].Slot);
        Assert.Equal("the Sigil of the Black Staff", slots[5].Item);
        Assert.Equal("wielded", slots[6].Slot);
        Assert.Equal("a rawhide whip", slots[6].Item);
        Assert.Equal("dual wielded", slots[7].Slot);
        Assert.True(slots[7].IsEmpty, "Dual-wield slot should be preserved as empty.");
        return Task.CompletedTask;
    }

    private static Task AvendarInventoryParserCapturesCarriedItems()
    {
        string[] lines =
        [
            "You are carrying:",
            "a copper polearm",
            "(Glowing) a small hide pack",
            "a quilted bedroll"
        ];

        bool parsed = AvendarInventoryParser.TryParse(lines, out IReadOnlyList<string>? items);
        Assert.True(parsed && items is not null, "Inventory fixture did not parse.");
        Assert.SequenceEqual(
            ["a copper polearm", "a small hide pack", "a quilted bedroll"],
            items!);
        return Task.CompletedTask;
    }

    private static Task InventorySnapshotPopulatesCanonicalItems()
    {
        StateSnapshot next = StateReducer.Reduce(
            StateSnapshot.Initial,
            Envelope(1, new InventorySnapshotObserved(
                ["a copper polearm", "a small hide pack", "a copper dagger"],
                ObservationCompleteness.Complete)));

        Assert.Equal(ObservationCompleteness.Complete, next.Character.InventoryCompleteness);
        Assert.Equal(3, next.Character.Inventory.Items);
        Assert.SequenceEqual(
            ["a copper polearm", "a small hide pack", "a copper dagger"],
            next.Character.CarriedItems);
        return Task.CompletedTask;
    }

    private static Task AvendarItemIdParserCapturesCommonIdentification()
    {
        string[] lines =
        [
            "+----------------------------------+",
            "| Object:   a copper sword         |",
            "| Flags:    none                   |",
            "| Weight:   8.0                    |",
            "| Wear:     wield                  |",
            "| Level:    5                      |",
            "| Material: copper                 |",
            "| -------------------------------- |",
            "| Type:         weapon             |",
            "| Weapon type:  sword              |",
            "| Weapon flags: none               |",
            "| Damage type:  slash              |",
            "| Damage dice:  6d4 (average 12.0) |",
            "| -------------------------------- |",
            "| Affects moves by 20              |",
            "| Affects hp by 3                  |",
            "| Affects constitution by 1        |",
            "+----------------------------------+"
        ];

        bool parsed = AvendarItemIdentificationParser.TryParse(lines, out ItemIdentified? observed);
        Assert.True(parsed && observed is not null, "Common item identification fixture did not parse.");
        Assert.Equal("a copper sword", observed!.Item.Name);
        Assert.Equal(8.0m, observed.Item.Weight);
        Assert.Equal(5, observed.Item.Level);
        Assert.Equal("copper", observed.Item.Material);
        Assert.Equal("weapon", observed.Item.ItemType);
        Assert.Equal("sword", observed.Item.WeaponType);
        Assert.Equal("slash", observed.Item.DamageType);
        Assert.Equal("6d4", observed.Item.DamageDice);
        Assert.Equal(12.0m, observed.Item.DamageAverage);
        Assert.Equal("wield", observed.Item.WearLocations.Single());
        Assert.Equal("20", observed.Item.ExtraFields["affect moves"]);
        Assert.Equal("3", observed.Item.ExtraFields["affect hp"]);
        Assert.Equal("1", observed.Item.ExtraFields["affect constitution"]);
        return Task.CompletedTask;
    }

    private static Task AvendarAbilityHelpParserCapturesSkillReference()
    {
        string[] lines =
        [
            "/---------------------------------------------------------------------------\\",
            "|          Dirt Kicking (Skill)                                             |",
            "\\---------------------------------------------------------------------------/",
            "",
            "Activation lag  :  1.5 rounds",
            "Activation cost :  5 mana",
            "",
            "Syntax: Dirt <target>",
            "",
            "This skill attempts to temporarily blind a person by kicking dirt in their",
            "eyes. This skill cannot be used on or underwater, or in the air."
        ];

        bool parsed = AvendarAbilityHelpParser.TryParse(lines, out AbilityHelpObserved? observed);
        Assert.True(parsed && observed is not null, "Ability help fixture did not parse.");
        Assert.Equal("Dirt Kicking", observed!.Help.Name);
        Assert.Equal(AbilityHelpKind.Skill, observed.Help.Kind);
        Assert.Equal(1.5m, observed.Help.ActivationLagRounds);
        Assert.Equal(5, observed.Help.ActivationManaCost);
        Assert.Equal("Dirt <target>", observed.Help.Syntax);
        Assert.True(observed.Help.Description.Contains("temporarily blind", StringComparison.OrdinalIgnoreCase), "Ability help description should preserve the blindness behavior.");
        Assert.True(observed.Help.Description.Contains("underwater", StringComparison.OrdinalIgnoreCase), "Ability help description should preserve environmental restrictions.");
        Assert.True(!observed.Help.Description.Contains('\n') && !observed.Help.Description.Contains('\r'), "Ability help semantic description must remove terminal hard wrapping.");
        Assert.Equal("This skill attempts to temporarily blind a person by kicking dirt in their eyes. This skill cannot be used on or underwater, or in the air.", observed.Help.Description);
        return Task.CompletedTask;
    }

    private static Task EquipmentSnapshotPreservesDuplicateEmptySlots()
    {
        EquipmentSlotState[] slots =
        [
            new("worn on finger", 1, "a copper ring"),
            new("worn on finger", 2, null),
            new("worn around wrist", 1, null),
            new("worn around wrist", 2, null),
            new("wielded", 1, "a rawhide whip")
        ];

        StateSnapshot next = StateReducer.Reduce(
            StateSnapshot.Initial,
            Envelope(1, new EquipmentSnapshotObserved(slots)));

        Assert.Equal(ObservationCompleteness.Complete, next.Character.Equipment.Completeness);
        Assert.Equal(5, next.Character.Equipment.Slots.Count);
        Assert.Equal("a rawhide whip", next.Character.Equipment.MainHand);
        Assert.Equal(2, next.Character.Equipment.Slots.Count(slot => slot.Slot == "worn on finger"));
        Assert.True(next.Character.Equipment.Slots[1].IsEmpty, "Reducer should preserve duplicate empty equipment slots.");
        return Task.CompletedTask;
    }

    private static Task AbilityParserClassifiesKnownDomains()
    {
        string[] lines =
        [
            "Level 1: dagger 75%  steal 1%  recover 1%",
            "Level 3: dirt kicking 1%  mystery craft n/a"
        ];
        bool parsed = AvendarSkillsParser.TryParse(lines, out SkillsSnapshotObserved? observed);

        Assert.True(parsed && observed is not null, "Ability fixture did not parse.");
        Assert.Equal(AbilityDomain.Combat, observed!.Skills.Single(skill => skill.Name == "dagger").Domain);
        Assert.Equal(AbilityDomain.Utility, observed.Skills.Single(skill => skill.Name == "steal").Domain);
        Assert.Equal(AbilityDomain.Recovery, observed.Skills.Single(skill => skill.Name == "recover").Domain);
        Assert.Equal(AbilityDomain.Combat, observed.Skills.Single(skill => skill.Name == "dirt kicking").Domain);
        Assert.Equal(AbilityDomain.Unknown, observed.Skills.Single(skill => skill.Name == "mystery craft").Domain);
        return Task.CompletedTask;
    }

    private static Task PartialAbilitySnapshotsPreserveKnownEntries()
    {
        StateSnapshot current = StateSnapshot.Initial with
        {
            Character = StateSnapshot.Initial.Character with
            {
                Skills =
                [
                    new SkillState("dagger", 1, SkillAvailability.Available, 75, true, AbilityDomain.Combat),
                    new SkillState("dual wield", 8, SkillAvailability.Unavailable, null)
                ],
                SkillsCompleteness = ObservationCompleteness.Complete
            }
        };

        SkillsSnapshotObserved partial = new(
            [new SkillState("dagger", 1, SkillAvailability.Available, 80, true, AbilityDomain.Combat)],
            ObservationCompleteness.Partial);
        StateSnapshot next = StateReducer.Reduce(current, Envelope(77, partial));

        Assert.Equal(2, next.Character.Skills.Count);
        Assert.Equal(80, next.Character.Skills.Single(skill => skill.Name == "dagger").ProficiencyPercent);
        Assert.Equal(SkillAvailability.Unavailable, next.Character.Skills.Single(skill => skill.Name == "dual wield").Availability);
        Assert.Equal(ObservationCompleteness.Partial, next.Character.SkillsCompleteness);
        return Task.CompletedTask;
    }

    private static async Task ClientSettingsAllowCompactTranscriptSizes()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nexmud-tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        try
        {
            ClientSettingsStore store = new(path);
            Assert.Equal(14d, ClientSettings.Default.TranscriptFontSize);

            await store.SaveAsync(ClientSettings.Default with { TranscriptFontSize = 8 });
            ClientSettings compact = await store.LoadAsync();
            Assert.Equal(8d, compact.TranscriptFontSize);

            await store.SaveAsync(ClientSettings.Default with { TranscriptFontSize = 7 });
            ClientSettings clampedLow = await store.LoadAsync();
            Assert.Equal(8d, clampedLow.TranscriptFontSize);

            await store.SaveAsync(ClientSettings.Default with { TranscriptFontSize = 30 });
            ClientSettings clampedHigh = await store.LoadAsync();
            Assert.Equal(24d, clampedHigh.TranscriptFontSize);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static async Task ClientSettingsRoundTripJevAuthority()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nexmud-tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        try
        {
            ClientSettingsStore store = new(path);
            Dictionary<JevDomain, JevAuthority> domains = Enum.GetValues<JevDomain>()
                .ToDictionary(domain => domain, _ => JevAuthority.Observe);
            domains[JevDomain.Combat] = JevAuthority.Suggest;
            ClientSettings settings = new ClientSettings(
                JevPreset.Custom,
                domains,
                "avendar.net",
                9999,
                false,
                "xterm-256color",
                16,
                false,
                false) with
            {
                AutoLogSessions = true,
                SlurpTelemetryPrompt = true,
                HighlightRules = [new TranscriptHighlightRule("PK", "#FFAA00", Bold: true, Underline: true)],
                LogFormat = TranscriptLogFormat.JsonLines,
                Aliases = [new CommandAlias("kk", "kill $*")],
                Triggers = [new TriggerRule("You are hungry", "eat bread")],
                Timers = [new CommandTimer("keepalive", 30, "look", Repeat: true, Enabled: true)],
                KeyBindings = [new CommandKeyBinding("Primary+1", "look", Name: "Look", Context: KeybindingContext.World, Action: KeybindingActionKind.SendCommand, Priority: 4)],
                Input = new InputPreferences(750, PersistHistory: true, DeduplicateConsecutiveHistory: false, CompletionEnabled: true, CompletionTokenLimit: 7000, LocalEcho: false),
                Output = new OutputPreferences(TimestampRenderMode.TimeWithMilliseconds, 7000, SplitOutputEnabled: false, NotifyWhenUnfocused: false),
                OutputRules = [new OutputTransformationRule("guard", "Guard", "cityguard", Actions: [new OutputRuleAction(OutputRuleActionKind.Substitute, "[Guard]")])],
                Workspace = new WorkspacePreferences(1440, 900, true, 420, "Jev", 210),
                JevEnabled = false,
                CommandSeparator = "|",
                Protocols = new ProtocolPreferences(Eor: false),
                Mapper = new MapperPreferences(AutoOpenDoors: true, DoorOpenCommandTemplate: "open {direction}")
            };

            await store.SaveAsync(settings);
            ClientSettings loaded = await store.LoadAsync();

            Assert.Equal(JevPreset.Custom, loaded.JevPreset);
            Assert.Equal(JevAuthority.Suggest, loaded.JevDomains[JevDomain.Combat]);
            Assert.Equal(JevAuthority.Observe, loaded.JevDomains[JevDomain.Navigation]);
            Assert.Equal("avendar.net", loaded.Host);
            Assert.Equal(9999, loaded.Port);
            Assert.Equal("xterm-256color", loaded.TerminalType);
            Assert.Equal(16d, loaded.TranscriptFontSize);
            Assert.False(loaded.ShowContextDock, "Context dock setting did not round-trip.");
            Assert.False(loaded.AutoOpenCombatContext, "Combat context setting did not round-trip.");
            Assert.True(loaded.AutoLogSessions, "Automatic logging setting did not round-trip.");
            Assert.True(loaded.SlurpTelemetryPrompt, "Prompt slurp setting did not round-trip.");
            TranscriptHighlightRule highlight = Assert.Single(loaded.HighlightRules!);
            Assert.Equal("PK", highlight.Pattern);
            Assert.Equal("#FFAA00", highlight.Foreground);
            Assert.True(highlight.Bold, "Highlight bold setting did not round-trip.");
            Assert.True(highlight.Underline, "Highlight underline setting did not round-trip.");
            Assert.Equal(TranscriptLogFormat.JsonLines, loaded.LogFormat);
            Assert.Equal("kk", Assert.Single(loaded.Aliases!).Name);
            Assert.Equal("You are hungry", Assert.Single(loaded.Triggers!).Pattern);
            Assert.Equal(30, Assert.Single(loaded.Timers!).IntervalSeconds);
            CommandKeyBinding loadedBinding = Assert.Single(loaded.KeyBindings!);
            Assert.Equal("Primary+1", loadedBinding.Gesture);
            Assert.Equal("Look", loadedBinding.Name);
            Assert.Equal(KeybindingContext.World, loadedBinding.Context);
            Assert.Equal(KeybindingActionKind.SendCommand, loadedBinding.Action);
            Assert.Equal(4, loadedBinding.Priority);
            Assert.Equal(750, loaded.Input!.HistoryMaximumEntries);
            Assert.False(loaded.Input.DeduplicateConsecutiveHistory, "History de-duplication setting did not round-trip.");
            Assert.False(loaded.Input.LocalEcho, "Local echo setting did not round-trip.");
            Assert.Equal(7000, loaded.Input.CompletionTokenLimit);
            Assert.Equal(TimestampRenderMode.TimeWithMilliseconds, loaded.Output!.TimestampMode);
            Assert.Equal(7000, loaded.Output.ScrollbackMaximumEntries);
            Assert.False(loaded.Output.SplitOutputEnabled, "Split-output setting did not round-trip.");
            Assert.False(loaded.Output.NotifyWhenUnfocused, "Notification focus setting did not round-trip.");
            Assert.Equal("guard", Assert.Single(loaded.OutputRules!).Id);
            Assert.Equal("Jev", loaded.Workspace!.DockView);
            Assert.Equal(420d, loaded.Workspace.DockWidth);
            Assert.False(loaded.JevEnabled, "Jev master enabled state did not round-trip.");
            Assert.Equal("|", loaded.CommandSeparator);
            Assert.False(loaded.Protocols!.Eor, "EOR protocol preference did not round-trip.");
            Assert.True(loaded.Mapper!.AutoOpenDoors, "Mapper automatic door-opening preference did not round-trip.");
            Assert.Equal("open {direction}", loaded.Mapper.DoorOpenCommandTemplate);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static async Task RuntimeJevSavePersistsBeforeReturning()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nexmud-tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        try
        {
            ClientSettingsStore store = new(path);
            await using NexMudRuntime runtime = new(store);
            runtime.Start();
            await runtime.RestoreSettingsAsync();

            Dictionary<JevDomain, JevAuthority> domains = Enum.GetValues<JevDomain>()
                .ToDictionary(domain => domain, _ => JevAuthority.Off);
            domains[JevDomain.Combat] = JevAuthority.Suggest;
            JevAuthoritySnapshot profile = JevAuthoritySnapshot.Create(JevPreset.Custom, domains);

            await runtime.ApplyAndSaveAuthorityAsync(profile);
            ClientSettings loaded = await store.LoadAsync();

            Assert.Equal(JevPreset.Custom, loaded.JevPreset);
            Assert.Equal(JevAuthority.Suggest, loaded.JevDomains[JevDomain.Combat]);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static Task DirtKickingEligibilityHonorsTerrain()
    {
        SkillActionDefinition dirt = AvendarActionCapabilities.KnownActions["dirt kicking"];
        StateSnapshot state = StateWithAvailableSkill("dirt kicking", mana: 20, terrain: "inside");
        ActionEligibilityResult allowed = AvendarActionCapabilities.Evaluate(dirt, state, hasTarget: true);
        ActionEligibilityResult blocked = AvendarActionCapabilities.Evaluate(
            dirt,
            state with { Room = state.Room with { Terrain = "underwater" } },
            hasTarget: true);

        Assert.Equal(ActionEligibility.Eligible, allowed.Eligibility);
        Assert.Equal(ActionEligibility.Ineligible, blocked.Eligibility);
        return Task.CompletedTask;
    }

    private static Task SkillEligibilityUnknownBeforeObservation()
    {
        SkillActionDefinition dirt = AvendarActionCapabilities.KnownActions["dirt kicking"];
        StateSnapshot state = StateSnapshot.Initial with
        {
            Character = StateSnapshot.Initial.Character with { Mana = new VitalState(20, 20) },
            Room = StateSnapshot.Initial.Room with { Terrain = "inside" }
        };

        ActionEligibilityResult result = AvendarActionCapabilities.Evaluate(dirt, state, hasTarget: true);
        Assert.Equal(ActionEligibility.Unknown, result.Eligibility);
        return Task.CompletedTask;
    }

    private static Task DirtEligibilityUnknownWithoutTerrain()
    {
        SkillActionDefinition dirt = AvendarActionCapabilities.KnownActions["dirt kicking"];
        StateSnapshot state = StateWithAvailableSkill("dirt kicking", mana: 20, terrain: "inside") with
        {
            Room = StateSnapshot.Initial.Room
        };

        ActionEligibilityResult result = AvendarActionCapabilities.Evaluate(dirt, state, hasTarget: true);
        Assert.Equal(ActionEligibility.Unknown, result.Eligibility);
        return Task.CompletedTask;
    }

    private static Task BackstabEligibilityRequiresWeaponKnowledge()
    {
        SkillActionDefinition backstab = AvendarActionCapabilities.KnownActions["backstab"];
        StateSnapshot state = StateWithAvailableSkill("backstab", mana: 20, terrain: "inside");
        ActionEligibilityResult unknown = AvendarActionCapabilities.Evaluate(backstab, state, hasTarget: true);

        Assert.Equal(ActionEligibility.Unknown, unknown.Eligibility);
        Assert.True(unknown.Reasons.Any(reason => reason.Contains("weapon", StringComparison.OrdinalIgnoreCase)), "Unknown backstab eligibility should identify missing weapon knowledge.");
        return Task.CompletedTask;
    }

    private static Task IdentifiedEquippedWeaponResolvesBackstabLegality()
    {
        StateSnapshot initial = StateWithAvailableSkill("backstab", mana: 20, terrain: "inside") with
        {
            Character = StateWithAvailableSkill("backstab", mana: 20, terrain: "inside").Character with
            {
                Equipment = new EquipmentState("a copper sword", new Dictionary<string, string>())
                {
                    Slots = [new EquipmentSlotState("wielded", 1, "a copper sword")],
                    Completeness = ObservationCompleteness.Complete
                }
            }
        };

        ItemIdentification sword = new(
            "a copper sword",
            Array.Empty<string>(),
            8m,
            ["wield"],
            5,
            "copper",
            "weapon",
            "sword",
            Array.Empty<string>(),
            "slash",
            "6d4",
            12m,
            new Dictionary<string, string>(),
            "fixture");
        StateSnapshot identified = StateReducer.Reduce(initial, Envelope(2, new ItemIdentified(sword)));
        identified = identified with
        {
            Session = identified.Session with { InputMode = SessionInputMode.Normal },
            Combat = new CombatState(true, null, "a human pupil", null, null, null, null)
        };

        bool created = AvendarJevRequestFactory.TryCreateCombatRequest(identified, out JevChoiceRequest? request);
        Assert.True(created && request is not null, "Expected a combat request after identifying the equipped weapon.");
        Assert.True(request!.Criteria.ContainsKey("backstab"), "A slash weapon identification should resolve backstab's weapon precondition.");
        return Task.CompletedTask;
    }

    private static Task ZeroCostAbilityToleratesUnknownMana()
    {
        SkillActionDefinition recover = AvendarActionCapabilities.KnownActions["recover"];
        StateSnapshot state = StateSnapshot.Initial with
        {
            Character = StateSnapshot.Initial.Character with
            {
                Skills = [new SkillState("recover", 1, SkillAvailability.Available, 1, true, AbilityDomain.Recovery)]
            }
        };

        ActionEligibilityResult result = AvendarActionCapabilities.Evaluate(recover, state, hasTarget: false);
        Assert.Equal(ActionEligibility.Eligible, result.Eligibility);
        return Task.CompletedTask;
    }

    private static Task CombatJevRequestFiltersCapabilities()
    {
        StateSnapshot initial = StateSnapshot.Initial;
        StateSnapshot state = initial with
        {
            Session = initial.Session with { InputMode = SessionInputMode.Normal },
            Character = initial.Character with
            {
                HitPoints = new VitalState(40, 100),
                Mana = new VitalState(20, 20),
                Skills =
                [
                    new SkillState("dirt kicking", 3, SkillAvailability.Available, 75, true, AbilityDomain.Combat),
                    new SkillState("recover", 1, SkillAvailability.Available, 75, true, AbilityDomain.Recovery),
                    new SkillState("backstab", 1, SkillAvailability.Available, 75, true, AbilityDomain.Combat)
                ]
            },
            Room = initial.Room with { Terrain = "inside" },
            Combat = new CombatState(true, null, "a human pupil", "pretty hurt", null, null, null)
        };

        bool created = AvendarJevRequestFactory.TryCreateCombatRequest(state, out JevChoiceRequest? request);
        Assert.True(created && request is not null, "Expected a combat decision request.");
        Assert.True(request!.Criteria.ContainsKey("continue"), "Automatic combat continuation must always be an option.");
        Assert.True(request.Criteria.ContainsKey("dirt_kicking"), "Eligible dirt kicking must be exposed as a semantic decision action.");
        Assert.True(request.Criteria.ContainsKey("recover"), "Eligible recovery must be exposed.");
        Assert.False(request.Criteria.ContainsKey("backstab"), "Backstab must stay hidden while weapon type is unknown.");
        bool materialized = AvendarJevRequestFactory.TryMaterializeCombatAction(
            state,
            "dirt_kicking",
            out string? command,
            out string? rejectionReason);
        Assert.True(materialized, rejectionReason ?? "Expected dirt_kicking to materialize.");
        Assert.Equal("dirt a human pupil", command);
        return Task.CompletedTask;
    }

    private static Task CombatJevRequestUsesTypedQuestions()
    {
        StateSnapshot initial = StateSnapshot.Initial;
        StateSnapshot state = initial with
        {
            Session = initial.Session with { InputMode = SessionInputMode.Normal },
            Character = initial.Character with
            {
                HitPoints = new VitalState(40, 100),
                Mana = new VitalState(20, 20),
                Skills = [new SkillState("recover", 1, SkillAvailability.Available, 75, true, AbilityDomain.Recovery)]
            },
            Combat = new CombatState(true, null, "a human pupil", "pretty hurt", null, null, null)
        };

        bool created = AvendarJevRequestFactory.TryCreateCombatRequest(state, out JevChoiceRequest? request);
        Assert.True(created && request is not null, "Expected a combat decision request.");
        Assert.Equal(2, request!.AuxiliaryQuestions!.Count);
        Assert.Equal(JevQuestionType.Score, request.AuxiliaryQuestions[0].Type);
        Assert.Equal("danger", request.AuxiliaryQuestions[0].Id);
        Assert.Equal(JevQuestionType.Noul, request.AuxiliaryQuestions[1].Type);
        Assert.Equal("disengage", request.AuxiliaryQuestions[1].Id);
        return Task.CompletedTask;
    }

    private static Task IdleCombatJevCanChooseObservedTarget()
    {
        StateSnapshot initial = StateSnapshot.Initial;
        StateSnapshot state = initial with
        {
            Session = initial.Session with { InputMode = SessionInputMode.Normal },
            Character = initial.Character with
            {
                HitPoints = new VitalState(100, 100),
                Mana = new VitalState(100, 100),
                Movement = new VitalState(100, 100),
                Position = "standing"
            },
            Room = initial.Room with
            {
                Contents =
                [
                    new RoomContentObservation(
                        "An oversized rat snarls here.",
                        "an oversized rat",
                        RoomEntityKind.Occupant,
                        RoomEntityTraits.Mobile,
                        ["rat"])
                ],
                ContentsCompleteness = ObservationCompleteness.Complete
            }
        };

        bool created = AvendarJevRequestFactory.TryCreateCombatRequest(state, out JevChoiceRequest? request);
        Assert.True(created && request is not null, "Expected idle target acquisition request.");
        Assert.True(request!.Criteria.ContainsKey("engage:rat"), "Observed target should be a closed Jev choice.");
        bool materialized = AvendarJevRequestFactory.TryMaterializeCombatAction(state, "engage:rat", out string? command, out string? reason);
        Assert.True(materialized, reason ?? "Expected engage action to materialize.");
        Assert.Equal("kill rat", command);
        return Task.CompletedTask;
    }

    private static Task IdleCombatJevProtectsRecentSpeakers()
    {
        StateSnapshot initial = StateSnapshot.Initial;
        StateSnapshot state = initial with
        {
            Session = initial.Session with { InputMode = SessionInputMode.Normal },
            Character = initial.Character with
            {
                HitPoints = new VitalState(100, 100),
                Mana = new VitalState(100, 100),
                Movement = new VitalState(100, 100),
                Position = "standing"
            },
            Room = initial.Room with
            {
                Contents =
                [
                    new RoomContentObservation(
                        "Michadi Fippirea floats here.",
                        "Michadi Fippirea",
                        RoomEntityKind.Occupant,
                        RoomEntityTraits.Mobile,
                        ["michadi"]),
                    new RoomContentObservation(
                        "An oversized rat snarls here.",
                        "an oversized rat",
                        RoomEntityKind.Occupant,
                        RoomEntityTraits.Mobile,
                        ["rat"])
                ],
                ContentsCompleteness = ObservationCompleteness.Complete
            }
        };
        AvendarAutonomyConstraints constraints = new(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            null,
            [],
            new HashSet<string>(new[] { "Michadi Fippirea" }, StringComparer.OrdinalIgnoreCase));

        bool created = AvendarJevRequestFactory.TryCreateCombatRequest(state, null, constraints, out JevChoiceRequest? request);
        Assert.True(created && request is not null, "Expected combat target selection with one safe grind target.");
        Assert.False(request!.Criteria.ContainsKey("engage:michadi"), "A recent speaker must not be offered as a blind grind target.");
        Assert.True(request.Criteria.ContainsKey("engage:rat"), "Unprotected grind target should remain available.");
        return Task.CompletedTask;
    }

    private static Task IdleCombatJevProtectsTargetAlias()
    {
        StateSnapshot initial = StateSnapshot.Initial;
        StateSnapshot state = initial with
        {
            Session = initial.Session with { InputMode = SessionInputMode.Normal },
            Character = initial.Character with
            {
                HitPoints = new VitalState(100, 100),
                Mana = new VitalState(100, 100),
                Movement = new VitalState(100, 100),
                Position = "standing"
            },
            Room = initial.Room with
            {
                Contents =
                [
                    new RoomContentObservation(
                        "An exotic young woman floats languidly within the test area.",
                        null,
                        RoomEntityKind.Occupant,
                        RoomEntityTraits.Mobile,
                        ["woman"]),
                    new RoomContentObservation(
                        "An oversized rat snarls here.",
                        "an oversized rat",
                        RoomEntityKind.Occupant,
                        RoomEntityTraits.Mobile,
                        ["rat"])
                ],
                ContentsCompleteness = ObservationCompleteness.Complete
            }
        };
        AvendarAutonomyConstraints constraints = new(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            null,
            [],
            new HashSet<string>(new[] { "woman" }, StringComparer.OrdinalIgnoreCase));

        bool created = AvendarJevRequestFactory.TryCreateCombatRequest(state, null, constraints, out JevChoiceRequest? request);
        Assert.True(created && request is not null, "Expected one unprotected grind target to remain.");
        Assert.False(request!.Criteria.ContainsKey("engage:woman"), "Protected target keyword must exclude the described social NPC.");
        Assert.True(request.Criteria.ContainsKey("engage:rat"), "Unprotected target should remain available.");
        return Task.CompletedTask;
    }

    private static Task IdleCombatJevDefersToRecoveryWhenDepleted()
    {
        StateSnapshot initial = StateSnapshot.Initial;
        StateSnapshot state = initial with
        {
            Session = initial.Session with { InputMode = SessionInputMode.Normal },
            Character = initial.Character with
            {
                HitPoints = new VitalState(20, 100),
                Mana = new VitalState(100, 100),
                Movement = new VitalState(100, 100),
                Position = "standing"
            },
            Room = initial.Room with
            {
                Contents =
                [
                    new RoomContentObservation(
                        "An oversized rat snarls here.",
                        "an oversized rat",
                        RoomEntityKind.Occupant,
                        RoomEntityTraits.Mobile,
                        ["rat"])
                ],
                ContentsCompleteness = ObservationCompleteness.Complete
            }
        };

        bool combatCreated = AvendarJevRequestFactory.TryCreateCombatRequest(state, out _);
        bool recoveryCreated = AvendarJevRequestFactory.TryCreateRecoveryRequest(state, null, out JevChoiceRequest? recovery);
        Assert.False(combatCreated, "Idle target acquisition must not preempt recovery while depleted.");
        Assert.True(recoveryCreated && recovery is not null, "Recovery should own the depleted idle state.");
        return Task.CompletedTask;
    }

    private static Task RecoveryJevRequestExposesActions()
    {
        StateSnapshot initial = StateSnapshot.Initial;
        StateSnapshot state = initial with
        {
            Session = initial.Session with { InputMode = SessionInputMode.Normal },
            Character = initial.Character with
            {
                HitPoints = new VitalState(40, 100),
                Mana = new VitalState(100, 100),
                Movement = new VitalState(100, 100),
                Position = "standing",
                Skills = [new SkillState("recover", 1, SkillAvailability.Available, 75, true, AbilityDomain.Recovery)]
            }
        };

        bool created = AvendarJevRequestFactory.TryCreateRecoveryRequest(state, null, out JevChoiceRequest? request);
        Assert.True(created && request is not null, "Expected recovery request while injured.");
        Assert.True(request!.Criteria.ContainsKey("recover"), "Recover should be offered when known and legal.");
        Assert.True(request.Criteria.ContainsKey("rest"), "Rest should be offered while standing and depleted.");
        bool materialized = AvendarJevRequestFactory.TryMaterializeRecoveryAction(state, "recover", out string? command, out string? reason);
        Assert.True(materialized, reason ?? "Expected recover action to materialize.");
        Assert.Equal("recover", command);
        return Task.CompletedTask;
    }

    private static Task NavigationJevRequestExposesExits()
    {
        StateSnapshot initial = StateSnapshot.Initial;
        StateSnapshot state = initial with
        {
            Session = initial.Session with { InputMode = SessionInputMode.Normal },
            Character = initial.Character with
            {
                HitPoints = new VitalState(100, 100),
                Mana = new VitalState(100, 100),
                Movement = new VitalState(100, 100),
                Position = "standing"
            },
            Room = initial.Room with
            {
                Id = "room-a",
                Name = "A Room",
                Exits = new ExitState(true, ["north", "south"],
                [
                    new RoomExitObservation("north", true, ExitDoorState.Open, ExitTraversability.Traversable),
                    new RoomExitObservation("south", true, ExitDoorState.Closed, ExitTraversability.Blocked, "closed door")
                ])
            }
        };

        bool created = AvendarJevRequestFactory.TryCreateNavigationRequest(state, null, out JevChoiceRequest? request);
        Assert.True(created && request is not null, "Expected navigation request with a traversable exit.");
        Assert.True(request!.Criteria.ContainsKey("move:north"), "Traversable north exit should be exposed.");
        Assert.False(request.Criteria.ContainsKey("move:south"), "Blocked south exit must not be exposed.");
        bool materialized = AvendarJevRequestFactory.TryMaterializeNavigationAction(state, "move:north", out string? command, out string? reason);
        Assert.True(materialized, reason ?? "Expected move north to materialize.");
        Assert.Equal("north", command);
        return Task.CompletedTask;
    }

    private static Task CombatJevRequestIncludesPersistentContext()
    {
        StateSnapshot initial = StateSnapshot.Initial;
        StateSnapshot state = initial with
        {
            Session = initial.Session with { InputMode = SessionInputMode.Normal },
            Character = initial.Character with
            {
                HitPoints = new VitalState(40, 100),
                Skills = [new SkillState("recover", 1, SkillAvailability.Available, 75, true, AbilityDomain.Recovery)]
            },
            Combat = new CombatState(true, null, "a human pupil", "pretty hurt", null, null, null)
        };

        object memory = new
        {
            priorEncounters = 3,
            recentHumanCommands = new[] { "dirt pupil", "flee" }
        };
        bool created = AvendarJevRequestFactory.TryCreateCombatRequest(state, memory, out JevChoiceRequest? request);
        Assert.True(created && request is not null, "Expected a combat decision request.");
        string serialized = JsonSerializer.Serialize(request!.State);
        Assert.True(serialized.Contains("priorEncounters", StringComparison.Ordinal), "Persistent context was not projected into Jev state.");
        Assert.True(serialized.Contains("dirt pupil", StringComparison.Ordinal), "Recent human command context was not projected into Jev state.");
        return Task.CompletedTask;
    }

    private static async Task JevPersistentContextIncludesAbilityHelpAndCommandSources()
    {
        string databasePath = Path.Combine(Path.GetTempPath(), $"nexmud-test-{Guid.NewGuid():N}.db");
        await using EventPipeline events = new();
        StateReducer reducer = new(events.StateEvents);
        WorldKnowledgeStore knowledge = new(events.SubscribeLossless(), reducer, databasePath);
        using CancellationTokenSource cts = new();
        Task reducerTask = reducer.RunAsync(cts.Token);
        Task knowledgeTask = knowledge.RunAsync(cts.Token);

        await events.PublishAsync(new ConnectionStateChanged(ConnectionStatus.Connected, "avendar.net", 9999), "test");
        await events.PublishAsync(new SessionInputModeChanged(SessionInputMode.Normal), "test");
        await events.PublishAsync(new SkillsSnapshotObserved([
            new SkillState("recover", 1, SkillAvailability.Available, 75, true, AbilityDomain.Recovery)
        ]), "test");
        await events.PublishAsync(new CombatStateChanged(true, "a human pupil"), "test");
        await events.PublishAsync(new ActionExecuted(Guid.NewGuid(), "recover", false, DecisionSource.Jev), "actions");
        await events.PublishAsync(new CommunicationObserved("say", "Michadi Fippirea", "Would you like to learn about flying?"), "test");
        await events.PublishAsync(new AbilityHelpObserved(new AbilityHelpDocument(
            "recover",
            AbilityHelpKind.Skill,
            1.0m,
            8,
            "recover",
            "Recover some health while preserving combat tempo. Hard wrapped text must become contiguous.",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["activation lag"] = "1.0 rounds",
                ["activation cost"] = "8 mana"
            },
            "raw help")), "test");

        await WaitUntilAsync(() => knowledge.Summary.AbilityHelpDocuments >= 1 &&
                                   reducer.Current.Character.Skills.Any(skill => skill.Name.Equals("recover", StringComparison.OrdinalIgnoreCase)));

        JevPersistentContext? context = await knowledge.GetCombatContextAsync(reducer.Current);
        Assert.True(context is not null, "Expected persistent Jev context for a connected session.");
        AbilityHelpKnowledge help = context!.AbilityReference.Single(reference => reference.Name.Equals("recover", StringComparison.OrdinalIgnoreCase));
        Assert.True(!help.Description.Contains('\n') && !help.Description.Contains('\r'), "Jev ability reference must contain contiguous semantic prose.");
        Assert.Equal("recover", help.Syntax);
        ExecutedCommandKnowledge command = context.RecentCommands.Single(entry => entry.Command == "recover");
        Assert.Equal("Jev", command.Source);
        CommunicationKnowledge communication = context.RecentCommunications.Single(entry => entry.Speaker == "Michadi Fippirea");
        Assert.True(communication.Message.Contains("flying", StringComparison.OrdinalIgnoreCase), "Recent room communication should be available to Jev.");

        bool requestCreated = AvendarJevRequestFactory.TryCreateCombatRequest(reducer.Current, context, out JevChoiceRequest? request);
        Assert.True(requestCreated && request is not null, "Expected Jev request to accept the enriched persistent context.");
        string requestState = JsonSerializer.Serialize(request!.State);
        Assert.True(requestState.Contains("Recover some health while preserving combat tempo", StringComparison.Ordinal), "Serialized Jev state must include stored ability help prose.");
        Assert.True(requestState.Contains("\"source\":\"Jev\"", StringComparison.OrdinalIgnoreCase), "Serialized Jev state must preserve the source of recent automated commands.");
        Assert.True(requestState.Contains("Michadi Fippirea", StringComparison.OrdinalIgnoreCase), "Serialized Jev state must include recent social context.");

        cts.Cancel();
        await IgnoreCancellation(reducerTask);
        await IgnoreCancellation(knowledgeTask);
        foreach (string path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static async Task BlankCommandDispatches()
    {
        await using EventPipeline events = new();
        ChannelReader<EventEnvelope> observer = events.SubscribeLossless();
        StateReducer reducer = new(events.StateEvents);
        FakeSender sender = new();
        JevAuthorityService authority = new(events);
        ActionProcessor actions = new(sender, reducer, authority, events);
        using CancellationTokenSource cts = new();
        Task reducerTask = reducer.RunAsync(cts.Token);
        Task actionTask = actions.RunAsync(cts.Token);

        Guid actionId = Guid.NewGuid();
        await actions.QueueAsync(new MudActionEnvelope(
            actionId,
            null,
            0,
            null,
            DecisionSource.Human,
            true,
            DateTimeOffset.UtcNow,
            new SendCommandAction(string.Empty)));

        await WaitUntilAsync(() => sender.Commands.Count == 1);
        List<IMudEvent> emitted = [];
        using CancellationTokenSource readTimeout = new(TimeSpan.FromSeconds(1));
        while (emitted.Count < 2)
        {
            EventEnvelope envelope = await observer.ReadAsync(readTimeout.Token);
            if (envelope.Payload is ActionDispatching or ActionExecuted)
            {
                emitted.Add(envelope.Payload);
            }
        }

        Assert.Equal(string.Empty, sender.Commands[0]);
        ActionDispatching dispatching = Assert.IsType<ActionDispatching>(emitted[0]);
        ActionExecuted executed = Assert.IsType<ActionExecuted>(emitted[1]);
        Assert.Equal(DecisionSource.Human, dispatching.Source);
        Assert.Equal(DecisionSource.Human, executed.Source);

        cts.Cancel();
        await IgnoreCancellation(reducerTask);
        await IgnoreCancellation(actionTask);
    }


    private static async Task SensitiveCommandIsRedacted()
    {
        await using EventPipeline events = new();
        ChannelReader<EventEnvelope> observer = events.SubscribeLossless();
        StateReducer reducer = new(events.StateEvents);
        FakeSender sender = new();
        JevAuthorityService authority = new(events);
        ActionProcessor actions = new(sender, reducer, authority, events);
        using CancellationTokenSource cts = new();
        Task reducerTask = reducer.RunAsync(cts.Token);
        Task actionTask = actions.RunAsync(cts.Token);

        const string secret = "super-secret-password";
        await actions.QueueAsync(new MudActionEnvelope(
            Guid.NewGuid(),
            null,
            0,
            null,
            DecisionSource.Human,
            true,
            DateTimeOffset.UtcNow,
            new SendCommandAction(secret, Sensitive: true)));

        await WaitUntilAsync(() => sender.Commands.Count == 1);
        List<IMudEvent> emitted = [];
        using CancellationTokenSource readTimeout = new(TimeSpan.FromSeconds(1));
        while (emitted.Count < 2)
        {
            EventEnvelope envelope = await observer.ReadAsync(readTimeout.Token);
            if (envelope.Payload is ActionDispatching or ActionExecuted)
            {
                emitted.Add(envelope.Payload);
            }
        }

        Assert.Equal(secret, sender.Commands[0]);
        ActionDispatching dispatching = Assert.IsType<ActionDispatching>(emitted[0]);
        ActionExecuted executed = Assert.IsType<ActionExecuted>(emitted[1]);
        Assert.Equal("<redacted>", dispatching.Command);
        Assert.Equal("<redacted>", executed.Command);
        Assert.True(dispatching.Sensitive && executed.Sensitive, "Sensitive marker must survive event publication.");

        cts.Cancel();
        await IgnoreCancellation(reducerTask);
        await IgnoreCancellation(actionTask);
    }

    private static async Task AutomatedStaleActionIsRejected()
    {
        await using EventPipeline events = new();
        StateReducer reducer = new(events.StateEvents);
        FakeSender sender = new();
        JevAuthorityService authority = new(events);
        await authority.SetDomainAsync(JevDomain.Combat, JevAuthority.Auto);
        using CancellationTokenSource cts = new();
        Task reducerTask = reducer.RunAsync(cts.Token);
        ActionProcessor actions = new(sender, reducer, authority, events);
        Task actionTask = actions.RunAsync(cts.Token);

        await events.PublishAsync(new CombatStateChanged(true, "guard"), "test");
        await WaitUntilAsync(() => reducer.Current.Version >= 2);

        MudActionEnvelope stale = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            0,
            JevDomain.Combat,
            DecisionSource.Jev,
            false,
            DateTimeOffset.UtcNow,
            new SendCommandAction("bash guard"));
        await actions.QueueAsync(stale);
        await Task.Delay(50);

        Assert.Equal(0, sender.Commands.Count);
        cts.Cancel();
        await IgnoreCancellation(reducerTask);
        await IgnoreCancellation(actionTask);
    }


    private static async Task RuleActionBypassesJevAuthority()
    {
        await using EventPipeline events = new();
        StateReducer reducer = new(events.StateEvents);
        FakeSender sender = new();
        JevAuthorityService authority = new(events);
        using CancellationTokenSource cts = new();
        Task reducerTask = reducer.RunAsync(cts.Token);
        ActionProcessor actions = new(sender, reducer, authority, events);
        Task actionTask = actions.RunAsync(cts.Token);

        MudActionEnvelope rule = new(
            Guid.NewGuid(),
            null,
            reducer.Current.Version,
            null,
            DecisionSource.Rules,
            true,
            DateTimeOffset.UtcNow,
            new SendCommandAction("stand"));
        await actions.QueueAsync(rule);
        await WaitUntilAsync(() => sender.Commands.Count == 1);

        Assert.Equal("stand", sender.Commands[0]);
        cts.Cancel();
        await IgnoreCancellation(reducerTask);
        await IgnoreCancellation(actionTask);
    }

    private static Task SemanticGainsUpdateKnownState()
    {
        StateSnapshot current = StateSnapshot.Initial with
        {
            Character = StateSnapshot.Initial.Character with
            {
                Experience = 1000,
                Exploration = 40,
                Profile = StateSnapshot.Initial.Character.Profile with { Level = 3 },
                HitPoints = new VitalState(100, 120),
                Mana = new VitalState(80, 100),
                Movement = new VitalState(90, 110),
                Inventory = StateSnapshot.Initial.Character.Inventory with { Copper = 10 }
            }
        };

        current = StateReducer.Reduce(current, Envelope(1, new ExperienceGained(25)));
        current = StateReducer.Reduce(current, Envelope(2, new ExplorationGained(1, 25)));
        current = StateReducer.Reduce(current, Envelope(3, new CurrencyGained("copper", 5)));
        current = StateReducer.Reduce(current, Envelope(4, new CharacterPromoted(16, 8, 16)));

        Assert.Equal(1025L, current.Character.Experience);
        Assert.Equal(41, current.Character.Exploration);
        Assert.Equal(15, current.Character.Inventory.Copper);
        Assert.Equal(4, current.Character.Profile.Level);
        Assert.Equal(136, current.Character.HitPoints.Maximum);
        Assert.Equal(108, current.Character.Mana.Maximum);
        Assert.Equal(126, current.Character.Movement.Maximum);
        Assert.Equal(100, current.Character.HitPoints.Current);
        return Task.CompletedTask;
    }

    private static Task MultiDenominationCurrencyUpdatesKnownState()
    {
        StateSnapshot current = StateSnapshot.Initial with
        {
            Character = StateSnapshot.Initial.Character with
            {
                Inventory = StateSnapshot.Initial.Character.Inventory with
                {
                    Copper = 10,
                    Silver = 2,
                    Gold = 1
                }
            }
        };

        current = StateReducer.Reduce(current, Envelope(1, new CurrencyGained("silver", 1)));
        current = StateReducer.Reduce(current, Envelope(2, new CurrencyGained("gold", 2)));

        Assert.Equal(3, current.Character.Inventory.Silver);
        Assert.Equal(3, current.Character.Inventory.Gold);
        Assert.Equal(10, current.Character.Inventory.Copper);
        return Task.CompletedTask;
    }

    private static Task QueuedMovementBuildsTopology()
    {
        StateSnapshot current = StateSnapshot.Initial;
        RoomObservationObserved firstRoom = RoomObservation(
            "avendar:room-a",
            "The Adventurer's Lounge",
            "fingerprint-a",
            "A fountain corner.",
            [new RoomExitObservation("west", true, ExitDoorState.Unknown, ExitTraversability.Traversable)]);
        current = StateReducer.Reduce(current, Envelope(1, firstRoom));
        current = StateReducer.Reduce(current, Envelope(2, new NavigationAttempted("west")));
        current = StateReducer.Reduce(current, Envelope(3, new NavigationAttempted("south")));

        RoomObservationObserved secondRoom = RoomObservation(
            "avendar:room-b",
            "The Adventurer's Lounge",
            "fingerprint-b",
            "A stairwell corner.",
            [new RoomExitObservation("east", true, ExitDoorState.Unknown, ExitTraversability.Traversable)]);
        current = StateReducer.Reduce(current, Envelope(4, secondRoom));

        Assert.Equal(1, current.World.Edges.Count);
        Assert.Equal("west", current.World.Edges[0].Direction);
        Assert.Equal("avendar:room-a", current.World.Edges[0].FromRoomId);
        Assert.Equal("avendar:room-b", current.World.Edges[0].ToRoomId);
        Assert.Equal(1, current.World.PendingDirections.Count);
        Assert.Equal("south", current.World.PendingDirections[0]);
        return Task.CompletedTask;
    }

    private static Task AvendarSemanticParserCapturesItemProvenance()
    {
        AvendarSemanticParser parser = new();
        ItemAcquired corpse = Assert.Single(parser.ParseLine("You get a brass key from the corpse of a dockhand.").OfType<ItemAcquired>());
        Assert.Equal("a brass key", corpse.ItemName);
        Assert.Equal(ItemAcquisitionSourceKind.Corpse, corpse.SourceKind);
        Assert.Equal("the corpse of a dockhand", corpse.SourceDescription);

        ItemAcquired drop = Assert.Single(parser.ParseLine("A dockhand drops a canvas pouch.").OfType<ItemAcquired>());
        Assert.Equal("a canvas pouch", drop.ItemName);
        Assert.Equal(ItemAcquisitionSourceKind.MobDrop, drop.SourceKind);
        Assert.Equal("A dockhand", drop.SourceDescription);
        return Task.CompletedTask;
    }

    private static Task CombatLootBurstPreservesCorpseProvenance()
    {
        AvendarSemanticParser parser = new();
        string[] lines =
        [
            "A human pupil is DEAD!!",
            "You receive 504 experience points.",
            "A human pupil's head is shattered, and his brains splash all over.",
            "You get a copper chainmail shirt from the corpse of a human pupil.",
            "You get some copper chainmail leggings from the corpse of a human pupil.",
            "You get a pair of hide boots from the corpse of a human pupil.",
            "You get 6 copper coins from the corpse of a human pupil.",
            "You get 1 silver coins from the corpse of a human pupil.",
            "You quickly destroy the corpse of a human pupil."
        ];

        List<IMudEvent> events = [];
        foreach (string line in lines)
        {
            events.AddRange(parser.ParseLine(line));
        }

        EnemyKilled killed = Assert.Single(events.OfType<EnemyKilled>());
        Assert.Equal("A human pupil", killed.TargetName);

        ItemAcquired[] items = events.OfType<ItemAcquired>().ToArray();
        Assert.Equal(3, items.Length);
        Assert.True(items.All(item => item.SourceKind == ItemAcquisitionSourceKind.Corpse),
            "All equipment from the corpse must retain explicit corpse provenance.");
        Assert.True(items.All(item => string.Equals(item.SourceDescription, "the corpse of a human pupil", StringComparison.OrdinalIgnoreCase)),
            "All corpse acquisitions should identify the actual corpse owner.");
        Assert.True(items.Any(item => item.ItemName == "a copper chainmail shirt"));
        Assert.True(items.Any(item => item.ItemName == "some copper chainmail leggings"));
        Assert.True(items.Any(item => item.ItemName == "a pair of hide boots"));

        CurrencyGained[] currency = events.OfType<CurrencyGained>().ToArray();
        Assert.Equal(2, currency.Length);
        Assert.True(currency.Any(value => value.Currency == "copper" && value.Amount == 6));
        Assert.True(currency.Any(value => value.Currency == "silver" && value.Amount == 1));

        CorpseDestroyed destroyed = Assert.Single(events.OfType<CorpseDestroyed>());
        Assert.Equal("a human pupil", destroyed.TargetName);
        return Task.CompletedTask;
    }

    private static Task CorpseDestructionRemovesMatchingRoomCorpse()
    {
        StateSnapshot current = StateSnapshot.Initial with
        {
            Room = StateSnapshot.Initial.Room with
            {
                Id = "avendar:hall-of-heroes",
                Name = "The Hall of Heroes",
                ContentsCompleteness = ObservationCompleteness.Complete,
                Contents =
                [
                    new RoomContentObservation(
                        "The corpse of a human pupil is lying here.",
                        Kind: RoomEntityKind.Corpse,
                        Traits: RoomEntityTraits.Corpse | RoomEntityTraits.Container | RoomEntityTraits.Lootable),
                    new RoomContentObservation(
                        "A human pupil tries unsuccessfully to adjust his chain mail.",
                        Kind: RoomEntityKind.Occupant,
                        Traits: RoomEntityTraits.Mobile)
                ]
            }
        };

        StateSnapshot next = StateReducer.Reduce(current, Envelope(1, new CorpseDestroyed("a human pupil")));
        Assert.Equal(0, next.Room.Corpses.Count);
        Assert.Equal(1, next.Room.Occupants.Count);
        Assert.Equal(ObservationCompleteness.Partial, next.Room.ContentsCompleteness);
        return Task.CompletedTask;
    }

    private static Task AutomationExpressionsSupportRichPredicates()
    {
        StateSnapshot state = StateSnapshot.Initial with
        {
            Session = StateSnapshot.Initial.Session with { ConnectionStatus = ConnectionStatus.Connected, InputMode = SessionInputMode.Normal },
            Character = StateSnapshot.Initial.Character with
            {
                HitPoints = new VitalState(25, 100),
                Effects = ["poison"],
                Inventory = new InventorySummary(2, 20, 5, 100, 12, 3, 0),
                Equipment = StateSnapshot.Initial.Character.Equipment with
                {
                    MainHand = "a copper dagger",
                    Slots = [new EquipmentSlotState("wielded", 0, "a copper dagger")]
                }
            },
            Room = StateSnapshot.Initial.Room with
            {
                Terrain = "city",
                Contents =
                [
                    new RoomContentObservation("A human pupil waits here.", "a human pupil", RoomEntityKind.Occupant, RoomEntityTraits.Mobile, ["pupil"]),
                    new RoomContentObservation("A gurgling fountain bubbles here.", "a gurgling fountain", RoomEntityKind.Fixture, RoomEntityTraits.Fixture, ["fountain"])
                ]
            }
        };
        Dictionary<string, string> variables = new(StringComparer.OrdinalIgnoreCase) { ["mode"] = "grind" };
        Assert.True(GameRuleEvaluator.Evaluate("hp.percent < 30 && (effect('poison') || occupant('pupil'))", state, variables));
        Assert.True(GameRuleEvaluator.Evaluate("var.mode == 'grind' && inventory.copper >= 10", state, variables));
        Assert.True(GameRuleEvaluator.Evaluate("!(combat.active || hp.percent > 30)", state, variables));
        Assert.True(GameRuleEvaluator.Evaluate("equipped('dagger') && equipment('wielded') == 'a copper dagger'", state, variables));
        Assert.True(GameRuleEvaluator.Evaluate("interactable('fountain')", state, variables));
        Assert.False(GameRuleEvaluator.Evaluate("exit('north') || hp.percent > 50", state, variables));
        return Task.CompletedTask;
    }

    private static async Task AutomationRulesWaitForHumanOverride()
    {
        await using EventPipeline events = new();
        StateReducer reducer = new(events.StateEvents);
        FakeSender sender = new();
        JevAuthorityService authority = new(events);
        ActionProcessor actions = new(sender, reducer, authority, events);
        AutomationPreferences preferences = new(MaxCommandsPerSecond: 20, HumanOverrideMilliseconds: 300);
        ScriptScheduler scheduler = new();
        ClientScriptCommands commands = new(actions, reducer, scheduler, () => preferences);
        using CancellationTokenSource cts = new();
        Task reducerTask = reducer.RunAsync(cts.Token);
        Task actionTask = actions.RunAsync(cts.Token);

        await events.PublishAsync(new ConnectionStateChanged(ConnectionStatus.Connected, "localhost", 4000), "test");
        await events.PublishAsync(new SessionInputModeChanged(SessionInputMode.Normal), "test");
        await WaitUntilAsync(() => reducer.Current.Session.ConnectionStatus == ConnectionStatus.Connected &&
                                  reducer.Current.Session.InputMode == SessionInputMode.Normal);

        ScriptCommandResult manual = await commands.SendAsync(new ScriptCommandRequest(
            "say manual",
            ScriptCommandOrigin.User,
            "user",
            "User",
            "test human override"));
        Assert.True(manual.Accepted);
        await WaitUntilAsync(() => sender.Commands.Count == 1);
        sender.Commands.Clear();

        Task<ScriptCommandResult> automated = commands.SendAsync(new ScriptCommandRequest(
            "look",
            ScriptCommandOrigin.Automation,
            "automation.test",
            "Automation test",
            "human override parity"));
        await Task.Delay(100);
        Assert.Equal(0, sender.Commands.Count);
        ScriptCommandResult result = await automated;
        Assert.True(result.Accepted);
        await WaitUntilAsync(() => sender.Commands.Count == 1);
        Assert.Equal("look", sender.Commands[0]);

        cts.Cancel();
        await IgnoreCancellation(reducerTask);
        await IgnoreCancellation(actionTask);
    }

    private static async Task AutomationWorkflowSettingsRoundTrip()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nexmud-workflow-settings", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        try
        {
            ClientSettingsStore store = new(path);
            ClientSettings settings = ClientSettings.Default with
            {
                Workflows = [new AutomationWorkflow(
                    "recover after kill",
                    "set last=${combat.target}\nwait !combat.active timeout=5000\njev recovery",
                    TriggerEvent: nameof(EnemyKilled),
                    CooldownMilliseconds: 250,
                    FailureMode: AutomationWorkflowFailureMode.Continue)],
                Automation = new AutomationPreferences(HumanOverrideMilliseconds: 2200, MaxConcurrentWorkflows: 3, PersistVariables: true)
            };
            await store.SaveAsync(settings);
            ClientSettings loaded = await store.LoadAsync();
            AutomationWorkflow workflow = Assert.Single(loaded.Workflows!);
            Assert.Equal("recover after kill", workflow.Name);
            Assert.Equal(nameof(EnemyKilled), workflow.TriggerEvent);
            Assert.Equal(2200, loaded.Automation!.HumanOverrideMilliseconds);
            Assert.Equal(3, loaded.Automation.MaxConcurrentWorkflows);
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { }
        }
    }

    private static async Task AutomationWorkflowDispatchesWithoutDeadlock()
    {
        string statePath = Path.Combine(Path.GetTempPath(), $"nexmud-automation-state-{Guid.NewGuid():N}.json");
        await using EventPipeline events = new();
        StateReducer reducer = new(events.StateEvents);
        FakeSender sender = new();
        JevAuthorityService authority = new(events);
        ActionProcessor actions = new(sender, reducer, authority, events);
        ClientSettings settings = ClientSettings.Default with
        {
            Workflows = [new AutomationWorkflow(
                "manual smoke",
                "set mode=test\nsend look\nwait connected timeout=1000\nunset mode")],
            Automation = new AutomationPreferences(MaxCommandsPerSecond: 20, HumanOverrideMilliseconds: 0)
        };
        await using ScriptExecutionSupervisor execution = new();
        ScriptScheduler scheduler = new();
        ClientScriptCommands commands = new(actions, reducer, scheduler, () => settings.Automation ?? new AutomationPreferences());
        ClientAutomationService automation = new(
            events.SubscribeLossless(),
            reducer,
            commands,
            scheduler,
            execution,
            events,
            () => settings,
            stateStore: new AutomationStateStore(statePath));
        using CancellationTokenSource cts = new();
        Task reducerTask = reducer.RunAsync(cts.Token);
        Task actionTask = actions.RunAsync(cts.Token);
        Task automationTask = automation.RunAsync(cts.Token);

        await events.PublishAsync(new ConnectionStateChanged(ConnectionStatus.Connected, "localhost", 4000), "test");
        await events.PublishAsync(new SessionInputModeChanged(SessionInputMode.Normal), "test");
        await WaitUntilAsync(() => reducer.Current.Session.ConnectionStatus == ConnectionStatus.Connected &&
                                  reducer.Current.Session.InputMode == SessionInputMode.Normal);

        Assert.True(await automation.RunWorkflowAsync("manual smoke"), "Manual workflow should start without an automatic trigger.");
        await WaitUntilAsync(() => sender.Commands.Count == 1 && automation.ActiveWorkflowNames.Count == 0);
        Assert.Equal("look", sender.Commands[0]);
        Assert.False(automation.Variables.ContainsKey("mode"), "Workflow unset step should remove its persistent variable.");

        cts.Cancel();
        await IgnoreCancellation(reducerTask);
        await IgnoreCancellation(actionTask);
        await IgnoreCancellation(automationTask);
        try { File.Delete(statePath); } catch { }
        try { File.Delete(statePath + ".tmp"); } catch { }
    }

    private static async Task AutomationWorkflowRetriesAreBounded()
    {
        string statePath = Path.Combine(Path.GetTempPath(), $"nexmud-automation-retry-state-{Guid.NewGuid():N}.json");
        await using EventPipeline events = new();
        ChannelReader<EventEnvelope> observer = events.SubscribeLossless();
        StateReducer reducer = new(events.StateEvents);
        FakeSender sender = new();
        JevAuthorityService authority = new(events);
        ActionProcessor actions = new(sender, reducer, authority, events);
        ClientSettings settings = ClientSettings.Default with
        {
            Workflows = [new AutomationWorkflow("bounded retry", "retry 2 delay=1 :: assert hp > 999999")],
            Automation = new AutomationPreferences(MaxCommandsPerSecond: 20, HumanOverrideMilliseconds: 0)
        };
        await using ScriptExecutionSupervisor execution = new();
        ScriptScheduler scheduler = new();
        ClientScriptCommands commands = new(actions, reducer, scheduler, () => settings.Automation ?? new AutomationPreferences());
        ClientAutomationService automation = new(
            events.SubscribeLossless(),
            reducer,
            commands,
            scheduler,
            execution,
            events,
            () => settings,
            stateStore: new AutomationStateStore(statePath));
        using CancellationTokenSource cts = new();
        Task reducerTask = reducer.RunAsync(cts.Token);
        Task actionTask = actions.RunAsync(cts.Token);
        Task automationTask = automation.RunAsync(cts.Token);

        await events.PublishAsync(new ConnectionStateChanged(ConnectionStatus.Connected, "localhost", 4000), "test");
        await events.PublishAsync(new SessionInputModeChanged(SessionInputMode.Normal), "test");
        await WaitUntilAsync(() => reducer.Current.Session.ConnectionStatus == ConnectionStatus.Connected &&
                                  reducer.Current.Session.InputMode == SessionInputMode.Normal);
        Assert.True(await automation.RunWorkflowAsync("bounded retry"));

        List<AutomationWorkflowStateChanged> states = [];
        using CancellationTokenSource readTimeout = new(TimeSpan.FromSeconds(2));
        while (!states.Any(state => state.Status == AutomationWorkflowStatus.Failed))
        {
            EventEnvelope envelope = await observer.ReadAsync(readTimeout.Token);
            if (envelope.Payload is AutomationWorkflowStateChanged state &&
                state.WorkflowName.Equals("bounded retry", StringComparison.OrdinalIgnoreCase))
                states.Add(state);
        }

        Assert.Equal(2, states.Count(state => state.Status == AutomationWorkflowStatus.Waiting &&
                                             state.Detail?.StartsWith("Retry ", StringComparison.Ordinal) == true));
        Assert.True(states.Any(state => state.Status == AutomationWorkflowStatus.Failed),
            "Retry exhaustion must fail the workflow instead of retrying forever.");

        cts.Cancel();
        await IgnoreCancellation(reducerTask);
        await IgnoreCancellation(actionTask);
        await IgnoreCancellation(automationTask);
        try { File.Delete(statePath); } catch { }
        try { File.Delete(statePath + ".tmp"); } catch { }
    }

    private static async Task AutomationWorkflowConcurrencyIsEnforced()
    {
        string statePath = Path.Combine(Path.GetTempPath(), $"nexmud-automation-concurrency-state-{Guid.NewGuid():N}.json");
        await using EventPipeline events = new();
        StateReducer reducer = new(events.StateEvents);
        FakeSender sender = new();
        JevAuthorityService authority = new(events);
        ActionProcessor actions = new(sender, reducer, authority, events);
        ClientSettings settings = ClientSettings.Default with
        {
            Workflows =
            [
                new AutomationWorkflow("first", "delay 500"),
                new AutomationWorkflow("second", "send look")
            ],
            Automation = new AutomationPreferences(MaxCommandsPerSecond: 20, HumanOverrideMilliseconds: 0, MaxConcurrentWorkflows: 1)
        };
        await using ScriptExecutionSupervisor execution = new();
        ScriptScheduler scheduler = new();
        ClientScriptCommands commands = new(actions, reducer, scheduler, () => settings.Automation ?? new AutomationPreferences());
        ClientAutomationService automation = new(
            events.SubscribeLossless(), reducer, commands, scheduler, execution, events, () => settings,
            stateStore: new AutomationStateStore(statePath));
        using CancellationTokenSource cts = new();
        Task reducerTask = reducer.RunAsync(cts.Token);
        Task actionTask = actions.RunAsync(cts.Token);
        Task automationTask = automation.RunAsync(cts.Token);

        await events.PublishAsync(new ConnectionStateChanged(ConnectionStatus.Connected, "localhost", 4000), "test");
        await events.PublishAsync(new SessionInputModeChanged(SessionInputMode.Normal), "test");
        await WaitUntilAsync(() => reducer.Current.Session.ConnectionStatus == ConnectionStatus.Connected &&
                                  reducer.Current.Session.InputMode == SessionInputMode.Normal);

        Assert.True(await automation.RunWorkflowAsync("first"));
        Assert.False(await automation.RunWorkflowAsync("second"),
            "Manual workflow starts must honor MaxConcurrentWorkflows.");
        await WaitUntilAsync(() => automation.ActiveWorkflowNames.Count == 0);
        Assert.True(await automation.RunWorkflowAsync("second"),
            "A workflow should start after the previous execution releases its slot.");
        await WaitUntilAsync(() => sender.Commands.Count == 1 && automation.ActiveWorkflowNames.Count == 0);

        cts.Cancel();
        await IgnoreCancellation(reducerTask);
        await IgnoreCancellation(actionTask);
        await IgnoreCancellation(automationTask);
        try { File.Delete(statePath); } catch { }
        try { File.Delete(statePath + ".tmp"); } catch { }
    }

    private static async Task AutomationWorkflowContinueModeCompletes()
    {
        string statePath = Path.Combine(Path.GetTempPath(), $"nexmud-automation-continue-state-{Guid.NewGuid():N}.json");
        await using EventPipeline events = new();
        StateReducer reducer = new(events.StateEvents);
        FakeSender sender = new();
        JevAuthorityService authority = new(events);
        ActionProcessor actions = new(sender, reducer, authority, events);
        ClientSettings settings = ClientSettings.Default with
        {
            Workflows = [new AutomationWorkflow(
                "continue after failure",
                "assert hp > 999999\nset mode=continued",
                FailureMode: AutomationWorkflowFailureMode.Continue)],
            Automation = new AutomationPreferences(MaxCommandsPerSecond: 20, HumanOverrideMilliseconds: 0)
        };
        ChannelReader<EventEnvelope> observer = events.SubscribeLossless();
        await using ScriptExecutionSupervisor execution = new();
        ScriptScheduler scheduler = new();
        ClientScriptCommands commands = new(actions, reducer, scheduler, () => settings.Automation ?? new AutomationPreferences());
        ClientAutomationService automation = new(
            events.SubscribeLossless(), reducer, commands, scheduler, execution, events, () => settings,
            stateStore: new AutomationStateStore(statePath));
        using CancellationTokenSource cts = new();
        Task reducerTask = reducer.RunAsync(cts.Token);
        Task actionTask = actions.RunAsync(cts.Token);
        Task automationTask = automation.RunAsync(cts.Token);

        await events.PublishAsync(new ConnectionStateChanged(ConnectionStatus.Connected, "localhost", 4000), "test");
        await events.PublishAsync(new SessionInputModeChanged(SessionInputMode.Normal), "test");
        await WaitUntilAsync(() => reducer.Current.Session.ConnectionStatus == ConnectionStatus.Connected &&
                                  reducer.Current.Session.InputMode == SessionInputMode.Normal);

        Assert.True(await automation.RunWorkflowAsync("continue after failure"));
        List<AutomationWorkflowStateChanged> states = [];
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
        while (!states.Any(state => state.Status == AutomationWorkflowStatus.Completed))
        {
            EventEnvelope envelope = await observer.ReadAsync(timeout.Token);
            if (envelope.Payload is AutomationWorkflowStateChanged state &&
                state.WorkflowName.Equals("continue after failure", StringComparison.OrdinalIgnoreCase))
                states.Add(state);
        }

        Assert.False(states.Any(state => state.Status == AutomationWorkflowStatus.Failed),
            "Continue mode should not publish a terminal Failed state before later completing.");
        Assert.True(states.Any(state => state.Status == AutomationWorkflowStatus.Running &&
                                       state.Detail?.Contains("continuing", StringComparison.OrdinalIgnoreCase) == true),
            "Continue mode should make the recovered step failure observable.");
        Assert.Equal("continued", automation.Variables["mode"]);

        cts.Cancel();
        await IgnoreCancellation(reducerTask);
        await IgnoreCancellation(actionTask);
        await IgnoreCancellation(automationTask);
        try { File.Delete(statePath); } catch { }
        try { File.Delete(statePath + ".tmp"); } catch { }
    }

    private static async Task AutomationWorkflowUsesEventContextAndNavigation()
    {
        string statePath = Path.Combine(Path.GetTempPath(), $"nexmud-automation-event-state-{Guid.NewGuid():N}.json");
        await using EventPipeline events = new();
        StateReducer reducer = new(events.StateEvents);
        FakeSender sender = new();
        JevAuthorityService authority = new(events);
        ActionProcessor actions = new(sender, reducer, authority, events);
        string? navigated = null;
        ClientSettings settings = ClientSettings.Default with
        {
            Workflows = [new AutomationWorkflow(
                "loot source navigation",
                "set killed=${event.target}\nif event.target == 'a rat' :: set matched=yes\nnavigate ${event.target}",
                TriggerEvent: nameof(EnemyKilled),
                CooldownMilliseconds: 0)],
            Automation = new AutomationPreferences(MaxCommandsPerSecond: 20, HumanOverrideMilliseconds: 0)
        };
        await using ScriptExecutionSupervisor execution = new();
        ScriptScheduler scheduler = new();
        ClientScriptCommands commands = new(actions, reducer, scheduler, () => settings.Automation ?? new AutomationPreferences());
        ClientAutomationService automation = new(
            events.SubscribeLossless(),
            reducer,
            commands,
            scheduler,
            execution,
            events,
            () => settings,
            navigate: (query, _) =>
            {
                navigated = query;
                return Task.FromResult(true);
            },
            stateStore: new AutomationStateStore(statePath));
        using CancellationTokenSource cts = new();
        Task reducerTask = reducer.RunAsync(cts.Token);
        Task actionTask = actions.RunAsync(cts.Token);
        Task automationTask = automation.RunAsync(cts.Token);

        await events.PublishAsync(new ConnectionStateChanged(ConnectionStatus.Connected, "localhost", 4000), "test");
        await events.PublishAsync(new SessionInputModeChanged(SessionInputMode.Normal), "test");
        await WaitUntilAsync(() => reducer.Current.Session.ConnectionStatus == ConnectionStatus.Connected &&
                                  reducer.Current.Session.InputMode == SessionInputMode.Normal);
        await events.PublishAsync(new EnemyKilled("a rat"), "test");

        await WaitUntilAsync(() => automation.Variables.TryGetValue("killed", out string? killed) && killed == "a rat" &&
                                  automation.ActiveWorkflowNames.Count == 0 && navigated == "a rat");
        Assert.Equal("yes", automation.Variables["matched"]);
        Assert.Equal("a rat", navigated);

        cts.Cancel();
        await IgnoreCancellation(reducerTask);
        await IgnoreCancellation(actionTask);
        await IgnoreCancellation(automationTask);
        try { File.Delete(statePath); } catch { }
        try { File.Delete(statePath + ".tmp"); } catch { }
    }

    private static async Task MapperBrowseSearchAndGraph()
    {
        string databasePath = Path.Combine(Path.GetTempPath(), $"nexmud-map-test-{Guid.NewGuid():N}.db");
        await using EventPipeline events = new();
        StateReducer reducer = new(events.StateEvents);
        WorldKnowledgeStore knowledge = new(events.SubscribeLossless(), reducer, databasePath);
        using CancellationTokenSource cts = new();
        Task reducerTask = reducer.RunAsync(cts.Token);
        Task knowledgeTask = knowledge.RunAsync(cts.Token);

        RoomObservationObserved square = new(
            "avendar:square",
            "Market Square",
            "square-fingerprint",
            "A busy market square.",
            [new RoomExitObservation("east", true, ExitDoorState.Unknown, ExitTraversability.Traversable)],
            [
                new RoomContentObservation(
                    "An oversized rat noses around a discarded sack.",
                    "an oversized rat",
                    RoomEntityKind.Occupant,
                    RoomEntityTraits.Mobile,
                    ["rat"]),
                new RoomContentObservation(
                    "A gurgling fountain bubbles here.",
                    Kind: RoomEntityKind.Fixture,
                    Traits: RoomEntityTraits.Fixture | RoomEntityTraits.Examinable | RoomEntityTraits.Drinkable,
                    TargetKeywords: ["fountain"])
            ],
            ObservationCompleteness.Complete);
        RoomObservationObserved alley = new(
            "avendar:alley",
            "Eastern Alley",
            "alley-fingerprint",
            "A narrow alley runs east and west.",
            [
                new RoomExitObservation("west", true, ExitDoorState.Unknown, ExitTraversability.Traversable),
                new RoomExitObservation("east", true, ExitDoorState.Unknown, ExitTraversability.Traversable)
            ],
            Array.Empty<RoomContentObservation>(),
            ObservationCompleteness.Complete);
        RoomObservationObserved dock = new(
            "avendar:dock",
            "Eastern Dock",
            "dock-fingerprint",
            "A weathered dock reaches over dark water.",
            [new RoomExitObservation("west", true, ExitDoorState.Unknown, ExitTraversability.Traversable)],
            Array.Empty<RoomContentObservation>(),
            ObservationCompleteness.Complete);

        await events.PublishAsync(square, "test");
        await events.PublishAsync(new NavigationAttempted("east"), "test");
        await events.PublishAsync(new NavigationAttempted("east"), "test");
        await events.PublishAsync(alley, "test");
        await events.PublishAsync(dock, "test");
        await WaitUntilAsync(() => knowledge.Summary.Rooms >= 3);

        IReadOnlyList<MapperDestinationSearchResult> destinations = await knowledge.FindMapDestinationsAsync("rat");
        MapperDestinationSearchResult rat = Assert.Single(destinations.Where(result => result.Kind == MapperDestinationKind.Entity));
        Assert.Equal("avendar:square", rat.RoomId);
        Assert.False((await knowledge.FindMapDestinationsAsync("gurgling fountain"))
            .Any(result => result.Kind == MapperDestinationKind.Entity),
            "Fixtures must not be exposed as MOB destinations.");
        Assert.True((await knowledge.FindMapDestinationsAsync("gurgling fountain"))
            .Any(result => result.Kind == MapperDestinationKind.Fixture && result.RoomId == "avendar:square"),
            "Fixtures should remain valid mapper destinations without being misclassified as MOBs.");
        Assert.Equal(0, (await knowledge.SearchCodexAsync(CodexEntryKind.Entity, "gurgling fountain"))
            .Count,
            "Fixtures must not be exposed in the MOB Codex category.");

        MapperGraphSnapshot? graph = await knowledge.GetMapGraphAsync("avendar:square", 8, 120);
        Assert.True(graph is not null, "Expected a graph rooted at the observed room.");
        Assert.True(graph!.Rooms.Any(room => room.RoomId == "avendar:square"), "Graph should contain its origin room.");
        Assert.True(graph.Rooms.Any(room => room.RoomId == "avendar:alley"), "Graph should contain the first observed destination room.");
        Assert.True(graph.Rooms.Any(room => room.RoomId == "avendar:dock"), "Graph should contain the second queued destination room.");
        Assert.True(graph.Edges.Any(edge => edge.FromRoomId == "avendar:square" && edge.ToRoomId == "avendar:alley" && edge.Direction == "east"),
            "Graph should expose the first learned east traversal.");
        Assert.True(graph.Edges.Any(edge => edge.FromRoomId == "avendar:alley" && edge.ToRoomId == "avendar:dock" && edge.Direction == "east"),
            "Queued movement must preserve the second learned east traversal.");

        // A malformed/custom client can accumulate many distinct movement commands between
        // the same rooms. Mapper projection must remain bounded even when room cardinality is low.
        for (int index = 0; index < 180; index++)
        {
            await knowledge.SaveMapExitAsync("avendar:dock", $"portal-{index:N3}", "avendar:square");
        }
        MapperGraphSnapshot? boundedGraph = await knowledge.GetMapGraphAsync("avendar:dock", 8, 10);
        Assert.True(boundedGraph is not null, "Expected bounded graph projection for a known room.");
        Assert.True(boundedGraph!.Edges.Count <= 120,
            "Mapper projection must enforce its edge budget even with high-cardinality custom exits.");

        KnowledgeRoute? route = await knowledge.FindRouteAsync("avendar:square", "avendar:dock");
        Assert.True(route is not null && route.Steps.Count == 2, "Expected a two-step route over queued movement topology.");
        Assert.Equal("east", route!.Steps[0].Direction);
        Assert.Equal("east", route.Steps[1].Direction);

        MapperReadRepository mapperReader = new(databasePath);
        MapperGraphSnapshot? isolatedGraph = await mapperReader.LoadNeighborhoodAsync("avendar:square", 8, 120);
        Assert.True(isolatedGraph is not null && isolatedGraph.Rooms.Count >= 3,
            "Read-only mapper repository should load the observed neighborhood independently from the knowledge writer.");
        IReadOnlyList<MapperDestinationSearchResult> isolatedSearch = await mapperReader.SearchAsync("rat", 20);
        Assert.True(isolatedSearch.Any(result => result.Kind == MapperDestinationKind.Entity && result.RoomId == "avendar:square"),
            "Read-only mapper search should resolve remembered MOB locations.");
        KnowledgeRoute? isolatedRoute = await mapperReader.FindRouteAsync("avendar:square", "avendar:dock", 250, true, true);
        Assert.True(isolatedRoute is not null && isolatedRoute.Steps.Count == 2,
            "Read-only mapper route planning should preserve known topology.");

        await knowledge.SaveRoomMetadataAsync(new MapperRoomMetadata("avendar:alley", null, "Unsafe Quarter", null, false));
        RoutePlanningOptions constrainedOptions = new(
            250,
            AvoidBlockedExits: true,
            AllowUnknownTraversability: true,
            AvoidClosedDoors: true,
            PreferKnownTraversableExits: true,
            AvoidAreas: new HashSet<string>(new[] { "Unsafe Quarter" }, StringComparer.OrdinalIgnoreCase),
            AvoidTerrains: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            AvoidMobNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        KnowledgeRoute? constrainedRoute = await mapperReader.FindRouteAsync("avendar:square", "avendar:dock", constrainedOptions);
        Assert.True(constrainedRoute is null,
            "Route constraints should exclude avoided areas when they are the only known path.");

        cts.Cancel();
        await IgnoreCancellation(reducerTask);
        await IgnoreCancellation(knowledgeTask);
        foreach (string path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static async Task MapperNeighborhoodFollowsIncomingEdges()
    {
        string databasePath = Path.Combine(Path.GetTempPath(), $"nexmud-map-incoming-test-{Guid.NewGuid():N}.db");
        await using EventPipeline events = new();
        StateReducer reducer = new(events.StateEvents);
        WorldKnowledgeStore knowledge = new(events.SubscribeLossless(), reducer, databasePath);
        using CancellationTokenSource cts = new();
        Task reducerTask = reducer.RunAsync(cts.Token);
        Task knowledgeTask = knowledge.RunAsync(cts.Token);

        RoomObservationObserved parent = new(
            "map:parent", "Parent Room", "parent-fingerprint", "The parent room.",
            [new RoomExitObservation("north", true, ExitDoorState.Unknown, ExitTraversability.Traversable)],
            Array.Empty<RoomContentObservation>(), ObservationCompleteness.Complete);
        RoomObservationObserved fresh = new(
            "map:fresh", "Fresh Room", "fresh-fingerprint", "A newly discovered room.",
            [new RoomExitObservation("south", true, ExitDoorState.Unknown, ExitTraversability.Traversable)],
            Array.Empty<RoomContentObservation>(), ObservationCompleteness.Complete);

        await events.PublishAsync(parent, "test");
        await events.PublishAsync(new NavigationAttempted("north"), "test");
        await events.PublishAsync(fresh, "test");
        await WaitUntilAsync(() => knowledge.Summary.Rooms >= 2);

        MapperReadRepository repository = new(databasePath);
        MapperGraphSnapshot? graph = await repository.LoadNeighborhoodAsync("map:fresh", 4, 20);
        Assert.True(graph is not null, "Fresh-room graph should be available.");
        Assert.True(graph!.Rooms.Any(room => room.RoomId == "map:parent"),
            "A fresh room must retain the room which points into it even before a reverse destination has been learned.");
        Assert.True(graph.Edges.Any(edge => edge.FromRoomId == "map:parent" && edge.ToRoomId == "map:fresh" && edge.Direction == "north"),
            "Incoming learned traversal should be present when the destination is the graph origin.");

        cts.Cancel();
        await IgnoreCancellation(reducerTask);
        await IgnoreCancellation(knowledgeTask);
        foreach (string path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static Task MapperTopologyPreservesDirectionalCoordinates()
    {
        MapperGraphSnapshot graph = new(
            "a",
            [
                new MapperGraphRoom("a", "A", null, null, false, 1, null),
                new MapperGraphRoom("b", "B", null, null, false, 1, null),
                new MapperGraphRoom("c", "C", null, null, false, 1, null),
                new MapperGraphRoom("u", "Up", null, null, false, 1, null)
            ],
            [
                new MapperGraphEdge("a", "north", "b", ExitTraversability.Traversable.ToString()),
                new MapperGraphEdge("a", "east", "c", ExitTraversability.Traversable.ToString()),
                new MapperGraphEdge("a", "up", "u", ExitTraversability.Traversable.ToString())
            ]);

        MapperTopologySnapshot layout = MapperTopologyLayout.Build(graph);
        MapperCoordinate a = layout.Rooms["a"].Coordinate;
        MapperCoordinate b = layout.Rooms["b"].Coordinate;
        MapperCoordinate c = layout.Rooms["c"].Coordinate;
        MapperCoordinate u = layout.Rooms["u"].Coordinate;
        Assert.True(b.Y < a.Y, "A north exit should place its destination visually above its source.");
        Assert.True(c.X > a.X, "An east exit should place its destination visually right of its source.");
        Assert.True(u != a, "An up exit should be rendered as a separate portal destination, not stacked on its source.");
        Assert.Equal(0, u.Level);
        Assert.Equal(0, layout.ConstraintConflictCount);
        Assert.Equal(0, layout.CoordinateCollisionCount);
        return Task.CompletedTask;
    }

    private static Task MapperTopologyToleratesFoldedGeometry()
    {
        MapperGraphSnapshot graph = new(
            "a",
            [
                new MapperGraphRoom("a", "A", null, null, false, 1, null),
                new MapperGraphRoom("n1", "North Hall 1", null, null, false, 1, null),
                new MapperGraphRoom("n2", "North Hall 2", null, null, false, 1, null),
                new MapperGraphRoom("east", "East Wing", null, null, false, 1, null),
                new MapperGraphRoom("fold", "Folded Junction", null, null, false, 1, null)
            ],
            [
                new MapperGraphEdge("a", "north", "n1", ExitTraversability.Traversable.ToString()),
                new MapperGraphEdge("n1", "north", "n2", ExitTraversability.Traversable.ToString()),
                new MapperGraphEdge("a", "east", "east", ExitTraversability.Traversable.ToString()),
                new MapperGraphEdge("east", "north", "fold", ExitTraversability.Traversable.ToString()),
                new MapperGraphEdge("n2", "east", "fold", ExitTraversability.Traversable.ToString())
            ]);

        MapperTopologySnapshot layout = MapperTopologyLayout.Build(graph);
        Assert.Equal(5, layout.Rooms.Count);
        Assert.Equal(5, layout.Rooms.Values.Select(room => room.Coordinate).Distinct().Count());
        Assert.Equal(0, layout.ConstraintConflictCount);
        Assert.Equal(0, layout.CoordinateCollisionCount);
        Assert.False(layout.Edges.Any(edge => edge.ConstraintConflict),
            "Folded MUD geometry is valid topology and must not be labeled as an error.");
        return Task.CompletedTask;
    }

    private static async Task MapperRelocationDoesNotCreateExit()
    {
        string databasePath = Path.Combine(Path.GetTempPath(), $"nexmud-map-relocation-test-{Guid.NewGuid():N}.db");
        await using EventPipeline events = new();
        StateReducer reducer = new(events.StateEvents);
        WorldKnowledgeStore knowledge = new(events.SubscribeLossless(), reducer, databasePath);
        using CancellationTokenSource cts = new();
        Task reducerTask = reducer.RunAsync(cts.Token);
        Task knowledgeTask = knowledge.RunAsync(cts.Token);

        RoomObservationObserved source = new(
            "relocation:source", "Source Room", "relocation-source", "A source room.",
            [new RoomExitObservation("north", true, ExitDoorState.Unknown, ExitTraversability.Traversable)],
            Array.Empty<RoomContentObservation>(), ObservationCompleteness.Complete);
        RoomObservationObserved destination = new(
            "relocation:destination", "Recall Destination", "relocation-destination", "A recall destination.",
            Array.Empty<RoomExitObservation>(), Array.Empty<RoomContentObservation>(), ObservationCompleteness.Complete);

        await events.PublishAsync(source, "test");
        await events.PublishAsync(destination, "test");
        await WaitUntilAsync(() => knowledge.Summary.Rooms >= 2);

        Assert.True(await knowledge.FindRouteAsync("relocation:source", "relocation:destination") is null,
            "A room change without a directional traversal must not become a routable exit.");
        RoomKnowledge? rememberedSource = await knowledge.GetRoomAsync("relocation:source");
        Assert.False(rememberedSource!.Exits.Any(exit => exit.ToRoomId == "relocation:destination"),
            "Recall/teleport/summon-style relocation must remain separate from room exits.");

        cts.Cancel();
        await IgnoreCancellation(reducerTask);
        await IgnoreCancellation(knowledgeTask);
        foreach (string path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static async Task CodexAggregatesMobsByAreaAndTracksLootSources()
    {
        string databasePath = Path.Combine(Path.GetTempPath(), $"nexmud-codex-area-test-{Guid.NewGuid():N}.db");
        await using EventPipeline events = new();
        StateReducer reducer = new(events.StateEvents);
        WorldKnowledgeStore knowledge = new(events.SubscribeLossless(), reducer, databasePath);
        using CancellationTokenSource cts = new();
        Task reducerTask = reducer.RunAsync(cts.Token);
        Task knowledgeTask = knowledge.RunAsync(cts.Token);

        static RoomObservationObserved DockRoom(string id, string name) => new(
            id,
            name,
            id + "-fingerprint",
            "A working dock.",
            Array.Empty<RoomExitObservation>(),
            [new RoomContentObservation(
                "a dockhand",
                "a dockhand",
                RoomEntityKind.Occupant,
                RoomEntityTraits.Mobile | RoomEntityTraits.Examinable,
                ["dockhand"])],
            ObservationCompleteness.Complete);

        await events.PublishAsync(DockRoom("avendar:vb-1", "Var Bandor Pier"), "test");
        await events.PublishAsync(DockRoom("avendar:vb-2", "Var Bandor Wharf"), "test");
        await events.PublishAsync(DockRoom("avendar:ea-1", "Earendam Pier"), "test");
        await WaitUntilAsync(() => knowledge.Summary.Rooms >= 3);

        await knowledge.SaveRoomMetadataAsync(new MapperRoomMetadata("avendar:vb-1", null, "Var Bandor Docks", null, false));
        await knowledge.SaveRoomMetadataAsync(new MapperRoomMetadata("avendar:vb-2", null, "Var Bandor Docks", null, false));
        await knowledge.SaveRoomMetadataAsync(new MapperRoomMetadata("avendar:ea-1", null, "Earendam Docks", null, false));

        IReadOnlyList<CodexEntrySummary> mobs = await knowledge.SearchCodexAsync(CodexEntryKind.Entity, "dockhand", 20);
        Assert.Equal(2, mobs.Count);
        CodexEntrySummary varBandor = mobs.Single(entry => entry.Subtitle?.Contains("Var Bandor Docks", StringComparison.Ordinal) == true);
        CodexEntrySummary earendam = mobs.Single(entry => entry.Subtitle?.Contains("Earendam Docks", StringComparison.Ordinal) == true);
        Assert.Equal("a dockhand", varBandor.Title);
        Assert.Equal("a dockhand", earendam.Title);

        CodexEntryDetail? varBandorDetail = await knowledge.GetCodexEntryAsync(CodexEntryKind.Entity, varBandor.Key);
        Assert.True(varBandorDetail is not null, "Expected area-scoped MOB detail.");
        Assert.Equal(2, varBandorDetail!.Locations.Count);
        Assert.True(varBandorDetail.Locations.All(location => location.Area == "Var Bandor Docks"),
            "Area-scoped MOB detail must not include same-named MOBs from another area.");

        await events.PublishAsync(DockRoom("avendar:vb-1", "Var Bandor Pier"), "test");
        await events.PublishAsync(new EnemyKilled("a dockhand"), "test");
        await events.PublishAsync(new ItemAcquired(
            "a brass key",
            "the corpse of a dockhand",
            ItemAcquisitionSourceKind.Corpse,
            "You get a brass key from the corpse of a dockhand."), "test");
        await WaitUntilAsync(() => knowledge.Summary.Items >= 1);

        CodexEntryDetail? item = await knowledge.GetCodexEntryAsync(CodexEntryKind.Item, "a brass key");
        Assert.True(item is not null, "Looted items should be discoverable in the Codex before identification.");
        CodexLocation source = Assert.Single(item!.Locations);
        Assert.Equal("a dockhand", source.EntityName);
        Assert.Equal("Var Bandor Docks", source.Area);
        Assert.Equal("avendar:vb-1", source.RoomId);

        varBandorDetail = await knowledge.GetCodexEntryAsync(CodexEntryKind.Entity, varBandor.Key);
        Assert.True(varBandorDetail!.RelatedItems.Any(related => related.ItemName == "a brass key"),
            "MOB detail should expose observed loot provenance.");

        cts.Cancel();
        await IgnoreCancellation(reducerTask);
        await IgnoreCancellation(knowledgeTask);
        foreach (string path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static Task SkillImprovementMarksSkillStale()
    {
        StateSnapshot current = StateSnapshot.Initial with
        {
            Character = StateSnapshot.Initial.Character with
            {
                Skills = [new SkillState("whip", 1, SkillAvailability.Available, 75)]
            }
        };

        StateSnapshot next = StateReducer.Reduce(current, Envelope(1, new SkillImproved("whip", 76)));
        Assert.False(next.Character.Skills[0].IsFresh, "Incremental improvement without a percentage should mark proficiency stale.");
        return Task.CompletedTask;
    }

    private static Task SessionParserRecognizesLoginModes()
    {
        SessionInputModeChanged? name = AvendarSessionParser.ParseLine("Under what name shall your deeds be recorded?");
        SessionInputModeChanged? password = AvendarSessionParser.ParseLine("Password: ");
        SessionInputModeChanged? pager = AvendarSessionParser.ParseLine("[Hit Return to continue]");

        Assert.Equal(SessionInputMode.LoginName, name!.Mode);
        Assert.Equal(SessionInputMode.LoginPassword, password!.Mode);
        Assert.Equal(SessionInputMode.Pager, pager!.Mode);
        return Task.CompletedTask;
    }


    private static Task CommandInputModeTransitionsAreSafe()
    {
        CommandInputPresentation login = CommandInputPolicy.For(SessionInputMode.LoginName);
        Assert.False(login.Sensitive, "Login-name mode must not be masked.");
        Assert.Equal('\0', login.PasswordChar);
        Assert.Equal("Character name", login.Placeholder);
        Assert.False(login.AllowHistory, "Login-name mode must disable command history.");
        Assert.False(login.AllowCompletion, "Login-name mode must disable completion.");
        Assert.True(login.ClearAfterSubmit, "Login-name input must clear after submission.");

        CommandInputPresentation password = CommandInputPolicy.For(SessionInputMode.LoginPassword);
        Assert.True(password.Sensitive, "Password mode must be sensitive.");
        Assert.Equal('●', password.PasswordChar);
        Assert.Equal("Password", password.Placeholder);
        Assert.False(password.AllowHistory, "Password mode must disable history.");
        Assert.False(password.AllowCompletion, "Password mode must disable completion.");
        Assert.True(password.ClearAfterSubmit, "Password input must clear immediately after submission.");

        CommandInputPresentation normal = CommandInputPolicy.For(SessionInputMode.Normal);
        Assert.False(normal.Sensitive, "Normal mode must not remain sensitive after login.");
        Assert.Equal('\0', normal.PasswordChar);
        Assert.Equal("Enter a MUD command", normal.Placeholder);
        Assert.True(normal.AllowHistory, "Normal mode must restore command history.");
        Assert.True(normal.AllowCompletion, "Normal mode must restore completion.");

        CommandInputTransitionPresentation loginToPassword = CommandInputPolicy.Transition(
            SessionInputMode.LoginName, SessionInputMode.LoginPassword);
        Assert.True(loginToPassword.ClearInput, "LoginName -> Password must clear the previous credential.");
        Assert.True(loginToPassword.Refocus, "LoginName -> Password must retain keyboard focus.");

        CommandInputTransitionPresentation passwordToCommand = CommandInputPolicy.Transition(
            SessionInputMode.LoginPassword, SessionInputMode.Normal);
        Assert.True(passwordToCommand.ClearInput, "Password -> Command must clear sensitive text.");
        Assert.True(passwordToCommand.Refocus, "Password -> Command must retain keyboard focus.");
        return Task.CompletedTask;
    }

    private static Task PasswordInputIsExcludedFromHistory()
    {
        Assert.False(
            CommandInputPolicy.ShouldRecordHistory(SessionInputMode.LoginPassword, "super-secret-password"),
            "Sensitive password input must never enter command history.");
        Assert.False(
            CommandInputPolicy.ShouldEchoToTranscript(sensitive: true),
            "Sensitive password input must never be locally echoed into the transcript.");
        Assert.False(
            CommandInputPolicy.ShouldRecordHistory(SessionInputMode.LoginName, "Randolph"),
            "Character names must stay out of normal MUD command history.");
        Assert.True(
            CommandInputPolicy.ShouldRecordHistory(SessionInputMode.Normal, "look"),
            "Normal commands must remain eligible for command history.");
        return Task.CompletedTask;
    }

    private static Task PromptTelemetryTriggersRecoveryOnly()
    {
        IReadOnlyList<JevDomain> domains = JevDecisionTriggerPolicy.GetTriggeredDomains(
            Prompt(new ExitState(true, ["north", "west"])));

        Assert.SequenceEqual([JevDomain.Recovery], domains);
        return Task.CompletedTask;
    }

    private static Task AlreadyStandingFeedbackRepairsPostureState()
    {
        AvendarSemanticParser parser = new();
        IReadOnlyList<IMudEvent> events = parser.ParseLine("You are already standing.");
        Assert.Equal(1, events.Count);
        CharacterPositionObserved observed = (CharacterPositionObserved)events[0];
        Assert.Equal("standing", observed.Position);

        StateSnapshot initial = StateSnapshot.Initial with
        {
            Character = StateSnapshot.Initial.Character with { Position = "resting" }
        };
        StateSnapshot next = StateReducer.Reduce(initial, Envelope(1, observed));
        Assert.Equal("standing", next.Character.Position);
        return Task.CompletedTask;
    }

    private static Task RecoveryJevDoesNotGuessStand()
    {
        StateSnapshot initial = StateSnapshot.Initial;
        StateSnapshot state = initial with
        {
            Session = initial.Session with { InputMode = SessionInputMode.Normal },
            Character = initial.Character with
            {
                HitPoints = new VitalState(100, 100),
                Mana = new VitalState(100, 100),
                Movement = new VitalState(100, 100),
                Position = "unknown"
            }
        };

        bool created = AvendarJevRequestFactory.TryCreateRecoveryRequest(state, null, out JevChoiceRequest? request);
        Assert.False(created, "Unknown posture must not produce a speculative stand command.");
        Assert.True(request is null, "No recovery request should be created when posture and resources require no known action.");
        return Task.CompletedTask;
    }

    private static async Task AutonomySupervisorSuppressesRepeatedStand()
    {
        await using EventPipeline events = new();
        StateReducer reducer = new(events.StateEvents);
        FakeSender sender = new();
        JevAuthorityService authority = new(events);
        ActionProcessor actions = new(sender, reducer, authority, events);
        SelectingJevEngine engine = new(request =>
            request.Criteria.ContainsKey("stand") ? "stand" : request.Criteria.Keys.First());
        await using ScriptExecutionSupervisor execution = new();
        ScriptScheduler scheduler = new();
        ClientScriptCommands commands = new(actions, reducer);
        JevDecisionCoordinator coordinator = new(
            events.SubscribeLossless(),
            reducer,
            authority,
            commands,
            scheduler,
            execution,
            events,
            () => engine);
        using CancellationTokenSource cts = new();
        Task reducerTask = reducer.RunAsync(cts.Token);
        Task actionTask = actions.RunAsync(cts.Token);
        Task coordinatorTask = coordinator.RunAsync(cts.Token);

        Dictionary<JevDomain, JevAuthority> domains = Enum.GetValues<JevDomain>()
            .ToDictionary(domain => domain, _ => JevAuthority.Off);
        domains[JevDomain.Recovery] = JevAuthority.Auto;
        await authority.ApplyProfileAsync(JevAuthoritySnapshot.Create(JevPreset.Custom, domains));
        await events.PublishAsync(new SessionInputModeChanged(SessionInputMode.Normal), "test");
        await events.PublishAsync(new CharacterPromptObserved(
            100, 100, 100, 100, 100, 100, 1, 1, "resting", "A Room",
            new ExitState(true, ["north"]), "inside", "light"), "test");

        await WaitUntilAsync(() => sender.Commands.Count == 1);
        Assert.Equal("stand", sender.Commands[0]);

        for (int i = 0; i < 3; i++)
        {
            await events.PublishAsync(new CharacterPromptObserved(
                100, 100, 100, 100, 100, 100, 1, 1, "resting", "A Room",
                new ExitState(true, ["north"]), "inside", "light"), "test");
        }

        await Task.Delay(500);
        Assert.Equal(1, sender.Commands.Count);

        cts.Cancel();
        await IgnoreCancellation(reducerTask);
        await IgnoreCancellation(actionTask);
        await IgnoreCancellation(coordinatorTask);
    }

    private static Task NavigationJevSuppressesImmediateBacktrack()
    {
        StateSnapshot initial = StateSnapshot.Initial;
        IReadOnlyDictionary<string, RoomNodeState> rooms = new Dictionary<string, RoomNodeState>
        {
            ["room-a"] = new("room-a", "A", null, null, [], DateTimeOffset.UtcNow),
            ["room-b"] = new("room-b", "B", null, null, [], DateTimeOffset.UtcNow),
            ["room-c"] = new("room-c", "C", null, null, [], DateTimeOffset.UtcNow)
        };
        StateSnapshot state = initial with
        {
            Session = initial.Session with { InputMode = SessionInputMode.Normal },
            Character = initial.Character with
            {
                HitPoints = new VitalState(100, 100),
                Mana = new VitalState(100, 100),
                Movement = new VitalState(100, 100),
                Position = "standing"
            },
            Room = initial.Room with
            {
                Id = "room-b",
                Name = "B",
                Exits = new ExitState(true, ["west", "north"],
                [
                    new RoomExitObservation("west", true, ExitDoorState.Open, ExitTraversability.Traversable),
                    new RoomExitObservation("north", true, ExitDoorState.Open, ExitTraversability.Traversable)
                ])
            },
            World = new WorldMapState(rooms,
            [
                new RoomEdgeState("room-b", "west", "room-a", DateTimeOffset.UtcNow),
                new RoomEdgeState("room-b", "north", "room-c", DateTimeOffset.UtcNow)
            ], [])
        };
        AvendarAutonomyConstraints constraints = new(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            "west",
            ["room-a", "room-b"],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        bool created = AvendarJevRequestFactory.TryCreateNavigationRequest(state, null, constraints, out JevChoiceRequest? request);
        Assert.True(created && request is not null, "Expected a navigation request.");
        Assert.False(request!.Criteria.ContainsKey("move:west"), "Immediate reversal must be removed when another exit exists.");
        Assert.True(request.Criteria.ContainsKey("move:north"), "Alternative exit should remain available.");
        return Task.CompletedTask;
    }

    private static Task NavigationJevPermitsDeadEndBacktrack()
    {
        StateSnapshot initial = StateSnapshot.Initial;
        StateSnapshot state = initial with
        {
            Session = initial.Session with { InputMode = SessionInputMode.Normal },
            Character = initial.Character with
            {
                HitPoints = new VitalState(100, 100),
                Mana = new VitalState(100, 100),
                Movement = new VitalState(100, 100),
                Position = "standing"
            },
            Room = initial.Room with
            {
                Id = "room-b",
                Name = "B",
                Exits = new ExitState(true, ["west"],
                [new RoomExitObservation("west", true, ExitDoorState.Open, ExitTraversability.Traversable)])
            }
        };
        AvendarAutonomyConstraints constraints = new(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            "west",
            ["room-a", "room-b"],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        bool created = AvendarJevRequestFactory.TryCreateNavigationRequest(state, null, constraints, out JevChoiceRequest? request);
        Assert.True(created && request is not null, "Dead ends must still permit the only way out.");
        Assert.True(request!.Criteria.ContainsKey("move:west"), "Backtracking must remain available when it is the sole exit.");
        return Task.CompletedTask;
    }

    private static Task SemanticCombatTriggersJev()
    {
        IReadOnlyList<JevDomain> damageDomains = JevDecisionTriggerPolicy.GetTriggeredDomains(
            new CombatTargetConditionObserved("a human pupil", "pretty hurt"));
        IReadOnlyList<JevDomain> equipmentDomains = JevDecisionTriggerPolicy.GetTriggeredDomains(
            new EquipmentChanged("wielded", "a rawhide whip"));

        Assert.SequenceEqual([JevDomain.Combat], damageDomains);
        Assert.SequenceEqual([JevDomain.Inventory, JevDomain.Combat], equipmentDomains);
        return Task.CompletedTask;
    }

    private static Task UnrelatedDeathDoesNotEndCombat()
    {
        StateSnapshot current = StateReducer.Reduce(
            StateSnapshot.Initial,
            Envelope(1, new CombatTargetConditionObserved("an oversized rat", "pretty hurt")));

        StateSnapshot next = StateReducer.Reduce(current, Envelope(2, new EnemyKilled("a city guard")));

        Assert.True(ReferenceEquals(current, next), "Death of a different entity must not clear the current combat target.");
        Assert.True(next.Combat.Active, "Current combat must remain active after an unrelated death.");
        return Task.CompletedTask;
    }

    private static Task CombatTargetChangeClearsStaleState()
    {
        StateSnapshot current = StateSnapshot.Initial with
        {
            Combat = new CombatState(
                true,
                "rat-1",
                "an oversized rat",
                "pretty hurt",
                new DamageObservation(CombatActor.Player, "player", "an oversized rat", "stab", "weak", 1, "hits", 1),
                null,
                null)
        };

        StateSnapshot next = StateReducer.Reduce(current, Envelope(1, new CombatStateChanged(true, "guard-1")));

        Assert.Equal("guard-1", next.Combat.TargetId);
        Assert.True(next.Combat.TargetName is null, "A new target id must not inherit the prior target name.");
        Assert.True(next.Combat.TargetCondition is null, "A new target id must not inherit prior condition state.");
        Assert.True(next.Combat.LastPlayerDamage is null, "A new target id must not inherit prior damage state.");
        return Task.CompletedTask;
    }

    private static async Task TypesafePreservesInjectedHttpTimeout()
    {
        using HttpClient http = new(new FakeHttpHandler("{}"))
        {
            BaseAddress = new Uri("https://api.typesafe.ai/"),
            Timeout = TimeSpan.FromSeconds(17)
        };
        await using EventPipeline events = new();
        using TypesafeJevDecisionEngine engine = new(
            new TypesafeJevOptions("test-key", "jev-latest", MaxRetries: 0),
            events,
            http);

        Assert.Equal(TimeSpan.FromSeconds(17), http.Timeout);
    }

    private static async Task TypesafeChoiceMapsToTrace()
    {
        await using EventPipeline events = new();
        FakeHttpHandler handler = new("""
        {
          "model":"jev-1.13.0",
          "answers":{
            "action":{
              "type":"choice",
              "choice":"bash_guard",
              "probabilities":{"bash_guard":0.72,"flee":0.18,"wait":0.10},
              "confidence":0.81
            }
          },
          "usage":{"input_tokens":100,"output_tokens":20}
        }
        """);
        using HttpClient http = new(handler) { BaseAddress = new Uri("https://api.typesafe.ai/") };
        using TypesafeJevDecisionEngine engine = new(
            new TypesafeJevOptions("test-key", "jev-latest", MaxRetries: 0),
            events,
            http);

        JevDecisionTrace trace = await engine.EvaluateChoiceAsync(new JevChoiceRequest(
            42,
            JevDomain.Combat,
            "Choose the immediate combat action.",
            new { player_hp_pct = 0.7, target_hp_pct = 0.4 },
            new Dictionary<string, string?>
            {
                ["bash_guard"] = "Attempt to control the current target.",
                ["flee"] = "Leave combat.",
                ["wait"] = "Take no command action."
            }));

        Assert.Equal("bash_guard", trace.Selected.Action);
        Assert.Equal(3, trace.Candidates.Count);
        Assert.Equal("jev-1.13.0", trace.Model);
        Assert.Equal(42L, trace.StateVersion);
        Assert.Equal("Bearer test-key", handler.LastAuthorization);
    }

    private static async Task TypesafeParallelQuestionsMapToTrace()
    {
        await using EventPipeline events = new();
        FakeHttpHandler handler = new("""
        {
          "model":"jev-1.13.0",
          "answers":{
            "action":{
              "type":"choice",
              "choice":"recover",
              "probabilities":{"recover":0.67,"continue":0.33},
              "confidence":0.74
            },
            "danger":{
              "type":"score",
              "score":2.25,
              "probabilities":{"0":0.02,"1":0.18,"2":0.45,"3":0.35},
              "confidence":0.79,
              "legend":{"0":"stable","1":"pressured","2":"danger","3":"critical"}
            },
            "disengage":{
              "type":"noul",
              "noul":0.82
            }
          },
          "usage":{"input_tokens":140,"output_tokens":30}
        }
        """);
        using HttpClient http = new(handler) { BaseAddress = new Uri("https://api.typesafe.ai/") };
        using TypesafeJevDecisionEngine engine = new(
            new TypesafeJevOptions("test-key", "jev-latest", MaxRetries: 0),
            events,
            http);

        JevDecisionTrace trace = await engine.EvaluateChoiceAsync(new JevChoiceRequest(
            9,
            JevDomain.Combat,
            "Choose an immediate action.",
            new { hp = 0.4 },
            new Dictionary<string, string?>
            {
                ["recover"] = "Recover footing.",
                ["continue"] = "Issue no extra command."
            },
            JevAuthority.Suggest,
            DecisionSource.Jev,
            [
                JevAuxiliaryQuestion.Score("danger", "Rate danger.", "stable", "pressured", "danger", "critical"),
                JevAuxiliaryQuestion.Noul("disengage", "Prioritize disengagement?")
            ]));

        Assert.Equal(2, trace.Metrics!.Count);
        JevDecisionMetric danger = trace.Metrics.Single(metric => metric.Id == "danger");
        JevDecisionMetric disengage = trace.Metrics.Single(metric => metric.Id == "disengage");
        Assert.Equal(JevQuestionType.Score, danger.Type);
        Assert.Equal(2.25d, danger.Value);
        Assert.Equal(0.79d, danger.Confidence!.Value);
        Assert.Equal("danger", danger.Legend!["2"]);
        Assert.Equal(JevQuestionType.Noul, disengage.Type);
        Assert.Equal(0.82d, disengage.Value);
        Assert.Equal(140, trace.Usage!.InputTokens);
    }

    private static RoomObservationObserved RoomObservation(
        string id,
        string name,
        string fingerprint,
        string description,
        IReadOnlyList<RoomExitObservation> exits) =>
        new(
            id,
            name,
            fingerprint,
            description,
            exits,
            Array.Empty<RoomContentObservation>(),
            ObservationCompleteness.Complete);

    private static EventEnvelope Envelope(long sequence, IMudEvent mudEvent) =>
        new(
            Guid.NewGuid(),
            sequence,
            DateTimeOffset.Parse("2026-09-19T00:00:00Z").AddMilliseconds(sequence),
            "test",
            mudEvent);

    private static CharacterPromptObserved Prompt(ExitState exits) =>
        new(
            116,
            132,
            108,
            116,
            115,
            132,
            4880,
            1869,
            "standing",
            "The Northeast Corner of the First Floor",
            exits,
            "inside",
            "light");

    private static StateSnapshot StateWithAvailableSkill(string name, int mana, string terrain)
    {
        StateSnapshot state = StateSnapshot.Initial;
        return state with
        {
            Character = state.Character with
            {
                Mana = new VitalState(mana, mana),
                Skills = [new SkillState(name, 1, SkillAvailability.Available, 1)]
            },
            Room = state.Room with { Terrain = terrain }
        };
    }

    private static async Task RunAsync(string name, Func<Task> test)
    {
        try
        {
            await test();
            _passed++;
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception exception)
        {
            _failed++;
            Console.WriteLine($"FAIL {name}: {exception.Message}");
        }
    }

    private static Task WaitUntilAsync(Func<bool> condition) =>
        WaitUntilAsync(condition, TimeSpan.FromSeconds(1));

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeoutDuration)
    {
        using CancellationTokenSource timeout = new(timeoutDuration);
        while (!condition())
        {
            await Task.Delay(5, timeout.Token);
        }
    }

    private static async Task IgnoreCancellation(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class SelectingJevEngine : NexMud.Jev.IJevDecisionEngine
    {
        private readonly Func<JevChoiceRequest, string> _select;

        public SelectingJevEngine(Func<JevChoiceRequest, string> select) => _select = select;

        public Task<JevDecisionTrace> EvaluateChoiceAsync(
            JevChoiceRequest request,
            CancellationToken cancellationToken = default)
        {
            string selectedAction = _select(request);
            DecisionCandidate[] candidates = request.Criteria.Keys
                .Select(action => new DecisionCandidate(
                    action,
                    request.Criteria[action],
                    action.Equals(selectedAction, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0))
                .ToArray();
            DecisionCandidate selected = candidates.First(candidate =>
                candidate.Action.Equals(selectedAction, StringComparison.OrdinalIgnoreCase));
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return Task.FromResult(new JevDecisionTrace(
                Guid.NewGuid(),
                request.StateVersion,
                now,
                now,
                request.Domain,
                request.Goal,
                candidates,
                selected,
                1.0,
                "test",
                DecisionOutcome.Proposed,
                request.Authority,
                request.Source));
        }
    }

    private sealed class RecordingDiagnosticsSink : IScriptDiagnosticsSink
    {
        private readonly object _gate = new();
        private readonly List<ScriptDiagnosticRecord> _records = [];

        public IReadOnlyList<ScriptDiagnosticRecord> Records
        {
            get { lock (_gate) return _records.ToArray(); }
        }

        public void Record(ScriptDiagnosticRecord diagnostic)
        {
            lock (_gate) _records.Add(diagnostic);
        }
    }

    private sealed class BlockingScriptCommands : IScriptCommands
    {
        private readonly TaskCompletionSource<ScriptCommandResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration _registration;
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ScriptCommandResult> SendAsync(
            ScriptCommandRequest request,
            CancellationToken cancellationToken = default)
        {
            _registration = cancellationToken.Register(() => CancellationObserved.TrySetResult(true));
            Started.TrySetResult(true);
            return _completion.Task;
        }

        public void Complete()
        {
            _registration.Dispose();
            _completion.TrySetResult(new ScriptCommandResult(Guid.NewGuid(), true));
        }
    }

    private sealed class DelayedFirstScriptCommands : IScriptCommands
    {
        private readonly object _gate = new();
        private readonly List<ScriptCommandRequest> _requests = [];
        private readonly TaskCompletionSource<ScriptCommandResult> _firstCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int RequestCount { get { lock (_gate) return _requests.Count; } }
        public IReadOnlyList<string> Commands { get { lock (_gate) return _requests.Select(request => request.Command).ToArray(); } }

        public Task<ScriptCommandResult> SendAsync(
            ScriptCommandRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count;
            lock (_gate)
            {
                _requests.Add(request);
                count = _requests.Count;
            }
            if (count == 1)
            {
                FirstStarted.TrySetResult(true);
                return _firstCompletion.Task;
            }
            return Task.FromResult(new ScriptCommandResult(request.ActionId ?? Guid.NewGuid(), true));
        }

        public void ReleaseFirst() =>
            _firstCompletion.TrySetResult(new ScriptCommandResult(Guid.NewGuid(), true));
    }

    private sealed class RecordingScriptCommands : IScriptCommands
    {
        private readonly object _gate = new();
        private readonly List<ScriptCommandRequest> _requests = [];

        public ScriptCommandRequest? LastRequest
        {
            get { lock (_gate) return _requests.LastOrDefault(); }
        }

        public int RequestCount
        {
            get { lock (_gate) return _requests.Count; }
        }

        public Task<ScriptCommandResult> SendAsync(
            ScriptCommandRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate) _requests.Add(request);
            return Task.FromResult(new ScriptCommandResult(request.ActionId ?? Guid.NewGuid(), true));
        }
    }

    private sealed record ScriptLogEntry(ScriptLogLevel Level, string Message, string? DataJson);

    private sealed class RecordingScriptLog : IScriptLog
    {
        private readonly object _gate = new();
        private readonly List<ScriptLogEntry> _entries = [];

        public IReadOnlyList<ScriptLogEntry> Entries
        {
            get { lock (_gate) return _entries.ToArray(); }
        }

        public Task WriteAsync(ScriptLogLevel level, string message, CancellationToken cancellationToken = default) =>
            WriteAsync(level, message, null, cancellationToken);

        public Task WriteAsync(
            ScriptLogLevel level,
            string message,
            string? dataJson,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate) _entries.Add(new ScriptLogEntry(level, message, dataJson));
            return Task.CompletedTask;
        }
    }

    private sealed class StaticScriptState : IScriptState
    {
        public ScriptStateSnapshot Snapshot() => new(
            0,
            true,
            "Normal",
            new ScriptCharacterState(
                new ScriptResourceState(null, null, null),
                new ScriptResourceState(null, null, null),
                new ScriptResourceState(null, null, null),
                null),
            new ScriptRoomState(null, null, Array.Empty<string>()),
            new ScriptCombatState(false, null));
    }

    private sealed class RecordingMapperHost : IScriptMapper
    {
        private readonly string _destination;
        private readonly string _direction;
        private string _current;

        public RecordingMapperHost(string current, string destination, string direction)
        {
            _current = current;
            _destination = destination;
            _direction = direction;
        }

        public List<ScriptMoveRequest> MoveRequests { get; } = [];

        public Task<ScriptRoomSnapshot?> CurrentRoomAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ScriptRoomSnapshot?>(new ScriptRoomSnapshot(_current, _current));

        public Task<ScriptRoutePlan?> FindPathAsync(string destinationRoomId, CancellationToken cancellationToken = default)
        {
            ScriptRoutePlan plan = new(
                $"route-{_current}-{destinationRoomId}",
                _current,
                destinationRoomId,
                0,
                [new ScriptRouteStep(1, _current, _direction, destinationRoomId, null, "Open")]);
            return Task.FromResult<ScriptRoutePlan?>(plan);
        }

        public Task<ScriptMovementResult> MoveAsync(ScriptMoveRequest request, CancellationToken cancellationToken = default)
        {
            MoveRequests.Add(request);
            string from = _current;
            _current = request.ExpectedRoomId ?? _destination;
            return Task.FromResult(new ScriptMovementResult("moved", from, _current, request.ExpectedRoomId, _current));
        }
    }

    private sealed class DelayedFindPathMapper : IScriptMapper
    {
        private readonly TaskCompletionSource<ScriptRoutePlan?> _plan =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string _current = "A";

        public TaskCompletionSource<bool> FindPathStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<ScriptMoveRequest> MoveRequests { get; } = [];
        public string CurrentRoomId => _current;

        public Task<ScriptRoomSnapshot?> CurrentRoomAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<ScriptRoomSnapshot?>(new ScriptRoomSnapshot(_current, _current));
        }

        public Task<ScriptRoutePlan?> FindPathAsync(string destinationRoomId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FindPathStarted.TrySetResult(true);
            return _plan.Task;
        }

        public Task<ScriptMovementResult> MoveAsync(ScriptMoveRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MoveRequests.Add(request);
            string from = _current;
            _current = request.ExpectedRoomId ?? DestinationFallback(request.Direction);
            return Task.FromResult(new ScriptMovementResult("moved", from, _current, request.ExpectedRoomId, _current));
        }

        public void CompleteFindPath() => _plan.TrySetResult(new ScriptRoutePlan(
            "route-A-D",
            "A",
            "D",
            0,
            [new ScriptRouteStep(1, "A", "north", "D", null, "Open")]));

        private static string DestinationFallback(string direction) => direction == "north" ? "D" : "A";
    }

    private sealed class ReplayRouteMapper : IScriptMapper
    {
        private readonly object _gate = new();
        private readonly bool _blockedReplan;
        private bool _blockedEast;
        private string _current;

        private ReplayRouteMapper(string current, bool blockedReplan)
        {
            _current = current;
            _blockedReplan = blockedReplan;
        }

        public static ReplayRouteMapper SimpleScenario() => new("A", false);
        public static ReplayRouteMapper BlockedReplanScenario() => new("A", true);
        public static ReplayRouteMapper PauseResumeScenario() => new("A", false);

        public string CurrentRoomId { get { lock (_gate) return _current; } }
        public List<ScriptMoveRequest> MoveRequests { get; } = [];
        public void SetCurrentRoom(string roomId) { lock (_gate) _current = roomId; }

        public Task<ScriptRoomSnapshot?> CurrentRoomAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string current = CurrentRoomId;
            return Task.FromResult<ScriptRoomSnapshot?>(new ScriptRoomSnapshot(current, current));
        }

        public Task<ScriptRoutePlan?> FindPathAsync(string destinationRoomId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string current = CurrentRoomId;
            ScriptRouteStep[] steps = current switch
            {
                "A" when _blockedReplan =>
                [
                    new ScriptRouteStep(1, "A", "north", "B", null, "Open"),
                    new ScriptRouteStep(2, "B", "east", "C", null, "Open"),
                    new ScriptRouteStep(3, "C", "south", "D", null, "Open")
                ],
                "B" when _blockedReplan && !_blockedEast =>
                [
                    new ScriptRouteStep(1, "B", "east", "C", null, "Open"),
                    new ScriptRouteStep(2, "C", "south", "D", null, "Open")
                ],
                "B" when _blockedReplan =>
                [
                    new ScriptRouteStep(1, "B", "west", "E", null, "Open"),
                    new ScriptRouteStep(2, "E", "south", "D", null, "Open")
                ],
                "E" when _blockedReplan =>
                [new ScriptRouteStep(1, "E", "south", "D", null, "Open")],
                "X" => [new ScriptRouteStep(1, "X", "east", "D", null, "Open")],
                "A" => [new ScriptRouteStep(1, "A", "north", "D", null, "Open")],
                _ when current == destinationRoomId => [],
                _ => []
            };
            ScriptRoutePlan plan = new($"route-{current}-{destinationRoomId}-{(_blockedEast ? 1 : 0)}", current, destinationRoomId, 0, steps);
            return Task.FromResult<ScriptRoutePlan?>(plan);
        }

        public Task<ScriptMovementResult> MoveAsync(ScriptMoveRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                MoveRequests.Add(request);
                if (_blockedReplan && _current == "B" && request.Direction == "east" && !_blockedEast)
                {
                    _blockedEast = true;
                    return Task.FromResult(new ScriptMovementResult(
                        "blocked",
                        FromRoomId: "B",
                        ExpectedRoomId: request.ExpectedRoomId,
                        Reason: ScriptMovementFailureReason.NoExit,
                        Message: "No exit."));
                }

                string from = _current;
                _current = request.ExpectedRoomId ?? _current;
                return Task.FromResult(new ScriptMovementResult("moved", from, _current, request.ExpectedRoomId, _current));
            }
        }
    }

    private sealed class NoopScriptMapper : IScriptMapper
    {
        public Task<ScriptRoomSnapshot?> CurrentRoomAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ScriptRoomSnapshot?>(null);

        public Task<ScriptRoutePlan?> FindPathAsync(string destinationRoomId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ScriptRoutePlan?>(null);

        public Task<ScriptMovementResult> MoveAsync(ScriptMoveRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ScriptMovementResult("cancelled"));
    }

    private sealed class NoopScriptCodex : IScriptCodex
    {
        public Task<IReadOnlyList<ScriptCodexResult>> SearchAsync(
            string query,
            int limit = 50,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ScriptCodexResult>>(Array.Empty<ScriptCodexResult>());
    }

    private sealed class NoopScriptUi : IScriptUi
    {
        public Task NotifyAsync(ScriptUiNotification notification, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class NoopScriptLog : IScriptLog
    {
        public Task WriteAsync(
            ScriptLogLevel level,
            string message,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class MemoryScriptStorage : IScriptStorage
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        public MemoryScriptStorage(ScriptModuleId moduleId) => ModuleId = moduleId;
        public ScriptModuleId ModuleId { get; }
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(_values.TryGetValue(key, out string? value) ? value : null);
        public Task SetAsync(string key, string jsonValue, CancellationToken cancellationToken = default)
        {
            _values[key] = jsonValue;
            return Task.CompletedTask;
        }
        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(_values.Remove(key));
        public Task<IReadOnlyList<ScriptStorageEntry>> ListAsync(string? prefix = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ScriptStorageEntry>>(_values
                .Where(pair => prefix is null || pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(pair => new ScriptStorageEntry(pair.Key, pair.Value, DateTimeOffset.UnixEpoch))
                .ToArray());
    }

    private sealed class FakeSender : ICommandSender
    {
        public List<string> Commands { get; } = [];

        public Task SendCommandAsync(string command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly string _response;

        public FakeHttpHandler(string response) => _response = response;

        public string? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastAuthorization = request.Headers.Authorization?.ToString();
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StringContent(_response, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    private static class Assert
    {
        public static void Equal<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
            }
        }

        public static void True(bool value, string message)
        {
            if (!value)
            {
                throw new InvalidOperationException(message);
            }
        }

        public static void False(bool value, string message) => True(!value, message);

        public static T IsType<T>(object value)
        {
            if (value is T typed)
            {
                return typed;
            }

            throw new InvalidOperationException($"Expected type {typeof(T).Name}, got {value.GetType().Name}.");
        }

        public static T Single<T>(IEnumerable<T> values)
        {
            T[] array = values.ToArray();
            if (array.Length != 1)
            {
                throw new InvalidOperationException($"Expected one item, got {array.Length}.");
            }
            return array[0];
        }

        public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
        {
            if (!expected.SequenceEqual(actual))
            {
                throw new InvalidOperationException(
                    $"Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
            }
        }

        public static void Throws<TException>(Action action) where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }
            throw new InvalidOperationException($"Expected exception {typeof(TException).Name}.");
        }
    }

    private const string ScoreFixture = """
/--------------------------------------------------------------------------\
| Leland the Filcher                                                       |
\--------------------------------------------------------------------------/
/-------------\/-----------------------------------------------------------\
| Str: 15(15) || Lineage: human       Class: thief           Level:  3     |
| Int: 15(15) || Gender : male        Age  : 18 (youthful)   Hours: 0      |
| Wis: 15(15) ||                                                           |
| Dex: 18(18) || Hit : 132/132        Experience : 4905      Hitroll: 9    |
| Con: 15(15) || Mana: 116/116        Exper/level: 1844      Damroll: 6    |
| Chr: 15(15) || Move: 132/132        Exploration: 43        Saves  : -5   |
\-------------/\-----------------------------------------------------------/
/-------------------------------------\/-----------------------------------\
| Resonance: None                     || Items : 3     Max Items : 100     |
| Alignment: Chaotic Neutral          || Weight: 2     Max Weight: 352     |
| Total AC : vulnerable        (100)  || Wealth: 129c                      |
\-------------------------------------/\-----------------------------------/
You are not affected by any spells.
""";

    private const string SkillsFixture = """
Level  1: dagger              75%        sword                1%
          whip                 1%        backstab             1%
          hand to hand         1%        steal                1%
          recover              1%
Level  2: peek                 1%
Level  3: dirt kicking         1%
Level  4: dual wield         n/a      dodge              n/a
[Hit Return to continue]
Level 27: loot               n/a
Level 30: circle stab        n/a
""";
}
