using Iemas.Application.Common.Interfaces;
using Iemas.Application.Inquiries;
using Iemas.Application.Notifications;
using Iemas.Application.Reminders;
using Iemas.Domain.Cases;
using Iemas.Domain.Email;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Agents;

/// <summary>One moment in the life of an unanswered inquiry, e.g. "Reminder 1" at "Thu 26 Nov, 10:00 AM".</summary>
public record AgentGuideStepDto(string Label, string When);

/// <summary>What the Agent's first-time walkthrough tells this employee, taken from the live configuration.</summary>
public record AgentGuideDto(
    string EmployeeName,
    IReadOnlyList<string> Mailboxes,
    string? SupervisorName,
    string WorkingHours,
    string TimeZone,
    int FirstReplyHours,
    int ReminderIntervalHours,
    int Reminders,
    int HoursUntilSupervisorIsTold,
    int FollowUpBusinessDays,
    int CustomerSilentDays,
    IReadOnlyList<AgentGuideStepDto> Example);

public class AgentGuideService
{
    private readonly IAppDbContext _db;
    private readonly TimeProvider _time;

    public AgentGuideService(IAppDbContext db, TimeProvider? time = null)
    {
        _db = db;
        _time = time ?? TimeProvider.System;
    }

    public async Task<AgentGuideDto?> GetAsync(Guid employeeId, CancellationToken cancellationToken)
    {
        var employee = await _db.Employees.AsNoTracking().Include(e => e.SupervisorEmployee)
            .FirstOrDefaultAsync(e => e.Id == employeeId, cancellationToken);
        if (employee is null) return null;

        var mailboxes = await _db.EmailAccounts.AsNoTracking()
            .Where(a => a.OwnerEmployeeId == employeeId && a.Purpose == EmailAccountPurpose.Inbound && a.IsActive)
            .OrderBy(a => a.EmailAddress).Select(a => a.EmailAddress).ToListAsync(cancellationToken);

        var s = await SupervisionSettings.LoadAsync(_db, cancellationToken);
        var calendar = s.TestClockMinutes ? BusinessCalendar.FastTestClock() : await BusinessCalendar.LoadAsync(_db, cancellationToken);
        var policy = await _db.ReminderPolicies.AsNoTracking().Where(p => p.Enabled && p.IsDefault).FirstOrDefaultAsync(cancellationToken);
        var hours = policy is null ? "Mon-Fri 08:00-17:00"
            : !policy.RestrictToBusinessHours ? (policy.ExcludeWeekends ? "All day, Monday to Friday" : "All day, every day")
            : WeeklySchedule.ParseOrNull(policy.WeeklyHours)?.ToString()
                ?? $"{(policy.ExcludeWeekends ? "Mon-Fri" : "Mon-Sun")} {policy.BusinessHoursStart:hh\\:mm}-{policy.BusinessHoursEnd:hh\\:mm}";

        // A made-up inquiry arriving now, to show real dates rather than only hour counts.
        var now = _time.GetUtcNow();
        var example = new Case { Stage = InquiryStage.AwaitingInitialResponse, StageChangedAt = now };
        var (due, escalates) = SupervisionRules.Forecast(example, s, calendar);
        string At(DateTimeOffset? t) => NotificationService.FormatMoment(t, calendar.Zone);

        var steps = new List<AgentGuideStepDto>
        {
            new("A customer emails you: pop-up on this PC", At(now)),
            new("Reply is due", At(due)),
        };
        for (var i = 1; i <= s.RemindersBeforeEscalation && due is DateTimeOffset deadline; i++)
        {
            var label = i == s.RemindersBeforeEscalation ? "Last reminder" : $"Reminder {i}";
            steps.Add(new AgentGuideStepDto(label, At(calendar.AddBusinessHours(deadline, s.ReminderIntervalBusinessHours * i))));
        }
        steps.Add(new AgentGuideStepDto("Supervisor is asked to help", At(escalates)));

        return new AgentGuideDto(
            employee.FullName, mailboxes, employee.SupervisorEmployee?.FullName, hours, calendar.Zone.Id,
            s.FirstResponseBusinessHours, s.ReminderIntervalBusinessHours, s.RemindersBeforeEscalation,
            s.FirstResponseBusinessHours + s.ReminderIntervalBusinessHours * (s.RemindersBeforeEscalation + 1),
            s.IdleBusinessDays, s.CustomerWaitCalendarDays, steps);
    }
}
