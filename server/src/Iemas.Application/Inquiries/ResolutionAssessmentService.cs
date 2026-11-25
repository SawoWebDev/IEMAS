using Iemas.Application.AiUsage;
using Iemas.Application.Common.Ai;
using Iemas.Application.Common.Interfaces;
using Iemas.Domain.Ai;
using Iemas.Domain.Cases;
using Iemas.Domain.Email;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Inquiries;

/// <summary>
/// Phase 5 - asks the AI, whenever an inquiry's conversation has changed, whether the customer's
/// concern appears addressed. Distinguishes acknowledgement from resolution: "we'll look into it" is
/// AcknowledgedOnly. Only an AppearsResolved verdict at or above the configured confidence moves the
/// inquiry to Potentially resolved and asks the agent to confirm - nothing here ever closes an inquiry.
/// </summary>
public class ResolutionAssessmentService
{
    /// <summary>After a failed AI call an inquiry is left alone this long, so a missing key or an outage is not retried every minute for every inquiry.</summary>
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(10);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTimeOffset> LastFailure = new();

    /// <summary>Primary plus fallbacks tried for one assessment before giving up (no verdict; the inquiry stays supervised).</summary>
    public const int MaxModelsTried = 3;

    private const int MaxCharsPerTurn = 2500;
    private const int MaxConversationChars = 14000;

    private readonly IAppDbContext _db;
    private readonly IAiResolutionAssessor _assessor;
    private readonly InquiryConversationService _conversation;
    private readonly AgentReplyProtector _protector;
    private readonly IAiUsageRecorder? _usage;
    private readonly AiCircuitBreakerStore? _breaker;

    public ResolutionAssessmentService(
        IAppDbContext db, IAiResolutionAssessor assessor, InquiryConversationService conversation, AgentReplyProtector protector, IAiUsageRecorder? usage = null,
        AiCircuitBreakerStore? breaker = null)
    {
        _breaker = breaker;
        _db = db;
        _assessor = assessor;
        _conversation = conversation;
        _protector = protector;
        _usage = usage;
    }

    /// <summary>Assesses up to <paramref name="max"/> inquiries whose conversation changed since their last assessment.</summary>
    public async Task<int> AssessChangedConversationsAsync(SupervisionSettings settings, int max, CancellationToken cancellationToken, DateTimeOffset? at = null)
    {
        var candidates = await _db.Cases
            .Where(c => !c.IsPractice && c.FirstAgentResponseAt != null
                && c.Stage != InquiryStage.Resolved && c.Stage != InquiryStage.Closed && c.Stage != InquiryStage.AwaitingInitialResponse
                && _db.CaseEmails.Count(ce => ce.CaseId == c.Id) != c.ResolutionAssessedMessageCount)
            .OrderBy(c => c.ResolutionAssessedAt ?? DateTimeOffset.MinValue)
            .Take(max)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        var assessed = 0;
        var clock = at ?? DateTimeOffset.UtcNow;
        foreach (var caseId in candidates)
        {
            if (LastFailure.TryGetValue(caseId, out var failedAt) && clock - failedAt < FailureBackoff) continue;
            if (await AssessAsync(caseId, settings, cancellationToken, at)) assessed++;
        }
        return assessed;
    }

