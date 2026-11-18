using Iemas.Domain.Common;

namespace Iemas.Domain.Ai;

/// <summary>
/// One AI provider API call, for cost monitoring. Every attempt is recorded — including retries,
/// fallbacks and failures — because a failed call can still consume tokens. Never stores email
/// content.
/// </summary>
public class AiUsageRecord : Entity
{
    public string Provider { get; set; } = string.Empty;
    public string ModelIdentifier { get; set; } = string.Empty;

    /// <summary>EmailClassification, ModelTest or ProfileTest.</summary>
    public string Purpose { get; set; } = string.Empty;
    public Guid? EmailMessageId { get; set; }

    /// <summary>The inquiry this call was made for, when it belongs to one (resolution checks); classification calls are tied to their email instead.</summary>
    public Guid? CaseId { get; set; }

    public bool Succeeded { get; set; }
    public string? ErrorMessage { get; set; }
    public long DurationMs { get; set; }

    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public int? TotalTokens { get; set; }

    /// <summary>USD as reported by the provider; null when the provider did not report a cost.</summary>
    public decimal? CostUsd { get; set; }
    public string? GenerationId { get; set; }

    // ---- AI audit trail (all additive and nullable: rows written before this existed simply have no values) ----

    /// <summary>The model id the provider reported it actually served (may differ from the requested alias); null when not reported.</summary>
    public string? ModelVersion { get; set; }

    /// <summary>Version of the prompt that produced this decision (see the PromptVersion constant next to each prompt).</summary>
    public string? PromptVersion { get; set; }

    /// <summary>Short outcome, e.g. "relevant/customer_inquiry" or "AppearsResolved -> InProgress (guard)".</summary>
    public string? Decision { get; set; }
    public double? Confidence { get; set; }

    /// <summary>The model's one-or-two-sentence reason (plus any deterministic-guard note). Never the email text.</summary>
    public string? Explanation { get; set; }

    /// <summary>True when this call was served by a fallback model rather than the primary.</summary>
    public bool? FallbackUsed { get; set; }

    /// <summary>HTTP/model attempts used by this call (retries and repair).</summary>
    public int? AttemptCount { get; set; }

    /// <summary>ClassificationFailureCategory name for a failed call.</summary>
    public string? FailureCategory { get; set; }
}
