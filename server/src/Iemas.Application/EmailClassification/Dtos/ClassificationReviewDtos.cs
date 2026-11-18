namespace Iemas.Application.EmailClassification.Dtos;

/// <summary>One email waiting for a human decision, with everything the reviewer needs to decide.</summary>
public sealed record PendingReviewDto(
    Guid EmailMessageId,
    string Subject,
    string FromAddress,
    string? FromDisplayName,
    string Mailbox,
    DateTimeOffset ReceivedAt,
    string Snippet,
    string? AiRelevance,
    string? AiCategory,
    string? AiPriority,
    double? AiConfidence,
    string? AiSummary,
    string? DecisionReason,
    string? FilterScore,
    string? ProcessingError,
    DateTimeOffset ClassifiedAt,
    bool IsStale);

public sealed record PendingReviewCountDto(int Pending, int Stale, int StaleAfterHours);

public sealed record ReviewClassificationRequest(
    // "Approve" (it is an inquiry) or "Reject" (it is not).
    string Decision,
    string? Note,
    // Optional corrections, recorded next to (never over) the AI values.
    string? CorrectedRelevance,
    string? CorrectedCategory,
    string? CorrectedPriority);

public sealed record ReviewClassificationResultDto(
    Guid EmailMessageId,
    string Decision,
    // NewCase, JoinedExistingCase, ReopenedCase, NoCase.
    string CaseOutcome,
    string? CaseNumber);

public enum ClassificationReviewStatus
{
    Succeeded,
    NotFound,
    /// <summary>Already reviewed, or no longer waiting for review. Maps to HTTP 409.</summary>
    Conflict,
    Invalid,
}

public sealed record ClassificationReviewResult(
    ClassificationReviewStatus Status, string? Error, ReviewClassificationResultDto? Value)
{
    public bool Succeeded => Status == ClassificationReviewStatus.Succeeded;
}
