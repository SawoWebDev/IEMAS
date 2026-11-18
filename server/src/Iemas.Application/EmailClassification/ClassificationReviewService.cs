using Iemas.Application.Cases;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.EmailClassification.Dtos;
using Iemas.Domain.Ai;
using Iemas.Domain.Email;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.EmailClassification;

/// <summary>
/// Manual review of emails the AI/policy marked ReviewRequired (uncertain, or the AI failed). Such an
/// email is never retried by the classification job and never turns into a Case by itself; a person
/// decides here:
///  - Approve: it is an inquiry. Decision becomes Important and the message goes through the normal
///    <see cref="CaseWorkflowService.ProcessOneAsync"/> path (new Case or join the existing one, notify
///    the owner), exactly like an AI-confirmed inquiry.
///  - Reject: Decision becomes NotImportant, no Case.
/// The AI's own values (relevance, category, priority, confidence, reason) are kept untouched; the
/// reviewer's corrections go in the Corrected* fields. Who/when/what/note is recorded on the
/// classification and in the audit log. A review can happen only once (a second one is a Conflict).
/// </summary>
public class ClassificationReviewService
{
    /// <summary>A review waiting longer than this is flagged as stale in the queue and the count.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);

    private readonly IAppDbContext _db;
    private readonly CaseWorkflowService _caseWorkflow;
    private readonly IAuditService _audit;
    private readonly ICurrentUserService _currentUser;

    public ClassificationReviewService(
        IAppDbContext db, CaseWorkflowService caseWorkflow, IAuditService audit, ICurrentUserService currentUser)
    {
        _db = db;
        _caseWorkflow = caseWorkflow;
        _audit = audit;
        _currentUser = currentUser;
    }

    private IQueryable<Iemas.Domain.Ai.EmailClassification> PendingQuery() =>
        _db.EmailClassifications.Where(c => c.Decision == ImportanceDecision.ReviewRequired
            && c.ReviewedAt == null
            && c.EmailMessage.ProcessingStatus == EmailProcessingStatus.ReviewRequired
            && (c.EmailMessage.EmailAccount.ProcessEmailsReceivedAfter == null
                || c.EmailMessage.ReceivedAt >= c.EmailMessage.EmailAccount.ProcessEmailsReceivedAfter));

    public async Task<PendingReviewCountDto> GetPendingCountAsync(CancellationToken cancellationToken)
    {
        var staleBefore = DateTimeOffset.UtcNow - StaleAfter;
        var pending = await PendingQuery().CountAsync(cancellationToken);
        var stale = await PendingQuery().CountAsync(c => c.ClassifiedAt < staleBefore, cancellationToken);
        return new PendingReviewCountDto(pending, stale, (int)StaleAfter.TotalHours);
    }

    /// <summary>Oldest first, so nothing sits at the bottom of the queue forever.</summary>
    public async Task<IReadOnlyList<PendingReviewDto>> ListPendingAsync(int take, CancellationToken cancellationToken)
    {
        take = Math.Clamp(take, 1, 200);
        var staleBefore = DateTimeOffset.UtcNow - StaleAfter;

        var rows = await PendingQuery()
            .AsNoTracking()
            .OrderBy(c => c.ClassifiedAt)
            .Take(take)
            .Select(c => new
            {
                c.EmailMessageId,
                c.EmailMessage.Subject,
                c.EmailMessage.FromAddress,
                c.EmailMessage.FromDisplayName,
                Mailbox = c.EmailMessage.EmailAccount.EmailAddress,
                c.EmailMessage.ReceivedAt,
                c.EmailMessage.BodyText,
                c.Relevance,
                c.Category,
                c.Priority,
                c.AiConfidence,
                c.Summary,
                c.DecisionReason,
                c.DeterministicFilterReason,
                c.ProcessingError,
                c.ClassifiedAt,
            })
            .ToListAsync(cancellationToken);

        return rows.Select(r => new PendingReviewDto(
            r.EmailMessageId, r.Subject, r.FromAddress, r.FromDisplayName, r.Mailbox, r.ReceivedAt,
            Snippet(r.BodyText),
            r.Relevance?.ToString(), r.Category, r.Priority?.ToString(), r.AiConfidence, r.Summary,
            r.DecisionReason, r.DeterministicFilterReason, r.ProcessingError, r.ClassifiedAt,
            r.ClassifiedAt < staleBefore)).ToList();
    }

