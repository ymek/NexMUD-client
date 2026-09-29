using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using JevMud.Contracts.Jev;
using JevMud.Core.Events;
using JevMud.Contracts.Events;

namespace JevMud.Jev.Typesafe;

public sealed class TypesafeJevDecisionEngine : IJevDecisionEngine, IDisposable
{
    private const string ActionQuestionId = "action";
    private readonly HttpClient _http;
    private readonly TypesafeJevOptions _options;
    private readonly IEventSink _events;
    private readonly bool _ownsHttpClient;

    public TypesafeJevDecisionEngine(TypesafeJevOptions options, IEventSink events, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(events);
        options.Validate();

        _options = options;
        _events = events;
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? new HttpClient();
        _http.BaseAddress ??= options.EffectiveBaseUri;
        if (_ownsHttpClient)
        {
            _http.Timeout = Timeout.InfiniteTimeSpan;
        }
    }

    public async Task<JevDecisionTrace> EvaluateChoiceAsync(
        JevChoiceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        Guid decisionId = Guid.NewGuid();
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        await _events.PublishAsync(
            new JevEvaluationStarted(decisionId, request.Domain, request.StateVersion),
            "jev.typesafe",
            cancellationToken).ConfigureAwait(false);

        Dictionary<string, SystemOneQuestion> questions = new(StringComparer.Ordinal)
        {
            [ActionQuestionId] = new SystemOneQuestion("choice", request.Goal, request.Criteria)
        };
        foreach (JevAuxiliaryQuestion question in request.AuxiliaryQuestions ?? Array.Empty<JevAuxiliaryQuestion>())
        {
            questions[question.Id] = question.Type switch
            {
                JevQuestionType.Score => new SystemOneQuestion("score", question.Instructions, question.ScoreCriteria),
                JevQuestionType.Noul => new SystemOneQuestion("noul", question.Instructions, question.NoulCriteria),
                _ => throw new ArgumentException(
                    $"Auxiliary question '{question.Id}' must be Score or Noul.",
                    nameof(request))
            };
        }

        SystemOneRequest wireRequest = new(request.State, _options.Model, questions);

        SystemOneResponse response;
        try
        {
            response = await SendWithRetryAsync(wireRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            DateTimeOffset failedAt = DateTimeOffset.UtcNow;
            await _events.PublishAsync(
                new JevEvaluationFailed(decisionId, failedAt - startedAt, exception.Message),
                "jev.typesafe",
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        DateTimeOffset completedAt = DateTimeOffset.UtcNow;
        SystemOneAnswer action = ParseActionAnswer(response, request);

        List<DecisionCandidate> candidates = action.Probabilities!
            .Select(pair => new DecisionCandidate(
                pair.Key,
                request.Criteria.TryGetValue(pair.Key, out string? description) ? description : null,
                pair.Value))
            .OrderByDescending(candidate => candidate.Probability)
            .ToList();

        DecisionCandidate selected = candidates.FirstOrDefault(candidate => candidate.Action == action.Choice)
            ?? throw new InvalidOperationException("Selected TypeSafe action is missing from probabilities.");

        IReadOnlyList<JevDecisionMetric> metrics = ParseAuxiliaryAnswers(response, request);
        JevDecisionTrace trace = new(
            decisionId,
            request.StateVersion,
            startedAt,
            completedAt,
            request.Domain,
            request.Goal,
            candidates,
            selected,
            action.Confidence!.Value,
            response.Model,
            DecisionOutcome.Proposed,
            request.Authority,
            request.Source,
            metrics,
            new JevDecisionUsage(response.Usage.InputTokens, response.Usage.OutputTokens));

        await _events.PublishAsync(
            new JevEvaluationCompleted(decisionId, completedAt - startedAt),
            "jev.typesafe",
            cancellationToken).ConfigureAwait(false);
        await _events.PublishAsync(new JevDecisionProduced(trace), "jev.typesafe", cancellationToken)
            .ConfigureAwait(false);

        return trace;
    }

    private static void ValidateRequest(JevChoiceRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Goal))
        {
            throw new ArgumentException("Decision goal is required.", nameof(request));
        }
        if (request.Criteria.Count < 2)
        {
            throw new ArgumentException("Choice decisions require at least two criteria.", nameof(request));
        }

        HashSet<string> ids = new(StringComparer.Ordinal) { ActionQuestionId };
        foreach (JevAuxiliaryQuestion question in request.AuxiliaryQuestions ?? Array.Empty<JevAuxiliaryQuestion>())
        {
            if (string.IsNullOrWhiteSpace(question.Id) || !ids.Add(question.Id))
            {
                throw new ArgumentException("Jev question ids must be non-empty and unique.", nameof(request));
            }
            if (string.IsNullOrWhiteSpace(question.Instructions))
            {
                throw new ArgumentException($"Question '{question.Id}' needs instructions.", nameof(request));
            }
            if (question.Type == JevQuestionType.Score && (question.ScoreCriteria?.Count ?? 0) is < 2 or > 10)
            {
                throw new ArgumentException($"Score question '{question.Id}' requires 2-10 ordered criteria.", nameof(request));
            }
            if (question.Type == JevQuestionType.Noul && question.ScoreCriteria is not null)
            {
                throw new ArgumentException($"Noul question '{question.Id}' cannot use score criteria.", nameof(request));
            }
            if (question.Type == JevQuestionType.Choice)
            {
                throw new ArgumentException("Only the primary action question may be Choice.", nameof(request));
            }
        }
    }

    private static SystemOneAnswer ParseActionAnswer(SystemOneResponse response, JevChoiceRequest request)
    {
        if (!response.Answers.TryGetValue(ActionQuestionId, out SystemOneAnswer? answer))
        {
            throw new InvalidOperationException("TypeSafe response did not contain the action answer.");
        }
        if (!string.Equals(answer.Type, "choice", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"TypeSafe returned answer type '{answer.Type}' instead of 'choice'.");
        }
        if (string.IsNullOrWhiteSpace(answer.Choice) || answer.Probabilities is null || answer.Confidence is null)
        {
            throw new InvalidOperationException("TypeSafe choice response is missing choice, probabilities, or confidence.");
        }
        if (!request.Criteria.ContainsKey(answer.Choice))
        {
            throw new InvalidOperationException($"TypeSafe selected unknown action '{answer.Choice}'.");
        }
        return answer;
    }

    private static IReadOnlyList<JevDecisionMetric> ParseAuxiliaryAnswers(
        SystemOneResponse response,
        JevChoiceRequest request)
    {
        List<JevDecisionMetric> metrics = [];
        foreach (JevAuxiliaryQuestion question in request.AuxiliaryQuestions ?? Array.Empty<JevAuxiliaryQuestion>())
        {
            if (!response.Answers.TryGetValue(question.Id, out SystemOneAnswer? answer))
            {
                throw new InvalidOperationException($"TypeSafe response did not contain '{question.Id}'.");
            }

            switch (question.Type)
            {
                case JevQuestionType.Score:
                    if (!string.Equals(answer.Type, "score", StringComparison.Ordinal) || answer.Score is null)
                    {
                        throw new InvalidOperationException($"TypeSafe score answer '{question.Id}' is malformed.");
                    }
                    IReadOnlyDictionary<string, string>? legend = question.ScoreCriteria is null
                        ? null
                        : question.ScoreCriteria
                            .Select((label, index) => new KeyValuePair<string, string>(
                                index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                label))
                            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                    metrics.Add(new JevDecisionMetric(
                        question.Id,
                        JevQuestionType.Score,
                        question.Instructions,
                        answer.Score.Value,
                        answer.Confidence,
                        answer.Probabilities,
                        legend));
                    break;
                case JevQuestionType.Noul:
                    if (!string.Equals(answer.Type, "noul", StringComparison.Ordinal) || answer.Noul is null)
                    {
                        throw new InvalidOperationException($"TypeSafe Noul answer '{question.Id}' is malformed.");
                    }
                    metrics.Add(new JevDecisionMetric(
                        question.Id,
                        JevQuestionType.Noul,
                        question.Instructions,
                        answer.Noul.Value));
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported auxiliary question type {question.Type}.");
            }
        }
        return metrics;
    }

    private async Task<SystemOneResponse> SendWithRetryAsync(
        SystemOneRequest request,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.EffectiveRequestTimeout);
            using HttpRequestMessage message = new(HttpMethod.Post, "v1/systemone")
            {
                Content = JsonContent.Create(request, options: JsonOptions)
            };
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

            HttpResponseMessage responseMessage;
            try
            {
                responseMessage = await _http.SendAsync(message, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"TypeSafe request timed out after {_options.EffectiveRequestTimeout}.");
            }

            using HttpResponseMessage response = responseMessage;
            if (response.IsSuccessStatusCode)
            {
                SystemOneResponse? body = await response.Content.ReadFromJsonAsync<SystemOneResponse>(JsonOptions, timeout.Token)
                    .ConfigureAwait(false);
                return body ?? throw new InvalidOperationException("TypeSafe returned an empty response body.");
            }

            string error = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            bool retryable = response.StatusCode is (HttpStatusCode)429 or (HttpStatusCode)529;
            if (!retryable || attempt >= _options.MaxRetries)
            {
                throw new HttpRequestException(
                    $"TypeSafe request failed with {(int)response.StatusCode} {response.ReasonPhrase}: {error}",
                    null,
                    response.StatusCode);
            }

            TimeSpan delay = GetRetryDelay(response, attempt);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
        if (retryAfter is not null && retryAfter.Value > TimeSpan.Zero)
        {
            return retryAfter.Value;
        }

        DateTimeOffset? retryAt = response.Headers.RetryAfter?.Date;
        if (retryAt is not null)
        {
            TimeSpan until = retryAt.Value - DateTimeOffset.UtcNow;
            if (until > TimeSpan.Zero)
            {
                return until;
            }
        }

        double seconds = Math.Min(Math.Pow(2, attempt) * 0.25, 4);
        return TimeSpan.FromSeconds(seconds);
    }

    private static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}