    public async Task<bool> AssessAsync(Guid caseId, SupervisionSettings settings, CancellationToken cancellationToken, DateTimeOffset? at = null)
    {
        var now = at ?? DateTimeOffset.UtcNow;
        var c = await _db.Cases.FirstOrDefaultAsync(x => x.Id == caseId, cancellationToken);
        if (c is null || InquiryLifecycle.IsTerminal(c.Stage)) return false;

        // The primary model first, then fallbacks by FallbackOrder (bounded). Models whose circuit is open are skipped.
        var models = await _db.AiModelConfigs.AsNoTracking()
            .Where(m => m.Enabled && m.TaskCapability == "EmailClassification")
            .OrderBy(m => m.FallbackOrder)
            .Take(MaxModelsTried + 2)
            .ToListAsync(cancellationToken);
        if (models.Count == 0) return false;

        var messages = await _db.CaseEmails.AsNoTracking()
            .Where(ce => ce.CaseId == caseId)
            .OrderBy(ce => ce.EmailMessage.ReceivedAt)
            .Select(ce => new { ce.Role, ce.EmailMessage.Direction, ce.EmailMessage.ReceivedAt, ce.EmailMessage.Subject, ce.EmailMessage.BodyText, ce.EmailMessage.BodyTextEncrypted, ce.EmailMessage.MessageId, ce.EmailMessage.InReplyTo, ce.EmailMessageId })
            .ToListAsync(cancellationToken);

        // The Inbox is read every minute, the Sent folder less often. If the customer is answering a message
        // this inquiry does not have yet (normally the agent's latest reply), judging now would mean judging
        // without the agent's answer. Wait for the Sent folder to catch up - but never for long.
        if (MissingRepliedToMessage(messages.Select(m => (m.Role, m.Direction, m.ReceivedAt, m.MessageId, m.InReplyTo)).ToList(), now)) return false;

        var turns = messages.Select(m => new ConversationTurn(
            m.Role.ToString(), m.ReceivedAt,
            m.Role == ConversationRole.Internal
                ? $"(internal message, not shown to the customer: \"{m.Subject}\")"
                : Clip(m.Direction == MessageDirection.Outbound ? _protector.Reveal(m.BodyTextEncrypted) ?? "(text no longer stored)" : AgentReplyProtector.Sanitize(m.BodyText))))
            .ToList();

        // Keep the most recent turns when the conversation is long; the latest state matters most.
        while (turns.Count > 1 && turns.Sum(t => t.Text.Length) > MaxConversationChars) turns.RemoveAt(1);

        var latestEmailId = messages.Count > 0 ? messages[^1].EmailMessageId : (Guid?)null;
        var request = new ResolutionAssessmentRequest(caseId, c.Subject, turns);

        ResolutionAssessmentResult? result = null;
        AiModelConfig? usedModel = null;
        var usedFallback = false;
        var tried = 0;
        foreach (var model in models)
        {
            if (tried >= MaxModelsTried) break;
            var acquired = _breaker?.TryAcquire(model.Provider, model.ModelIdentifier, out _) ?? true;
            if (!acquired) continue; // circuit open: do not spend a call on a model known to be failing
            var isFallback = tried > 0 || model.FallbackOrder > models[0].FallbackOrder;
            tried++;

            ResolutionAssessmentResult attempt;
            try
            {
                attempt = await _assessor.AssessAsync(request, model.ModelIdentifier, TimeSpan.FromSeconds(Math.Max(1, model.TimeoutSeconds)), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _breaker?.ReportOutcome(model.Provider, model.ModelIdentifier, CircuitOutcome.UncountedFailure);
                throw;
            }
            catch (Exception ex)
            {
                // AI failure must never throw out of the pipeline: no verdict, keep supervising.
                attempt = new ResolutionAssessmentResult(false, null, 0, null, model.Provider, model.ModelIdentifier, "Assessor threw: " + ex.Message, 0,
                    FailureCategory: ClassificationFailureCategory.Transient);
            }

            var ok = attempt.Succeeded && attempt.Verdict is not null;
            _breaker?.ReportOutcome(model.Provider, model.ModelIdentifier, ok ? CircuitOutcome.Success
                : attempt.FailureCategory is ClassificationFailureCategory.AuthenticationFailure or ClassificationFailureCategory.InvalidRequest ? CircuitOutcome.UncountedFailure
                : CircuitOutcome.CountedFailure);

            if (ok)
            {
                result = attempt;
                usedModel = model;
                usedFallback = isFallback;
                break;
            }
            await RecordAsync(attempt, model, caseId, latestEmailId, null, null, isFallback);
        }

        // A failed call is retried later (after the backoff): the message count is only recorded on success.
        if (result is null || usedModel is null || result.Verdict is not ResolutionVerdict aiVerdict)
        {
            LastFailure[caseId] = now;
            return false;
        }
        LastFailure.TryRemove(caseId, out _);

        // Deterministic guard: code, not the prompt, decides whether "appears resolved" is believable.
        var guard = ResolutionGuard.Apply(aiVerdict, turns, result.OpenItems);
        var verdict = guard.Verdict;
        var reason = result.Reason ?? string.Empty;
        if (result.OpenItems is { Count: > 0 }) reason += " Open items: " + string.Join("; ", result.OpenItems.Take(5)) + ".";
        if (guard.Downgraded) reason += $" Not proposed as resolved ({Describe(aiVerdict)} -> {Describe(verdict)}): " + string.Join(" ", guard.Findings);

        await RecordAsync(result, usedModel, caseId, latestEmailId,
            guard.Downgraded ? $"{aiVerdict} -> {verdict} (guard)" : verdict.ToString(), reason, usedFallback);

        c.ResolutionVerdict = verdict;
        c.ResolutionConfidence = result.Confidence;
        c.ResolutionReason = reason.Length > 1000 ? reason[..1000] : reason;
        c.ResolutionAssessedAt = now;
        c.ResolutionAssessedMessageCount = messages.Count;
        _db.CaseEvents.Add(new CaseEvent
        {
            CaseId = c.Id,
            EventType = CaseEventType.ResolutionAssessment,
            OccurredAt = now,
            Detail = $"AI assessment: {Describe(verdict)} (confidence {result.Confidence:P0}). {c.ResolutionReason}",
        });
        await _db.SaveChangesAsync(cancellationToken);

        if (verdict == ResolutionVerdict.AppearsResolved && result.Confidence * 100 >= settings.AiResolutionMinConfidencePercent)
        {
            await _conversation.MarkPotentiallyResolvedAsync(c,
                $"The AI judged the customer's concern addressed ({result.Confidence:P0}): {c.ResolutionReason} The agent must confirm.", now, cancellationToken);
        }
        return true;
    }

    /// <summary>Audit row for one assessment call: case id + latest email id, model + provider-reported version, prompt version, decision, tokens, cost, fallback.</summary>
    private async Task RecordAsync(ResolutionAssessmentResult r, AiModelConfig model, Guid caseId, Guid? emailId, string? decision, string? explanation, bool fallback)
    {
        if (_usage is null) return;
        var text = explanation ?? r.Reason;
        await _usage.RecordAsync(new AiUsageRecord
        {
            Provider = r.Provider, ModelIdentifier = model.ModelIdentifier, Purpose = AiUsagePurpose.ResolutionAssessment, CaseId = caseId, EmailMessageId = emailId,
            Succeeded = r.Succeeded, ErrorMessage = r.Error is { Length: > 1000 } e ? e[..1000] : r.Error, DurationMs = r.DurationMs,
            PromptTokens = r.Usage?.PromptTokens, CompletionTokens = r.Usage?.CompletionTokens, TotalTokens = r.Usage?.TotalTokens,
            CostUsd = r.Usage?.CostUsd, GenerationId = r.Usage?.GenerationId,
            ModelVersion = r.ModelVersion, PromptVersion = r.PromptVersion, Decision = decision ?? (r.Succeeded ? r.Verdict?.ToString() : null),
            Confidence = r.Succeeded ? r.Confidence : null,
            Explanation = text is { Length: > 1000 } ? text[..1000] : text,
            FallbackUsed = fallback, AttemptCount = r.Attempts,
            FailureCategory = r.Succeeded || r.FailureCategory == ClassificationFailureCategory.None ? null : r.FailureCategory.ToString(),
        }, CancellationToken.None);
    }

    public static string Describe(ResolutionVerdict verdict) => verdict switch
    {
        ResolutionVerdict.AcknowledgedOnly => "acknowledged only, concern still open",
        ResolutionVerdict.InProgress => "in progress, not yet resolved",
        ResolutionVerdict.AppearsResolved => "appears resolved",
        ResolutionVerdict.CustomerWaiting => "customer is waiting on the agent",
        _ => verdict.ToString(),
    };

    /// <summary>How long the assessment waits for a replied-to message to be recorded before judging anyway.</summary>
    public static readonly TimeSpan MissingMessageGrace = TimeSpan.FromMinutes(15);

    /// <summary>True while the latest customer message replies to a message this inquiry does not hold yet (and is recent).</summary>
    public static bool MissingRepliedToMessage(
        IReadOnlyList<(ConversationRole Role, MessageDirection Direction, DateTimeOffset At, string? MessageId, string? InReplyTo)> messages, DateTimeOffset now)
    {
        var latestCustomer = messages.LastOrDefault(m => m.Role == ConversationRole.Customer && m.Direction == MessageDirection.Inbound);
        if (latestCustomer.InReplyTo is not { Length: > 0 } repliedTo) return false;
        if (now - latestCustomer.At > MissingMessageGrace) return false;
        static string Norm(string id) => id.Trim().Trim('<', '>');
        return !messages.Any(m => m.MessageId is { Length: > 0 } id && string.Equals(Norm(id), Norm(repliedTo), StringComparison.OrdinalIgnoreCase));
    }

    private static string Clip(string? text)
    {
        var t = (text ?? string.Empty).Trim();
        return t.Length > MaxCharsPerTurn ? t[..MaxCharsPerTurn] + " ..." : t;
    }
}
