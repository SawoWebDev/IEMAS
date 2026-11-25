using Iemas.Application.Inquiries;
using Iemas.Application.Operations;
using Iemas.Domain.Operations;

namespace Iemas.Tests.TestSupport;

/// <summary>
/// The short, round-number timings the scenario tests are written around (first reply 4 business hours,
/// reminders 2 hours apart, Mon-Fri 08:00-17:00 UTC). Pinned here so a change to the shipped defaults does not
/// silently change what a scenario proves. The shipped defaults have their own tests.
/// </summary>
public static class ShortTimings
{
    public static readonly SupervisionSettings Settings = SupervisionSettings.Defaults with
    {
        FirstResponseBusinessHours = 4, ReminderIntervalBusinessHours = 2, RemindersBeforeEscalation = 2, IdleBusinessDays = 1,
        StalledBusinessDays = 1, CustomerWaitCalendarDays = 3, InternalWaitBusinessDays = 1, ResolutionTargetBusinessDays = 5,
        EscalationRepeatBusinessDays = 1, AlertsInWorkingHoursOnly = false,
    };

    public static void Apply(TestDbContext db)
    {
        void Set(string key, object value) => db.SystemSettings.Add(new SystemSetting { Key = key, Value = value.ToString()!.ToLowerInvariant() });
        var s = Settings;
        Set(SystemSettingKeys.FirstResponseBusinessHours, s.FirstResponseBusinessHours);
        Set(SystemSettingKeys.ReminderIntervalBusinessHours, s.ReminderIntervalBusinessHours);
        Set(SystemSettingKeys.RemindersBeforeEscalation, s.RemindersBeforeEscalation);
        Set(SystemSettingKeys.IdleBusinessDays, s.IdleBusinessDays);
        Set(SystemSettingKeys.StalledBusinessDays, s.StalledBusinessDays);
        Set(SystemSettingKeys.CustomerWaitCalendarDays, s.CustomerWaitCalendarDays);
        Set(SystemSettingKeys.InternalWaitBusinessDays, s.InternalWaitBusinessDays);
        Set(SystemSettingKeys.ResolutionTargetBusinessDays, s.ResolutionTargetBusinessDays);
        Set(SystemSettingKeys.EscalationRepeatBusinessDays, s.EscalationRepeatBusinessDays);
        Set(SystemSettingKeys.AlertsInWorkingHoursOnly, s.AlertsInWorkingHoursOnly);
        Set(SystemSettingKeys.AiConfirmsInquiries, false);
        db.SystemSettings.Add(new SystemSetting { Key = SystemSettingKeys.DefaultTimeZone, Value = "UTC" });
        db.SaveChanges();
    }
}
