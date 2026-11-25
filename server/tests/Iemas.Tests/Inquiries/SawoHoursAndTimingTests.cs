using Iemas.Application.Escalations;
using Iemas.Application.Inquiries;
using Iemas.Application.Notifications;
using Iemas.Application.Reminders;
using Iemas.Domain.Cases;
using Iemas.Domain.Reminders;
using Iemas.Tests.TestSupport;

namespace Iemas.Tests.Inquiries;

/// <summary>SAWO office hours (Mon-Thu 07:00-18:00, Fri 07:00-16:00) with the shipped timings.</summary>
public class SawoHoursAndTimingTests
{
    private static readonly TimeZoneInfo Manila = TimeZoneInfo.FindSystemTimeZoneById("Asia/Manila");

    private static readonly BusinessCalendar Office = new(
        Manila, TimeSpan.FromHours(8), TimeSpan.FromHours(17), true, Array.Empty<DateOnly>(),
        WeeklySchedule.ParseOrNull(WeeklySchedule.SawoDefault));

    private static readonly SupervisionSettings Shipped = SupervisionSettings.Defaults;

    /// <summary>A Manila wall-clock time in November/December 2026. 23 Nov is a Monday.</summary>
    private static DateTimeOffset At(int day, int hour, int minute = 0, int month = 11) =>
        new DateTimeOffset(2026, month, day, hour, minute, 0, TimeSpan.FromHours(8)).ToUniversalTime();

    [Theory]
    [InlineData("Mon-Thu 07:00-18:00, Fri 07:00-16:00")]
    [InlineData("monday - thursday 7:00-18:00; friday 7:00-16:00")]
    [InlineData("Mon=07:00-18:00\nTue=07:00-18:00\nWed=07:00-18:00\nThu=07:00-18:00\nFri=07:00-16:00")]
    public void Schedule_AcceptsTheWaysPeopleWriteIt(string text) =>
        Assert.Equal(WeeklySchedule.SawoDefault, WeeklySchedule.ParseOrNull(text)!.ToString());

