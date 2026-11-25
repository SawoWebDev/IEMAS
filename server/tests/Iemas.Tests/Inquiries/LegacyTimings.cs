using Iemas.Application.Inquiries;
using Iemas.Application.Operations;
using Iemas.Domain.Operations;
using Iemas.Tests.TestSupport;

namespace Iemas.Tests.Inquiries;

/// <summary>
/// The supervision scenarios were written against the product owner's first Q1 timings (first reply 4 business
/// hours, reminders every 2, customer wait 3 days, internal wait 1 day, target 5 days, next level after 1 day).
/// They test the rules, not the shipped defaults (which are now 8 h / 3 h / 5 / 2 / 10 / 2), so they pin those numbers.
/// </summary>
public static class LegacyTimings
{
    public static readonly SupervisionSettings Settings = SupervisionSettings.Defaults with
    {
        FirstResponseBusinessHours = 4,
        ReminderIntervalBusinessHours = 2,
        CustomerWaitCalendarDays = 3,
        InternalWaitBusinessDays = 1,
        ResolutionTargetBusinessDays = 5,
        EscalationRepeatBusinessDays = 1,
        AiResolutionMinConfidencePercent = 75,
    };

    public static void Seed(TestDbContext db)
    {
        void Set(string key, string value) => db.SystemSettings.Add(new SystemSetting { Key = key, Value = value });
        Set(SystemSettingKeys.FirstResponseBusinessHours, "4");
        Set(SystemSettingKeys.ReminderIntervalBusinessHours, "2");
        Set(SystemSettingKeys.CustomerWaitCalendarDays, "3");
        Set(SystemSettingKeys.InternalWaitBusinessDays, "1");
        Set(SystemSettingKeys.ResolutionTargetBusinessDays, "5");
        Set(SystemSettingKeys.EscalationRepeatBusinessDays, "1");
        Set(SystemSettingKeys.AiResolutionMinConfidence, "75");
        db.SaveChanges();
    }
}
