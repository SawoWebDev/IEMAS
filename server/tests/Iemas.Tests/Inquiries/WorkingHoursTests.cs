using Iemas.Application.Inquiries;
using Iemas.Application.Operations;
using Iemas.Application.Reminders;
using Iemas.Domain.Cases;
using Iemas.Domain.Operations;
using Iemas.Domain.Reminders;
using Iemas.Tests.TestSupport;

namespace Iemas.Tests.Inquiries;

/// <summary>
/// SAWO's working week: Monday-Thursday 07:00-18:00, Friday 07:00-16:00, Manila time. Deadlines must stop
/// counting when the team goes home on Friday, and the shipped defaults (8 h first reply, reminders every 3 h,
/// 2 reminders) must give about 1.5 working days before a supervisor is emailed.
/// </summary>
public class WorkingHoursTests
{
    private static readonly TimeZoneInfo Manila = ReminderTimingCalculator.ResolveTimeZone("Asia/Manila");

    private static BusinessCalendar Sawo() => new(Manila, TimeSpan.FromHours(7), TimeSpan.FromHours(18), true, Array.Empty<DateOnly>(),
        BusinessCalendar.ParseShorterDays("Fri 16:00").Days);

    /// <summary>A Manila wall-clock time as UTC.</summary>
    private static DateTimeOffset Local(int month, int day, int hour, int minute = 0) =>
        new DateTimeOffset(2026, month, day, hour, minute, 0, TimeSpan.FromHours(8)).ToUniversalTime();

    [Theory]
    [InlineData("Fri 16:00", "Fri 16:00")]
    [InlineData("friday 07:00-16:00", "Fri 07:00-16:00")]
    [InlineData("Fri 16:00\nSat 8:00-12:00", "Fri 16:00, Sat 08:00-12:00")]
    public void ShorterDays_AreParsedAndNormalised(string input, string expected)
    {
        var (_, invalid, normalized) = BusinessCalendar.ParseShorterDays(input);
        Assert.Empty(invalid);
        Assert.Equal(expected, string.Join(", ", normalized));
    }

    [Theory]
    [InlineData("Fri")]
    [InlineData("Funday 16:00")]
    [InlineData("Fri 16:00-07:00")]
    [InlineData("Fri 25:00")]
    public void ShorterDays_RejectsWhatItCannotRead(string input) =>
        Assert.NotEmpty(BusinessCalendar.ParseShorterDays(input).Invalid);

    [Fact]
    public void Friday_EndsAt1600_MondayToThursdayAt1800()
    {
        var cal = Sawo();
        Assert.Equal((TimeSpan.FromHours(7), TimeSpan.FromHours(16)), cal.HoursOn(DayOfWeek.Friday));
        Assert.Equal((TimeSpan.FromHours(7), TimeSpan.FromHours(18)), cal.HoursOn(DayOfWeek.Thursday));
    }

    [Fact]
    public void EmailOnFridayAfternoon_DeadlineCountsOnlyUntil1600_ThenMondayMorning()
    {
        // 27 Nov 2026 is a Friday. 15:00 + 8 business hours = 1 h on Friday + 7 h on Monday = Monday 14:00.
        var deadline = Sawo().AddBusinessHours(Local(11, 27, 15), 8);
        Assert.Equal(Local(11, 30, 14), deadline);
    }

    [Fact]
    public void EmailAfterFridayClose_StartsCountingMonday0700()
    {
        var deadline = Sawo().AddBusinessHours(Local(11, 27, 17), 8);
        Assert.Equal(Local(11, 30, 15), deadline);
    }

    [Fact]
    public void ShippedDefaults_MondayMorningEmail_ReachesTheSupervisorTuesdayAfternoon()
    {
        var s = SupervisionSettings.Defaults;
        var cal = Sawo();
        var arrived = Local(11, 23, 9); // Monday 09:00
        var deadline = cal.AddBusinessHours(arrived, s.FirstResponseBusinessHours);
        var reminder1 = cal.AddBusinessHours(deadline, s.ReminderIntervalBusinessHours);
        var warning = cal.AddBusinessHours(reminder1, s.ReminderIntervalBusinessHours);
        var escalation = cal.AddBusinessHours(warning, s.ReminderIntervalBusinessHours);

        Assert.Equal(Local(11, 23, 17), deadline);    // Mon 17:00
        Assert.Equal(Local(11, 24, 9), reminder1);    // Tue 09:00
        Assert.Equal(Local(11, 24, 12), warning);     // Tue 12:00
        Assert.Equal(Local(11, 24, 15), escalation);  // Tue 15:00
    }

