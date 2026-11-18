namespace Iemas.Infrastructure.Ai;

/// <summary>
/// Requirements §82 — non-secret AI configuration (base URL, default timeout) lives in
/// appsettings; the API key is supplied only via environment/secret configuration (§16 — "AI
/// provider keys" must never be hardcoded or exposed), matching the
/// <see cref="Iemas.Infrastructure.Security.CredentialEncryptionOptions"/> precedent.
/// </summary>
public class AiClassificationOptions
{
    public const string SectionName = "AiClassification";

    public bool Enabled { get; set; } = true;
    public string CronSchedule { get; set; } = "*/2 * * * *";
    public int BatchSize { get; set; } = 25;

    public OpenRouterOptions OpenRouter { get; set; } = new();
}

public class OpenRouterOptions
{
    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1";

    /// <summary>Never logged, never returned by any API response (§16).</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>OpenRouter uses this for its own analytics/rate-limit attribution; not secret.</summary>
    public string? SiteUrl { get; set; }
    public string? SiteName { get; set; }

    /// <summary>
    /// Extra attempts the resolution assessor makes itself after a timeout, network error, HTTP 5xx or 429
    /// (backoff via IRetryDelay; Retry-After honoured). Classification does NOT retry inside the provider
    /// because EmailClassificationService already runs its own per-model retry loop; see
    /// <see cref="ClassificationTransportRetries"/>.
    /// </summary>
    public int AssessmentMaxRetries { get; set; } = 2;

    /// <summary>Provider-level transport retries for classification. Default 0: the service layer owns that loop (avoids multiplying retries).</summary>
    public int ClassificationTransportRetries { get; set; }

    /// <summary>First backoff step; doubles per retry.</summary>
    public int RetryBaseDelayMilliseconds { get; set; } = 1000;

    /// <summary>Upper bound for any single backoff or Retry-After wait.</summary>
    public int MaxRetryDelaySeconds { get; set; } = 30;

    /// <summary>When the model returns malformed or out-of-range JSON, send ONE corrective follow-up asking it to re-emit valid JSON.</summary>
    public bool RepairMalformedOutput { get; set; } = true;
}