    [Theory]
    [InlineData("")]
    [InlineData("every day")]
    [InlineData("Mon 18:00-07:00")]
    [InlineData("Fri-Mon 07:00-16:00")]
    [InlineData("Mon-Tue 07:00-18:00, Tue 08:00-12:00")]
    [InlineData("Mon 07:00-25:00")]
    public void Schedule_RejectsWhatCannotBeMeant(string text)
    {
        Assert.False(WeeklySchedule.TryParse(text, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Friday_ClosesAtFour_AndTheClockContinuesMondayMorning()
    {
        // Friday 15:00 + 2 working hours: one on Friday, one on Monday.
        Assert.Equal(At(30, 8), Office.AddBusinessHours(At(27, 15), 2));
        Assert.True(Office.IsOpen(At(27, 15, 59)));
        Assert.False(Office.IsOpen(At(27, 17)));          // people may still be there, but nothing is counted or chased
        Assert.True(Office.IsOpen(At(26, 17)));           // Thursday runs to 18:00
        Assert.False(Office.IsOpen(At(28, 10)));          // Saturday
        Assert.Equal(TimeSpan.FromHours(11), Office.BusinessDayLength);
    }

    [Fact]
    public void ReminderEngine_UsesTheSameWeeklyHours()
    {
        var policy = new ReminderPolicy { RestrictToBusinessHours = true, WeeklyHours = WeeklySchedule.SawoDefault, TimeZoneId = "Asia/Manila" };
        Assert.Equal(At(30, 7), ReminderTimingCalculator.AdjustToAllowedWindow(policy, At(27, 16, 30), Array.Empty<DateOnly>()));
        Assert.Equal(At(26, 17), ReminderTimingCalculator.AdjustToAllowedWindow(policy, At(26, 17), Array.Empty<DateOnly>()));
    }

    private static Case NewInquiry(DateTimeOffset at) => new()
    {
        Stage = InquiryStage.AwaitingInitialResponse, StageChangedAt = at, FirstEmailReceivedAt = at,
        WorkStatus = CaseWorkStatus.ActionRequired,
    };

    [Theory]
    // received            reply due          supervisor told
    [InlineData(23, 9, 23, 17, 24, 18)]   // Monday morning: due the same afternoon, supervisor end of Tuesday
    [InlineData(26, 8, 26, 16, 30, 8)]    // Thursday morning: supervisor Monday morning, not Friday evening
    [InlineData(27, 15, 30, 14, 1, 15)]   // Friday afternoon: due Monday, supervisor Tuesday afternoon
    public void Timeline_GivesAboutTwoWorkingDaysBeforeAnyoneElseHears(int receivedDay, int receivedHour, int dueDay, int dueHour, int toldDay, int toldHour)
    {
        var (due, told) = SupervisionRules.Forecast(NewInquiry(At(receivedDay, receivedHour)), Shipped, Office);

        Assert.Equal(At(dueDay, dueHour), due);
        Assert.Equal(At(toldDay, toldHour, 0, toldDay < 23 ? 12 : 11), told);
    }

    [Fact]
    public void TheEngine_FollowsTheForecast_ExactlyAndOnlyThen()
    {
        var c = NewInquiry(At(23, 9));
        c.ResolutionDueAt = At(20, 18, 0, 12);
        var (_, told) = SupervisionRules.Forecast(c, Shipped, Office);
        var sent = new List<(SupervisionActionKind Kind, DateTimeOffset At)>();

        for (var now = At(23, 9); now <= At(25, 12); now = now.AddMinutes(30))
        {
            var action = SupervisionRules.Evaluate(c, Shipped, Office, now);
            if (action is null) continue;
            sent.Add((action.Kind, now));
            if (action.Kind == SupervisionActionKind.DeadlineMissed) c.EpisodeDeadlineMissedAt = action.Value;
            if (action.Kind == SupervisionActionKind.Reminder) { c.EpisodeRemindersSent++; c.EpisodeLastReminderAt = now; }
            if (action.Kind == SupervisionActionKind.Escalate) { c.EpisodeEscalations++; c.EpisodeLastEscalatedAt = now; }
        }

        Assert.Equal(new[]
        {
            (SupervisionActionKind.DeadlineMissed, At(23, 17)),
            (SupervisionActionKind.Reminder, At(24, 10)),
            (SupervisionActionKind.Reminder, At(24, 14)),
            (SupervisionActionKind.Escalate, told!.Value),
        }, sent);
    }

    [Fact]
    public void AReminderSentLate_PushesTheEscalationDateBackToo()
    {
        var c = NewInquiry(At(23, 9));
        c.EpisodeRemindersSent = 1;
        c.EpisodeLastReminderAt = At(24, 16);   // six hours later than planned

        var (_, told) = SupervisionRules.Forecast(c, Shipped, Office);

        Assert.Equal(At(25, 13), told);          // 8 more working hours: 2 on Tuesday, 6 on Wednesday
    }

    [Fact]
    public async Task AlertsDueOutsideWorkingHours_WaitForTheNextWorkingMorning()
    {
        using var h = new InquiryHarness("agent1@sawo.com");
        var setting = h.Db.SystemSettings.Single(s => s.Key == Application.Operations.SystemSettingKeys.AlertsInWorkingHoursOnly);
        setting.Value = "true";
        h.Db.SaveChanges();
        h.Receive("agent1@sawo.com", "anna@customer.example", "Heater price?", "What is the price of the heater?", InquiryHarness.Monday0900);
        await h.RunAsync(InquiryHarness.Monday0900);

        // Reminder 1 is long overdue by Saturday, but nobody is chased or escalated then.
        await h.RunAsync(InquiryHarness.Day(5, 10));
        await h.RunAsync(InquiryHarness.Day(5, 14));
        var onSaturday = await h.CaseAsync("agent1@sawo.com", "anna@customer.example");
        Assert.Equal(0, onSaturday.EpisodeRemindersSent);
        Assert.Empty(h.ManagementEmail.Sent);

        // Monday: first the overdue notice (the resolution target passed), then the held reminder.
        await h.RunAsync(InquiryHarness.Day(7, 9));
        await h.RunAsync(InquiryHarness.Day(7, 10));
        var onMonday = await h.CaseAsync("agent1@sawo.com", "anna@customer.example");
        Assert.Equal(1, onMonday.EpisodeRemindersSent);
    }

    [Fact]
    public async Task PopUps_SayWhenTheReplyIsDue_AndWhenTheSupervisorWouldBeTold()
    {
        using var h = new InquiryHarness("agent1@sawo.com");
        h.Receive("agent1@sawo.com", "anna@customer.example", "Heater price?", "What is the price of the heater?", InquiryHarness.Monday0900);
        await h.RunAsync(InquiryHarness.Monday0900);

        // Scenario timings: 4 hours to reply, from Monday 09:00 in the harness's UTC 08:00-17:00 calendar.
        Assert.Contains("Please reply by Mon 2 Nov, 1:00 PM", h.PopUps.Sent.Last().Command.Message);

        await h.RunAsync(InquiryHarness.Day(0, 13, 5));
        await h.RunAsync(InquiryHarness.Day(0, 15, 5));
        await h.RunAsync(InquiryHarness.Day(1, 9, 0));
        var warning = h.PopUps.Sent.Last().Command;
        Assert.Equal("Last reminder (escalation warning)", warning.Title);
        Assert.Contains("If there is no reply by Tue 3 Nov, 11:00 AM", warning.Message);
    }

    [Fact]
    public void UneditedTemplates_GetTheNewWording_EditedOnesAreKept()
    {
        var old = NotificationTemplateDefaults.PreviousDefaultMessages;
        var untouched = NotificationTemplateDefaults.For(Domain.Notifications.NotificationType.Reminder);
        untouched.Title = "Reminder";
        untouched.MessageText = old[Domain.Notifications.NotificationType.Reminder];
        var edited = NotificationTemplateDefaults.For(Domain.Notifications.NotificationType.NewEmail);
        edited.MessageText = "Our own words about {{email_subject}}";

        Infrastructure.Persistence.DbSeeder.RefreshUnchangedTemplates(new[] { untouched, edited });

        Assert.Equal(NotificationTemplateDefaults.For(Domain.Notifications.NotificationType.Reminder).MessageText, untouched.MessageText);
        Assert.Equal("Our own words about {{email_subject}}", edited.MessageText);
    }

    [Fact]
    public void EveryNotificationType_HasAnEarlierWordingRecorded_AndOnlyKnownVariables()
    {
        foreach (var type in Enum.GetValues<Domain.Notifications.NotificationType>())
        {
            var template = NotificationTemplateDefaults.For(type);
            Assert.True(NotificationTemplateDefaults.PreviousDefaultMessages.ContainsKey(type));
            Assert.Empty(NotificationService.FindUnknownVariables(template.MessageText));
            Assert.True(template.Title.Length <= 200);
        }
    }
}