    [Fact]
    public void ShippedDefaults_LadderMatchesTheRulesEngine()
    {
        var s = SupervisionSettings.Defaults;
        var cal = Sawo();
        var arrived = Local(11, 23, 9);
        var c = new Case { Subject = "Quote", Stage = InquiryStage.AwaitingInitialResponse, StageChangedAt = arrived, FirstEmailReceivedAt = arrived };
        c.ResolutionDueAt = cal.AddBusinessDays(arrived, s.ResolutionTargetBusinessDays);

        Assert.Null(SupervisionRules.Evaluate(c, s, cal, Local(11, 23, 16, 59)));
        Assert.Equal(SupervisionActionKind.DeadlineMissed, SupervisionRules.Evaluate(c, s, cal, Local(11, 23, 17))!.Kind);
        c.EpisodeDeadlineMissedAt = Local(11, 23, 17);
        Assert.Null(SupervisionRules.Evaluate(c, s, cal, Local(11, 24, 8, 59)));
        Assert.Equal(SupervisionActionKind.Reminder, SupervisionRules.Evaluate(c, s, cal, Local(11, 24, 9))!.Kind);
    }

    [Fact]
    public void PracticeClock_RunsTheWholeLadderInAboutSixMinutes()
    {
        var s = SupervisionSettings.Defaults;
        var cal = BusinessCalendar.PracticeClock();
        var start = new DateTimeOffset(2026, 11, 28, 22, 0, 0, TimeSpan.Zero); // a Saturday night: practice ignores business hours
        var hoursToEscalation = s.FirstResponseBusinessHours + s.ReminderIntervalBusinessHours * (s.RemindersBeforeEscalation + 1);
        var escalation = cal.AddBusinessHours(start, hoursToEscalation);
        Assert.InRange((escalation - start).TotalMinutes, 4, 8);
    }

    [Fact]
    public void PracticeInquiry_IsRecognisedByItsSubjectTag_AndTheCodeIsTiedToTheMailbox()
    {
        var tag = PracticeInquiry.SubjectTag("rafael.boroy@sawo.com");
        Assert.True(PracticeInquiry.IsPractice(new Case { Subject = $"{tag} Quotation request" }));
        Assert.True(PracticeInquiry.IsPractice(new Case { Subject = $"RE: {tag} Quotation request" }));
        Assert.False(PracticeInquiry.IsPractice(new Case { Subject = "Quotation request" }));
        Assert.NotEqual(PracticeInquiry.CodeFor("rafael.boroy@sawo.com"), PracticeInquiry.CodeFor("someone.else@sawo.com"));
        Assert.True(PracticeInquiry.IsGenuinePracticeEmail($"{tag} x", "icare@sawo.com", "Rafael.Boroy@sawo.com", new[] { "icare@sawo.com" }));
        Assert.False(PracticeInquiry.IsGenuinePracticeEmail($"{tag} x", "outsider@example.com", "rafael.boroy@sawo.com", new[] { "icare@sawo.com" }));
    }

    [Fact]
    public async Task LoadAsync_AppliesTheShorterWorkingDaysSetting_FromTheDefaultPolicy()
    {
        using var db = TestDbContext.CreateNew();
        db.ReminderPolicies.Add(new ReminderPolicy
        {
            Name = "Standard", Enabled = true, IsDefault = true, RestrictToBusinessHours = true, ExcludeWeekends = true,
            BusinessHoursStart = TimeSpan.FromHours(7), BusinessHoursEnd = TimeSpan.FromHours(18), TimeZoneId = "Asia/Manila",
            InitialDelay = TimeSpan.FromHours(1), ReminderInterval = TimeSpan.FromHours(2), MaxReminders = 3, MinimumInterval = TimeSpan.FromMinutes(30),
        });
        db.SystemSettings.Add(new SystemSetting { Key = SystemSettingKeys.ShorterWorkingDays, Value = "Fri 16:00" });
        await db.SaveChangesAsync();

        var cal = await BusinessCalendar.LoadAsync(db, CancellationToken.None);

        Assert.Equal((TimeSpan.FromHours(7), TimeSpan.FromHours(16)), cal.HoursOn(DayOfWeek.Friday));
        Assert.Equal(Local(11, 30, 14), cal.AddBusinessHours(Local(11, 27, 15), 8));
    }
}
