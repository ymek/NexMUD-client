using System.Text.Json.Serialization;

namespace NexMud.Jev.Typesafe;

internal sealed record SystemOneRequest(
    [property: JsonPropertyName("state")] object State,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("questions")] IReadOnlyDictionary<string, SystemOneQuestion> Questions);

internal sealed record SystemOneQuestion(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("instructions")] string Instructions,
    [property: JsonPropertyName("criteria")] object? Criteria = null);

internal sealed record SystemOneResponse(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("answers")] IReadOnlyDictionary<string, SystemOneAnswer> Answers,
    [property: JsonPropertyName("usage")] SystemOneUsage Usage);

internal sealed record SystemOneAnswer(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("choice")] string? Choice,
    [property: JsonPropertyName("probabilities")] IReadOnlyDictionary<string, double>? Probabilities,
    [property: JsonPropertyName("confidence")] double? Confidence,
    [property: JsonPropertyName("score")] double? Score,
    [property: JsonPropertyName("noul")] double? Noul);

internal sealed record SystemOneUsage(
    [property: JsonPropertyName("input_tokens")] int InputTokens,
    [property: JsonPropertyName("output_tokens")] int OutputTokens);
