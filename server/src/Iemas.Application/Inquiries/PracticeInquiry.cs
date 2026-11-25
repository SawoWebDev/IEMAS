using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Iemas.Application.Common;
using Iemas.Application.Common.Interfaces;
using Iemas.Domain.Cases;
using Iemas.Domain.Email;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Inquiries;

/// <summary>
/// The practice inquiry: a sample customer email IEMAS sends to an employee's own mailbox so they can see,
/// on their first day, exactly what happens to an inquiry - the new-email pop-up, the reminder, the
/// escalation warning and the escalation - in a few minutes instead of 1.5 working days.
///
/// How it differs from a real inquiry:
/// - It comes from our own outbound account, so the internal-sender rule would normally reject it. The
///   subject carries a code tied to the receiving mailbox ("[IEMAS practice P-1a2b3c4d]"); only a matching
///   code from our outbound account opens an inquiry, without an AI call.
/// - Its deadlines run on <see cref="BusinessCalendar.PracticeClock"/> (1 business hour = 20 seconds, round the clock).
/// - Its escalation email goes to the employee themself, marked as practice, never to management.
/// Everything else (pop-ups, reply detection, "looks resolved", closing) is the real thing.
/// </summary>
public static class PracticeInquiry
{
    private static readonly Regex Tag = new(@"\[IEMAS practice (?<code>P-[0-9a-f]{8})\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>The code for practice emails sent to this mailbox.</summary>
    public static string CodeFor(string mailboxAddress)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("iemas-practice|" + mailboxAddress.Trim().ToLowerInvariant()));
        return "P-" + Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    public static string SubjectTag(string mailboxAddress) => $"[IEMAS practice {CodeFor(mailboxAddress)}]";

    /// <summary>True for an inquiry that was opened from a practice email (its subject keeps the tag).</summary>
    public static bool IsPractice(Case c) => Tag.IsMatch(c.Subject ?? string.Empty);

