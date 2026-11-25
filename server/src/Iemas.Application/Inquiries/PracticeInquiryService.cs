using Iemas.Application.Common;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.Escalations;
using Iemas.Application.Notifications;
using Iemas.Application.Operations;
using Iemas.Domain.Cases;
using Iemas.Domain.Email;
using Iemas.Domain.Identity;
using Iemas.Domain.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Inquiries;

/// <param name="Step">0 new inquiry, 1 reminder, 2 last reminder, 3 escalated.</param>
/// <param name="Finished">True when there is no further step: escalated, replied or closed.</param>
public record PracticeStateDto(
    Guid CaseId, string CaseNumber, int Step, string Title, string Explanation, bool Finished, bool SampleEmailSent);

/// <summary>
/// The practice run of the Agent's first-time setup. It makes one made-up inquiry for the employee and walks
/// it through the same pop-ups a real one would raise, up to escalation, as fast as the employee clicks
/// "next". The employee receives the sample inquiry, and at the end a copy of the email a supervisor would
/// get, in their own mailbox. Nobody else is told anything, no timer touches it, reports leave it out and it
/// is deleted when the practice ends (or after an hour).
/// </summary>
public class PracticeInquiryService
{
    public const string CaseNumberPrefix = "PRACTICE-";
    public const string CustomerAddress = "sample.customer@practice.invalid";
    public const string CustomerName = "Sample Customer (practice)";
    public const string Subject = "[PRACTICE] Price for a 9 kW sauna heater";
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(1);

    private const int LastStep = 3;

    private readonly IAppDbContext _db;
    private readonly NotificationService _notifications;
    private readonly IEscalationEmailSender _sender;
    private readonly IAgentNotificationDispatcher? _dispatcher;
    private readonly TimeProvider _time;

    public PracticeInquiryService(IAppDbContext db, NotificationService notifications, IEscalationEmailSender sender,
        IAgentNotificationDispatcher? dispatcher = null, TimeProvider? time = null)
    {
        _db = db;
        _notifications = notifications;
        _sender = sender;
        _dispatcher = dispatcher;
        _time = time ?? TimeProvider.System;
    }

    public async Task<Result<PracticeStateDto>> StartAsync(Guid employeeId, CancellationToken cancellationToken)
    {
        var employee = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == employeeId && e.IsActive, cancellationToken);
        if (employee is null) return Result<PracticeStateDto>.Failure("Employee not found or inactive.");

        var accounts = await _db.EmailAccounts.AsNoTracking()
            .Where(a => a.OwnerEmployeeId == employeeId && a.Purpose == EmailAccountPurpose.Inbound && a.IsActive)
            .ToListAsync(cancellationToken);
        var account = accounts.FirstOrDefault(a => string.Equals(a.EmailAddress, employee.Email, StringComparison.OrdinalIgnoreCase))
            ?? accounts.OrderBy(a => a.EmailAddress).FirstOrDefault();
        if (account is null)
        {
            return Result<PracticeStateDto>.Failure("No active mailbox belongs to you in IEMAS yet, so there is nothing to practise with.");
        }

        // One practice at a time per person.
        await RemoveAsync(_db, await _db.Cases.Where(c => c.IsPractice && c.OwnerEmployeeId == employeeId).Select(c => c.Id).ToListAsync(cancellationToken), cancellationToken);

        var now = _time.GetUtcNow();
        var body = $"Hello {employee.FullName},\n\n"
            + "I am planning a small sauna and would like the price and delivery time for one 9 kW heater.\n\n"
            + "Best regards,\nSample Customer\n\n"
            + "----\nThis is a practice inquiry from IEMAS. No customer is waiting. You do not have to answer it.";
        var sample = await _sender.SendAsync(new OutboundEmail(
            new[] { new EmailRecipient(employee.FullName, account.EmailAddress) }, Subject, body), cancellationToken);

