using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Iemas.Application.Common.Ai;
using Iemas.Application.Common.Interfaces;
using Microsoft.Extensions.Logging;

namespace Iemas.Infrastructure.Ai;

/// <summary>Outcome of one logical chat-completions call (all transport attempts included).</summary>
internal sealed record ChatCallResult(
    bool Succeeded,
    string? Content,
    string? ModelVersion,
    AiTokenUsage? Usage,
    ClassificationFailureCategory Category,
    string? Error,
    TimeSpan? RetryAfter,
    int Attempts);

/// <summary>
/// The one place that talks HTTP to OpenRouter's chat-completions endpoint, shared by classification and
/// the resolution assessor so both get the same reliability behaviour: a per-attempt timeout, bounded
/// retries with exponential backoff for timeouts / network errors / HTTP 5xx / HTTP 429 (honouring
/// Retry-After, capped), and a clean failure category otherwise. Never throws for provider problems;
/// only the caller's own cancellation propagates. The API key only ever goes into the Authorization header.
/// </summary>
internal static class OpenRouterChatCaller
{
    public static async Task<ChatCallResult> SendAsync(
        HttpClient http, string baseUrl, string apiKey, OpenRouterOptions options, object body,
        TimeSpan timeout, int maxRetries, IRetryDelay? retryDelay, ILogger logger, string modelForLog, CancellationToken cancellationToken)
    {
        maxRetries = Math.Max(0, maxRetries);
        ChatCallResult? last = null;
        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            last = await SendOnceAsync(http, baseUrl, apiKey, options, body, timeout, logger, modelForLog, cancellationToken) with { Attempts = attempt + 1 };
            if (last.Succeeded) return last;
            if (last.Category is not (ClassificationFailureCategory.Transient or ClassificationFailureCategory.RateLimited) || attempt == maxRetries) break;

            var delay = BackoffFor(options, last, attempt);
            logger.LogInformation("OpenRouter call to {Model} failed ({Category}); retry {Attempt}/{Max} after {Delay}", modelForLog, last.Category, attempt + 1, maxRetries, delay);
            if (retryDelay is not null) await retryDelay.WaitAsync(delay, cancellationToken);
        }
        return last!;
    }

    internal static TimeSpan BackoffFor(OpenRouterOptions options, ChatCallResult failure, int attemptIndex)
    {
        var max = TimeSpan.FromSeconds(Math.Max(0, options.MaxRetryDelaySeconds));
        if (failure.Category == ClassificationFailureCategory.RateLimited && failure.RetryAfter is TimeSpan ra)
        {
            return ra < TimeSpan.Zero ? TimeSpan.Zero : (ra > max ? max : ra);
        }
        var exp = TimeSpan.FromMilliseconds(Math.Max(0, options.RetryBaseDelayMilliseconds) * Math.Pow(2, attemptIndex));
        return exp > max ? max : exp;
    }

    private static async Task<ChatCallResult> SendOnceAsync(
        HttpClient http, string baseUrl, string apiKey, OpenRouterOptions options, object body,
        TimeSpan timeout, ILogger logger, string modelForLog, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            if (!string.IsNullOrWhiteSpace(options.SiteUrl)) request.Headers.Add("HTTP-Referer", options.SiteUrl);
            if (!string.IsNullOrWhiteSpace(options.SiteName)) request.Headers.Add("X-Title", options.SiteName);
            request.Content = JsonContent.Create(body);

            using var response = await http.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                var text = await SafeReadBodyAsync(response, cts.Token);
                logger.LogWarning("OpenRouter request failed with status {StatusCode} for model {Model}", response.StatusCode, modelForLog);
                return new ChatCallResult(false, null, null, null, Categorize(response.StatusCode),
                    $"OpenRouter returned HTTP {(int)response.StatusCode}: {Truncate(text, 500)}", ParseRetryAfter(response), 1);
            }

            var chat = await response.Content.ReadFromJsonAsync<ChatResponse>(cts.Token);
            var usage = chat?.Usage is { } u
                ? new AiTokenUsage(u.PromptTokens, u.CompletionTokens, u.TotalTokens, u.Cost, chat.Id)
                : chat?.Id is not null ? new AiTokenUsage(null, null, null, null, chat.Id) : null;
            var content = chat?.Choices?.FirstOrDefault()?.Message?.Content;
            if (string.IsNullOrWhiteSpace(content))
            {
                return new ChatCallResult(false, null, chat?.Model, usage, ClassificationFailureCategory.MalformedResponse, "OpenRouter returned an empty response.", null, 1);
            }
            return new ChatCallResult(true, content, chat?.Model, usage, ClassificationFailureCategory.None, null, null, 1);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ChatCallResult(false, null, null, null, ClassificationFailureCategory.Transient,
                $"OpenRouter request timed out after {timeout.TotalSeconds:0}s.", null, 1);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "OpenRouter response body was not valid JSON for model {Model}", modelForLog);
            return new ChatCallResult(false, null, null, null, ClassificationFailureCategory.MalformedResponse,
                $"OpenRouter response was not valid JSON: {ex.Message}", null, 1);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OpenRouter request threw for model {Model}", modelForLog);
            return new ChatCallResult(false, null, null, null, ClassificationFailureCategory.Transient,
                $"OpenRouter request failed: {ex.Message}", null, 1);
        }
    }

    internal static ClassificationFailureCategory Categorize(HttpStatusCode status) => status switch
    {
        HttpStatusCode.TooManyRequests => ClassificationFailureCategory.RateLimited,
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ClassificationFailureCategory.AuthenticationFailure,
        HttpStatusCode.RequestTimeout => ClassificationFailureCategory.Transient,
        >= HttpStatusCode.InternalServerError => ClassificationFailureCategory.Transient,
        _ => ClassificationFailureCategory.InvalidRequest,
    };

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null) return null;
        if (retryAfter.Delta.HasValue) return retryAfter.Delta.Value;
        if (retryAfter.Date.HasValue) return retryAfter.Date.Value - DateTimeOffset.UtcNow;
        return null;
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try { return await response.Content.ReadAsStringAsync(cancellationToken); }
        catch { return "(could not read response body)"; }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private sealed class ChatResponse
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("model")] public string? Model { get; set; }
        [JsonPropertyName("choices")] public List<Choice>? Choices { get; set; }
        [JsonPropertyName("usage")] public UsageBlock? Usage { get; set; }
    }

    private sealed class Choice
    {
        [JsonPropertyName("message")] public Msg? Message { get; set; }
    }

    private sealed class Msg
    {
        [JsonPropertyName("content")] public string? Content { get; set; }
    }

    private sealed class UsageBlock
    {
        [JsonPropertyName("prompt_tokens")] public int? PromptTokens { get; set; }
        [JsonPropertyName("completion_tokens")] public int? CompletionTokens { get; set; }
        [JsonPropertyName("total_tokens")] public int? TotalTokens { get; set; }
        [JsonPropertyName("cost")] public decimal? Cost { get; set; }
    }
}
