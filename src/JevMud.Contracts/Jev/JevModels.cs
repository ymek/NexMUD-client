using System.Collections.ObjectModel;

namespace JevMud.Contracts.Jev;

public enum JevDomain
{
    Combat,
    Navigation,
    Inventory,
    Loot,
    Quests,
    Social,
    Training,
    Recovery
}

public enum JevAuthority
{
    Off,
    Observe,
    Suggest,
    Approve,
    Auto
}

public enum DecisionSource
{
    Human,
    Rules,
    Jev,
    Hybrid
}

public enum JevPreset
{
    Off,
    Copilot,
    Bot,
    Autonomous,
    Custom
}

public enum DecisionOutcome
{
    Proposed,
    Observed,
    Suggested,
    AwaitingApproval,
    Rejected,
    Executed,
    Resolved
}

public enum JevQuestionType
{
    Choice,
    Score,
    Noul
}

public sealed record JevAuthoritySnapshot(
    JevPreset Preset,
    IReadOnlyDictionary<JevDomain, JevAuthority> Domains)
{
    public static JevAuthoritySnapshot Create(
        JevPreset preset,
        IDictionary<JevDomain, JevAuthority> domains) =>
        new(
            preset,
            new ReadOnlyDictionary<JevDomain, JevAuthority>(
                new Dictionary<JevDomain, JevAuthority>(domains)));
}

public sealed record DecisionCandidate(
    string Action,
    string? Description,
    double Probability,
    bool Valid = true,
    string? RejectionReason = null);

/// <summary>
/// A typed, non-generative Jev answer which supplements the primary action choice.
/// Score values are rubric indices (and may be fractional); Noul values are P(true).
/// </summary>
public sealed record JevDecisionMetric(
    string Id,
    JevQuestionType Type,
    string Instructions,
    double Value,
    double? Confidence = null,
    IReadOnlyDictionary<string, double>? Probabilities = null,
    IReadOnlyDictionary<string, string>? Legend = null);

public sealed record JevDecisionUsage(int InputTokens, int OutputTokens);

public sealed record JevDecisionTrace(
    Guid DecisionId,
    long StateVersion,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    JevDomain Domain,
    string Goal,
    IReadOnlyList<DecisionCandidate> Candidates,
    DecisionCandidate Selected,
    double Confidence,
    string Model,
    DecisionOutcome Outcome,
    JevAuthority Authority = JevAuthority.Suggest,
    DecisionSource Source = DecisionSource.Jev,
    IReadOnlyList<JevDecisionMetric>? Metrics = null,
    JevDecisionUsage? Usage = null)
{
    public TimeSpan Latency => CompletedAt - StartedAt;
}

public sealed record JevAuxiliaryQuestion(
    string Id,
    JevQuestionType Type,
    string Instructions,
    IReadOnlyList<string>? ScoreCriteria = null,
    IReadOnlyDictionary<string, string?>? NoulCriteria = null)
{
    public static JevAuxiliaryQuestion Score(string id, string instructions, params string[] criteria) =>
        new(id, JevQuestionType.Score, instructions, criteria, null);

    public static JevAuxiliaryQuestion Noul(
        string id,
        string instructions,
        string? trueCriterion = null,
        string? falseCriterion = null) =>
        new(
            id,
            JevQuestionType.Noul,
            instructions,
            null,
            trueCriterion is null && falseCriterion is null
                ? null
                : new ReadOnlyDictionary<string, string?>(new Dictionary<string, string?>
                {
                    ["true"] = trueCriterion,
                    ["false"] = falseCriterion
                }));
}

/// <summary>
/// A bounded System One decision request. The primary question is always a closed Choice;
/// optional Score/Noul questions are evaluated against the same state in the same round trip.
/// </summary>
public sealed record JevChoiceRequest(
    long StateVersion,
    JevDomain Domain,
    string Goal,
    object State,
    IReadOnlyDictionary<string, string?> Criteria,
    JevAuthority Authority = JevAuthority.Suggest,
    DecisionSource Source = DecisionSource.Jev,
    IReadOnlyList<JevAuxiliaryQuestion>? AuxiliaryQuestions = null);