    /// <summary>
    /// True when <paramref name="subject"/> carries the practice code for <paramref name="mailboxAddress"/> and the
    /// email came from one of our outbound accounts.
    /// </summary>
    public static bool IsGenuinePracticeEmail(string subject, string fromAddress, string mailboxAddress, IEnumerable<string> outboundAddresses)
    {
        var m = Tag.Match(subject ?? string.Empty);
        if (!m.Success || !string.Equals(m.Groups["code"].Value, CodeFor(mailboxAddress), StringComparison.OrdinalIgnoreCase)) return false;
        return outboundAddresses.Any(a => string.Equals(a.Trim(), fromAddress.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record WorkingRulesDto(string WorkingHours, int FirstReplyBusinessHours, int ReminderIntervalBusinessHours,
    int RemindersBeforeEscalation, int CustomerWaitCalendarDays, string? SupervisorName);

public sealed record PracticeInquiryResult(bool Sent, string Mailbox, string Subject, string Message, string? Error);

/// <summary>Sends the practice inquiry email (CMS button, or the employee's own "Send me a practice inquiry" in the Agent).</summary>
public class PracticeInquiryService
{
    private readonly IAppDbContext _db;
    private readonly IEscalationEmailSender _sender;
    private readonly IAuditService? _audit;

    public PracticeInquiryService(IAppDbContext db, IEscalationEmailSender sender, IAuditService? audit = null)
    {
        _db = db;
        _sender = sender;
        _audit = audit;
    }

    public async Task<Result<PracticeInquiryResult>> SendAsync(Guid employeeId, string requestedBy, CancellationToken cancellationToken)
    {
        var employee = await _db.Employees.AsNoTracking().Include(e => e.SupervisorEmployee)
            .FirstOrDefaultAsync(e => e.Id == employeeId, cancellationToken);
        if (employee is null) return Result<PracticeInquiryResult>.Failure("Employee not found.");

        var mailbox = await _db.EmailAccounts.AsNoTracking()
            .Where(a => a.OwnerEmployeeId == employeeId && a.Purpose == EmailAccountPurpose.Inbound && a.IsActive && a.MonitoringEnabled)
            .OrderBy(a => a.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (mailbox is null)
        {
            return Result<PracticeInquiryResult>.Failure(
                $"{employee.FullName} has no monitored mailbox. Add one under Email Accounts with {employee.FullName} as the owner first.");
        }

        var outbound = await _db.EmailAccounts.AsNoTracking()
            .AnyAsync(a => a.Purpose == EmailAccountPurpose.Outbound && a.IsActive, cancellationToken);
        if (!outbound)
        {
            return Result<PracticeInquiryResult>.Failure("No outbound email account is configured, so IEMAS cannot send the practice email. Add one under Email Accounts.");
        }

        var settings = await SupervisionSettings.LoadAsync(_db, cancellationToken);
        var supervisor = employee.SupervisorEmployee?.FullName;
        var subject = $"{PracticeInquiry.SubjectTag(mailbox.EmailAddress)} Quotation request: SAWO heater for a home sauna";
        var body = Body(employee.FullName, supervisor, settings);

        var result = await _sender.SendAsync(new OutboundEmail(
            new[] { new EmailRecipient(employee.FullName, mailbox.EmailAddress) }, subject, body), cancellationToken);

        if (_audit is not null)
        {
            await _audit.LogAsync("Inquiry.PracticeEmailSent", "Employee", employee.Id.ToString(),
                $"Practice inquiry to {mailbox.EmailAddress}, requested by {requestedBy}: {(result.Sent ? "sent" : "failed - " + result.Error)}", cancellationToken);
        }

        var message = result.Sent
            ? $"A practice inquiry was sent to {mailbox.EmailAddress}. The NEW EMAIL pop-up appears within about 3 minutes; "
              + "if nobody replies, the reminder, the escalation warning and the escalation follow within about 10 minutes."
            : $"The practice email could not be sent: {result.Error}";
        return Result<PracticeInquiryResult>.Success(new PracticeInquiryResult(result.Sent, mailbox.EmailAddress, subject, message, result.Error));
    }

    /// <summary>The working hours and supervision times, in words, for the Agent's welcome tour.</summary>
    public async Task<WorkingRulesDto> GetWorkingRulesAsync(Guid employeeId, CancellationToken cancellationToken)
    {
        var settings = await SupervisionSettings.LoadAsync(_db, cancellationToken);
        var supervisor = await _db.Employees.AsNoTracking().Where(e => e.Id == employeeId)
            .Select(e => e.SupervisorEmployee == null ? null : e.SupervisorEmployee.FullName).FirstOrDefaultAsync(cancellationToken);
        var policy = await _db.ReminderPolicies.AsNoTracking().FirstOrDefaultAsync(p => p.Enabled && p.IsDefault, cancellationToken);
        var shorter = BusinessCalendar.ParseShorterDays(
            await Operations.SystemSettingsService.GetValueAsync(_db, Operations.SystemSettingKeys.ShorterWorkingDays, cancellationToken)).Days;

        string hours;
        if (policy is null || !policy.RestrictToBusinessHours)
        {
            hours = "Deadlines count around the clock (no business hours are set).";
        }
        else
        {
            static string T(TimeSpan t) => t.ToString(@"hh\:mm");
            var workdays = policy.ExcludeWeekends
                ? new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday }
                : new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday };
            var groups = new List<(List<DayOfWeek> Days, string Range)>();
            foreach (var day in workdays)
            {
                var range = shorter.TryGetValue(day, out var h) ? $"{T(h.Start ?? policy.BusinessHoursStart)}-{T(h.End)}" : $"{T(policy.BusinessHoursStart)}-{T(policy.BusinessHoursEnd)}";
                if (groups.Count > 0 && groups[^1].Range == range) groups[^1].Days.Add(day);
                else groups.Add((new List<DayOfWeek> { day }, range));
            }
            static string D(DayOfWeek d) => d.ToString()[..3];
            hours = string.Join(", ", groups.Select(g => (g.Days.Count == 1 ? D(g.Days[0]) : $"{D(g.Days[0])}-{D(g.Days[^1])}") + " " + g.Range))
                + $" ({policy.TimeZoneId})" + (policy.ExcludeWeekends ? "; weekends and holidays do not count." : ".");
        }

        return new WorkingRulesDto(hours, settings.FirstResponseBusinessHours, settings.ReminderIntervalBusinessHours,
            settings.RemindersBeforeEscalation, settings.CustomerWaitCalendarDays, supervisor);
    }

    private static string Body(string employeeName, string? supervisorName, SupervisionSettings s)
    {
        var firstName = employeeName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? employeeName;
        var who = supervisorName is null ? "your supervisor" : supervisorName;
        var realLife = $"{s.FirstResponseBusinessHours} business hours for the first reply, then a reminder every {s.ReminderIntervalBusinessHours} business hours";
        return $"""
            Hi {firstName},

            This is a PRACTICE inquiry from IEMAS. It is not from a real customer. It shows you what IEMAS does when a customer email arrives in your mailbox.

            ---------------- Sample customer message ----------------
            Good morning,

            We are building a home sauna of about 8 m3 in our new house and we are interested in a SAWO heater.
            Could you please send us a quotation for a suitable heater with a control unit, and tell us the delivery time?

            Best regards,
            Maria Santos
            ----------------------------------------------------------

            WHAT HAPPENS NOW (practice runs fast)
            1. Within about 3 minutes: a blue NEW EMAIL pop-up appears in the bottom-right corner of your screen.
            2. If you do not reply: an amber REMINDER, then an orange ESCALATION WARNING, then a red ESCALATED pop-up,
               about one minute apart. You also receive a copy of the escalation email.
            3. Reply to this email from Outlook at any point (a short "Test reply" is fine). Within about 2 minutes IEMAS sees
               your reply and the reminders stop. Later you may be asked to confirm that the inquiry is resolved.

            IN REAL USE
            - The same steps follow working hours: {realLife}, and only after {s.RemindersBeforeEscalation} reminder(s) is {who} emailed.
            - Evenings, weekends and holidays do not count.
            - Simply answer customers from Outlook as usual. IEMAS notices your reply on its own.

            In practice the escalation email is sent only to you, never to management.

            IEMAS
            """;
    }
}