    public async Task<ClassificationReviewResult> ReviewAsync(
        Guid emailMessageId, ReviewClassificationRequest request, CancellationToken cancellationToken)
    {
        if (!TryParseDecision(request.Decision, out var decision))
        {
            return Fail(ClassificationReviewStatus.Invalid, "Decision must be 'Approve' or 'Reject'.");
        }

        ClassificationRelevance? correctedRelevance = null;
        if (!string.IsNullOrWhiteSpace(request.CorrectedRelevance))
        {
            if (!Enum.TryParse<ClassificationRelevance>(request.CorrectedRelevance, true, out var r) || r == ClassificationRelevance.Uncertain)
            {
                return Fail(ClassificationReviewStatus.Invalid, "Corrected relevance must be 'Relevant' or 'NotRelevant'.");
            }
            correctedRelevance = r;
        }

        ClassificationPriority? correctedPriority = null;
        if (!string.IsNullOrWhiteSpace(request.CorrectedPriority))
        {
            if (!Enum.TryParse<ClassificationPriority>(request.CorrectedPriority, true, out var p) || !Enum.IsDefined(p))
            {
                return Fail(ClassificationReviewStatus.Invalid, "Corrected priority must be 'Low', 'Medium' or 'High'.");
            }
            correctedPriority = p;
        }

        if (decision == ClassificationReviewDecision.Approved && correctedRelevance == ClassificationRelevance.NotRelevant)
        {
            return Fail(ClassificationReviewStatus.Invalid, "An email corrected to 'NotRelevant' cannot be approved as an inquiry. Reject it instead.");
        }
        if (decision == ClassificationReviewDecision.Rejected && correctedRelevance == ClassificationRelevance.Relevant)
        {
            return Fail(ClassificationReviewStatus.Invalid, "An email corrected to 'Relevant' cannot be rejected. Approve it instead.");
        }

        var classification = await _db.EmailClassifications
            .FirstOrDefaultAsync(c => c.EmailMessageId == emailMessageId, cancellationToken);
        var message = await _db.EmailMessages.FirstOrDefaultAsync(m => m.Id == emailMessageId, cancellationToken);
        if (classification is null || message is null)
        {
            return Fail(ClassificationReviewStatus.NotFound, "Email not found.");
        }

        if (classification.ReviewedAt is not null
            || classification.Decision != ImportanceDecision.ReviewRequired
            || message.ProcessingStatus != EmailProcessingStatus.ReviewRequired)
        {
            return Fail(ClassificationReviewStatus.Conflict, "This email is not waiting for review (already reviewed or decided).");
        }

        var note = string.IsNullOrWhiteSpace(request.Note) ? null : Truncate(request.Note.Trim(), 2000);
        var corrected = correctedRelevance is not null || correctedPriority is not null || !string.IsNullOrWhiteSpace(request.CorrectedCategory);

        // Original AI values, captured for the audit entry before anything changes.
        var original = $"AI: relevance={classification.Relevance}, category={classification.Category}, priority={classification.Priority}, confidence={classification.AiConfidence:0.##}; reason: {Truncate(classification.DecisionReason ?? "", 300)}";

        if (corrected)
        {
            classification.IsManuallyCorrected = true;
            classification.CorrectedRelevance = correctedRelevance;
            classification.CorrectedCategory = string.IsNullOrWhiteSpace(request.CorrectedCategory) ? null : Truncate(request.CorrectedCategory.Trim(), 200);
            classification.CorrectedPriority = correctedPriority;
        }

        classification.ReviewDecision = decision;
        classification.ReviewedByUserId = _currentUser.UserId;
        classification.ReviewedByEmail = _currentUser.Email;
        classification.ReviewedAt = DateTimeOffset.UtcNow;
        classification.ReviewNote = note;

        if (decision == ClassificationReviewDecision.Approved)
        {
            classification.Decision = ImportanceDecision.Important;
            message.ProcessingStatus = EmailProcessingStatus.Processed;
            message.Classification = classification.CorrectedCategory ?? classification.Category ?? message.Classification ?? "Inquiry";
        }
        else
        {
            classification.Decision = ImportanceDecision.NotImportant;
            message.ProcessingStatus = EmailProcessingStatus.Processed;
            message.Classification = "NOT_RELEVANT";
        }
        message.ProcessingError = null;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Fail(ClassificationReviewStatus.Conflict, "This email was just reviewed by someone else.");
        }

        await _audit.LogAsync(
            decision == ClassificationReviewDecision.Approved ? "CLASSIFICATION_REVIEW_APPROVED" : "CLASSIFICATION_REVIEW_REJECTED",
            "EmailMessage", emailMessageId.ToString(),
            $"Reviewed by {_currentUser.Email}. {original}. Corrections: relevance={correctedRelevance?.ToString() ?? "-"}, category={classification.CorrectedCategory ?? "-"}, priority={correctedPriority?.ToString() ?? "-"}. Note: {note ?? "-"}",
            cancellationToken);

        var caseOutcome = "NoCase";
        string? caseNumber = null;
        if (decision == ClassificationReviewDecision.Approved)
        {
            // Same path as an AI-confirmed inquiry: create or join the Case and notify the owner.
            // If this throws, the message is already Processed+Important, so the regular case job
            // picks it up on its next run - the approval is never lost.
            var outcome = await _caseWorkflow.ProcessOneAsync(emailMessageId, cancellationToken);
            caseOutcome = outcome switch
            {
                CaseWorkflowService.CaseProcessOutcome.Created => "NewCase",
                CaseWorkflowService.CaseProcessOutcome.Updated => "JoinedExistingCase",
                CaseWorkflowService.CaseProcessOutcome.Reopened => "ReopenedCase",
                _ => "NoCase",
            };

            var caseId = await _db.EmailMessages.AsNoTracking().Where(m => m.Id == emailMessageId).Select(m => m.CaseId).FirstOrDefaultAsync(cancellationToken);
            if (caseId is not null)
            {
                caseNumber = await _db.Cases.AsNoTracking().Where(c => c.Id == caseId).Select(c => c.CaseNumber).FirstOrDefaultAsync(cancellationToken);
            }
        }

        return new ClassificationReviewResult(ClassificationReviewStatus.Succeeded, null,
            new ReviewClassificationResultDto(emailMessageId, decision.ToString(), caseOutcome, caseNumber));
    }

    private static bool TryParseDecision(string? value, out ClassificationReviewDecision decision)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "approve" or "approved":
                decision = ClassificationReviewDecision.Approved;
                return true;
            case "reject" or "rejected":
                decision = ClassificationReviewDecision.Rejected;
                return true;
            default:
                decision = default;
                return false;
        }
    }

    private static ClassificationReviewResult Fail(ClassificationReviewStatus status, string error) => new(status, error, null);

    private static string Snippet(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        var collapsed = string.Join(' ', body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= 300 ? collapsed : collapsed[..300] + "...";
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