        var id = Guid.NewGuid();
        var practice = new Case
        {
            CaseNumber = CaseNumberPrefix + id.ToString("N")[..6].ToUpperInvariant(),
            IsPractice = true,
            EmailAccountId = account.Id,
            OwnerEmployeeId = employeeId,
            CustomerEmailAddress = CustomerAddress,
            CustomerDisplayName = CustomerName,
            Subject = Subject,
            NormalizedSubject = Cases.CaseMatchingService.NormalizeSubject(Subject),
            WorkStatus = CaseWorkStatus.ActionRequired,
            ReplyStatus = CaseReplyStatus.AwaitingReply,
            Stage = InquiryStage.AwaitingInitialResponse,
            StageChangedAt = now,
            SupervisionCycleStartedAt = now,
            ResolutionDueAt = now.AddDays(14),
            FirstEmailReceivedAt = now,
            LastActivityAt = now,
            LastCustomerMessageAt = now,
        };
        var message = new EmailMessage
        {
            EmailAccountId = account.Id,
            Provider = account.Protocol,
            ProviderMessageId = "practice-" + id.ToString("N"),
            // The sent sample's own Message-ID, so a reply to it from Outlook is recognised like any reply.
            MessageId = sample.MessageId ?? $"<practice-{id:N}@iemas.invalid>",
            FromAddress = CustomerAddress,
            FromDisplayName = CustomerName,
            ToAddresses = account.EmailAddress,
            Subject = Subject,
            BodyText = body,
            ReceivedAt = now,
            ProcessingStatus = EmailProcessingStatus.Processed,
            Classification = "Practice",
            CaseId = practice.Id,
        };
        _db.Cases.Add(practice);
        _db.EmailMessages.Add(message);
        _db.CaseEmails.Add(new CaseEmail { CaseId = practice.Id, EmailMessageId = message.Id, Role = ConversationRole.Customer, MatchSignal = CaseMatchSignal.NewCase });
        AddEvent(practice, CaseEventType.Created, "Practice inquiry started from the employee's PC.", now);
        await _db.SaveChangesAsync(cancellationToken);

        await _notifications.SendForCaseAsync(NotificationType.NewEmail, practice, AgentPushCommandType.ShowCase, 0, cancellationToken);

        return Result<PracticeStateDto>.Success(State(practice, 0, sample.Sent));
    }

    /// <summary>Moves the practice inquiry one step on, as if nobody had answered.</summary>
    public async Task<Result<PracticeStateDto>> AdvanceAsync(Guid employeeId, Guid caseId, CancellationToken cancellationToken)
    {
        var practice = await FindAsync(employeeId, caseId, cancellationToken);
        if (practice is null) return Result<PracticeStateDto>.Failure("The practice inquiry has ended. Start it again.");

        var step = StepOf(practice);
        if (InquiryLifecycle.IsTerminal(practice.Stage) || !InquiryLifecycle.AgentOwesNextStep(practice.Stage) || step >= LastStep)
        {
            return Result<PracticeStateDto>.Success(State(practice, step, false));
        }

        var now = _time.GetUtcNow();
        var sampleSent = false;
        step++;
        if (step < LastStep)
        {
            practice.EpisodeRemindersSent = step;
            practice.EpisodeLastReminderAt = now;
            AddEvent(practice, CaseEventType.ReminderEvent, $"Practice reminder {step}.", now);
            await _db.SaveChangesAsync(cancellationToken);
            var (type, push) = step == 1
                ? (NotificationType.FirstReminder, AgentPushCommandType.ShowReminder)
                : (NotificationType.EscalationWarning, AgentPushCommandType.ShowReminder);
            await _notifications.SendForCaseAsync(type, practice, push, step, cancellationToken);
        }
        else
        {
            practice.EpisodeEscalations = 1;
            practice.EpisodeLastEscalatedAt = now;
            InquiryLifecycle.ProjectWorkStatus(practice);
            AddEvent(practice, CaseEventType.EscalationEvent, "Practice escalation. No supervisor was contacted.", now);
            await _db.SaveChangesAsync(cancellationToken);
            await _notifications.SendForCaseAsync(NotificationType.Escalation, practice, AgentPushCommandType.ShowNotification, 2, cancellationToken);
            sampleSent = await SendSupervisorSampleAsync(practice, now, cancellationToken);
        }

        return Result<PracticeStateDto>.Success(State(practice, step, sampleSent));
    }

    public async Task<Result<bool>> EndAsync(Guid employeeId, Guid caseId, CancellationToken cancellationToken)
    {
        var practice = await FindAsync(employeeId, caseId, cancellationToken);
        if (practice is not null) await RemoveAsync(_db, new List<Guid> { practice.Id }, cancellationToken);
        return Result<bool>.Success(true);
    }

    /// <summary>An administrator asks a connected PC to open the practice run.</summary>
    public async Task<Result<int>> InviteAsync(Guid agentId, CancellationToken cancellationToken)
    {
        var agent = await _db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken);
        if (agent?.EmployeeId is not Guid employeeId) return Result<int>.Failure("This PC is not approved for an employee.");

        var delivered = _dispatcher is null
            ? 0
            : await _dispatcher.NotifyEmployeeAsync(employeeId,
                new AgentPushCommand(AgentPushCommandType.StartPractice, null, null, "Practice run",
                    "Your administrator started a practice inquiry so you can see how alerts work."), cancellationToken);
        return delivered > 0
            ? Result<int>.Success(delivered)
            : Result<int>.Failure("This employee has no PC connected right now. Try again when the Agent shows Connected.");
    }

    public static async Task<int> RemoveStaleAsync(IAppDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var cutoff = now - MaxAge;
        var ids = await db.Cases.Where(c => c.IsPractice && c.CreatedAt < cutoff).Select(c => c.Id).ToListAsync(cancellationToken);
        await RemoveAsync(db, ids, cancellationToken);
        return ids.Count;
    }

    /// <summary>Removes practice inquiries and everything recorded about them. Refuses real inquiries by construction.</summary>
    private static async Task RemoveAsync(IAppDbContext db, List<Guid> caseIds, CancellationToken cancellationToken)
    {
        if (caseIds.Count == 0) return;
        var ids = await db.Cases.Where(c => c.IsPractice && caseIds.Contains(c.Id)).Select(c => c.Id).ToListAsync(cancellationToken);

        // Children before parents, in the order the foreign keys need.
        db.Reminders.RemoveRange(await db.Reminders.Where(r => ids.Contains(r.CaseId)).ToListAsync(cancellationToken));
        await db.SaveChangesAsync(cancellationToken);
        db.Notifications.RemoveRange(await db.Notifications.Where(n => n.CaseId != null && ids.Contains(n.CaseId.Value)).ToListAsync(cancellationToken));
        db.EscalationEvents.RemoveRange(await db.EscalationEvents.Where(e => ids.Contains(e.CaseId)).ToListAsync(cancellationToken));
        db.AgentCaseActions.RemoveRange(await db.AgentCaseActions.Where(a => ids.Contains(a.CaseId)).ToListAsync(cancellationToken));
        db.ReplyVerificationAttempts.RemoveRange(await db.ReplyVerificationAttempts.Where(a => ids.Contains(a.CaseId)).ToListAsync(cancellationToken));
        await db.SaveChangesAsync(cancellationToken);

        var links = await db.CaseEmails.Where(ce => ids.Contains(ce.CaseId)).ToListAsync(cancellationToken);
        var emailIds = links.Select(l => l.EmailMessageId).ToList();
        db.CaseEmails.RemoveRange(links);
        await db.SaveChangesAsync(cancellationToken);
        db.EmailMessages.RemoveRange(await db.EmailMessages
            .Where(m => emailIds.Contains(m.Id) || (m.CaseId != null && ids.Contains(m.CaseId.Value))).ToListAsync(cancellationToken));
        db.CaseEvents.RemoveRange(await db.CaseEvents.Where(e => ids.Contains(e.CaseId)).ToListAsync(cancellationToken));
        await db.SaveChangesAsync(cancellationToken);
        db.Cases.RemoveRange(await db.Cases.Where(c => ids.Contains(c.Id)).ToListAsync(cancellationToken));
        await db.SaveChangesAsync(cancellationToken);
    }

    private Task<Case?> FindAsync(Guid employeeId, Guid caseId, CancellationToken cancellationToken) =>
        _db.Cases.FirstOrDefaultAsync(c => c.Id == caseId && c.IsPractice && c.OwnerEmployeeId == employeeId, cancellationToken);

    private static int StepOf(Case c) => c.EpisodeEscalations > 0 ? LastStep : c.EpisodeRemindersSent;

    private async Task<bool> SendSupervisorSampleAsync(Case practice, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var employee = await _db.Employees.AsNoTracking().Include(e => e.SupervisorEmployee)
            .FirstAsync(e => e.Id == practice.OwnerEmployeeId, cancellationToken);
        var mailbox = await _db.EmailAccounts.AsNoTracking().Where(a => a.Id == practice.EmailAccountId)
            .Select(a => a.EmailAddress).FirstAsync(cancellationToken);
        var supervisor = employee.SupervisorEmployee?.FullName;
        var who = supervisor is null
            ? "In real use this email goes to your supervisor (none is set for you yet)."
            : $"In real use this email goes to your supervisor, {supervisor}.";

        var composed = EscalationEmailComposer.Compose(
            new EscalationEmailContext(practice, employee.FullName, employee.Email, 1,
                $"PRACTICE ONLY. {who} Nobody else received it.",
                new[] { now, now },
                await SystemSettingsService.GetDefaultTimeZoneAsync(_db, cancellationToken), now,
                await SystemSettingsService.GetValueAsync(_db, SystemSettingKeys.OrganizationName, cancellationToken),
                CmsBaseUrl: null, IsTest: true),
            new[] { new EmailRecipient(employee.FullName, mailbox) });
        var result = await _sender.SendAsync(composed with
        {
            Subject = "[PRACTICE] " + composed.Subject.Replace(Subject, "Price for a 9 kW sauna heater"),
            TextBody = $"THIS IS A PRACTICE EMAIL from IEMAS. No action is needed. {who}\n\n{composed.TextBody}",
        }, cancellationToken);
        return result.Sent;
    }

    private static PracticeStateDto State(Case c, int step, bool sampleSent)
    {
        if (c.EpisodeEscalations == 0 && (InquiryLifecycle.IsTerminal(c.Stage) || !InquiryLifecycle.AgentOwesNextStep(c.Stage)))
        {
            return new PracticeStateDto(c.Id, c.CaseNumber, step,
                InquiryLifecycle.IsTerminal(c.Stage) ? "You closed it" : "Your reply was found",
                InquiryLifecycle.IsTerminal(c.Stage)
                    ? "That is all it takes: once an inquiry is resolved, reminders and escalation stop for good."
                    : "IEMAS saw that you answered, so the reminders stop. It keeps watching until the inquiry is marked resolved.",
                true, sampleSent);
        }

        var (title, explanation) = step switch
        {
            0 => ("1. A customer inquiry arrived",
                "A pop-up appeared and the inquiry is in your Action Required list. A sample email is in your inbox. Reply as usual and IEMAS sees it."),
            1 => ("2. Friendly reminder",
                "This is what you see if the reply time passes. Replying, or marking it resolved, stops everything."),
            2 => ("3. Last reminder",
                "The final warning, with the date your supervisor would be asked to help. You still have time to answer."),
            _ => ("4. Escalated to your supervisor",
                "The inquiry is marked Escalated. A copy of the email your supervisor would receive was sent to you only. Answering is still the way to finish it."),
        };
        return new PracticeStateDto(c.Id, c.CaseNumber, step, title, explanation, step >= LastStep, sampleSent);
    }

    private void AddEvent(Case c, CaseEventType type, string detail, DateTimeOffset at) =>
        _db.CaseEvents.Add(new CaseEvent { CaseId = c.Id, EventType = type, Detail = detail, OccurredAt = at });
}
