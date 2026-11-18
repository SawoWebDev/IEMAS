using Iemas.Application.Inquiries;
using Iemas.Application.Operations;
using Iemas.Application.Reminders;
using Iemas.Domain.Operations;
using Iemas.Domain.Reminders;
using Iemas.Tests.TestSupport;
using Xunit;

namespace Iemas.Tests.Inquiries;

/// <summary>Business-time arithmetic: boundaries, weekends, holidays, midnight, time zones, DST.</summary>
public class BusinessCalendarHardeningTests
{
    private static readonly BusinessCalendar Utc = BusinessCalendar.Default; // Mon-Fri 08:00-17:00 UTC

    /// <summary>2026-11-02 is a Monday.</summary>
    private static DateTimeOffset At(int day, int hour, int minute = 0) => new(2026, 11, day, hour, minute, 0, TimeSpan.Zero);

    private static TimeZoneInfo? Zone(string id) => TimeZoneInfo.TryFindSystemTimeZoneById(id, out var z) ? z : null;

    // ------------------------------------------------------------ start / end of day, exact boundaries

    [Theory]
    [InlineData(2, 9, 0, 2, 11, 0)]    // inside the day
    [InlineData(2, 15, 0, 2, 17, 0)]   // finishes exactly at closing time: stays on the same day
    [InlineData(2, 16, 0, 3, 9, 0)]    // spills into the next morning (1h today + 1h tomorrow)
    [InlineData(2, 17, 0, 3, 10, 0)]   // starting exactly at closing time counts from tomorrow's opening
    [InlineData(2, 17, 1, 3, 10, 0)]   // just after closing
    [InlineData(2, 7, 59, 2, 10, 0)]   // before opening: counting starts at 08:00
    [InlineData(2, 8, 0, 2, 10, 0)]    // exactly at opening
    [InlineData(2, 0, 0, 2, 10, 0)]    // midnight
    public void AddBusinessHours_2h_BoundaryCases(int d, int h, int m, int ed, int eh, int em) =>
        Assert.Equal(At(ed, eh, em), Utc.AddBusinessHours(At(d, h, m), 2));

    [Fact]
    public void ZeroOrNegativeAmount_ReturnsStartUnchanged()
    {
        Assert.Equal(At(7, 12), Utc.AddBusinessTime(At(7, 12), TimeSpan.Zero)); // even on a Saturday
        Assert.Equal(At(7, 12), Utc.AddBusinessTime(At(7, 12), TimeSpan.FromHours(-3)));
    }

    [Fact]
    public void ExactlyOneFullBusinessDay_FromOpening_EndsAtClosing()
    {
        Assert.Equal(At(2, 17), Utc.AddBusinessTime(At(2, 8), TimeSpan.FromHours(9)));
        Assert.Equal(At(3, 9), Utc.AddBusinessTime(At(2, 8), TimeSpan.FromHours(10)));
    }

    // ------------------------------------------------------------ weekends

    [Theory]
    [InlineData(6, 16, 0, 9, 9)]   // Friday 16:00 +2h: 1h Friday + 1h Monday -> Monday 09:00
    [InlineData(7, 10, 0, 9, 10)]  // Saturday -> Monday 08:00 + 2h
    [InlineData(8, 23, 59, 9, 10)] // Sunday night -> Monday 08:00 + 2h
    public void Weekends_DoNotCount(int d, int h, int m, int ed, int eh) =>
        Assert.Equal(At(ed, eh), Utc.AddBusinessHours(At(d, h, m), 2));

    [Fact]
    public void OneBusinessDay_FromFriday_IsMonday_SameTime() =>
        Assert.Equal(At(9, 12), Utc.AddBusinessDays(At(6, 12), 1));

    [Fact]
    public void OneBusinessDay_FromSaturday_EndsMondayAtClosing() =>
        Assert.Equal(At(9, 17), Utc.AddBusinessDays(At(7, 12), 1));

    [Fact]
    public void FiveBusinessDays_FromMonday_IsNextMondaySameTime() =>
        Assert.Equal(At(9, 9), Utc.AddBusinessDays(At(2, 9), 5));

    [Fact]
    public void WeekendsIncluded_WhenNotExcluded()
    {
        var everyDay = new BusinessCalendar(TimeZoneInfo.Utc, TimeSpan.FromHours(8), TimeSpan.FromHours(17), false, Array.Empty<DateOnly>());
        Assert.Equal(At(8, 9), everyDay.AddBusinessHours(At(7, 16), 2)); // Saturday 16:00 +2h -> Sunday 09:00
    }

    // ------------------------------------------------------------ holidays

    [Fact]
    public void Holiday_IsSkipped()
    {
        var cal = new BusinessCalendar(TimeZoneInfo.Utc, TimeSpan.FromHours(8), TimeSpan.FromHours(17), true, new[] { new DateOnly(2026, 11, 9) });
        // Friday 16:00 +2h: Monday 9 Nov is a holiday, so Tuesday 09:00.
        Assert.Equal(At(10, 9), cal.AddBusinessHours(At(6, 16), 2));
    }

    [Fact]
    public void ConsecutiveHolidays_AroundWeekend_AreAllSkipped()
    {
        var cal = new BusinessCalendar(TimeZoneInfo.Utc, TimeSpan.FromHours(8), TimeSpan.FromHours(17), true,
            new[] { new DateOnly(2026, 11, 5), new DateOnly(2026, 11, 6), new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 10) });
        Assert.Equal(At(11, 9), cal.AddBusinessHours(At(4, 16), 2)); // Wed 16:00 -> Wed 17:00 is 1h, then Thu-Tue off: Wed 11 09:00
    }

    [Fact]
    public void HolidayOnAWeekend_ChangesNothing()
    {
        var cal = new BusinessCalendar(TimeZoneInfo.Utc, TimeSpan.FromHours(8), TimeSpan.FromHours(17), true, new[] { new DateOnly(2026, 11, 7) });
        Assert.Equal(At(9, 9), cal.AddBusinessHours(At(6, 16), 2));
    }

    [Fact]
    public void IsBusinessDay_ReflectsWeekendsAndHolidays()
    {
        var cal = new BusinessCalendar(TimeZoneInfo.Utc, TimeSpan.FromHours(8), TimeSpan.FromHours(17), true, new[] { new DateOnly(2026, 11, 4) });
        Assert.True(cal.IsBusinessDay(new DateOnly(2026, 11, 2)));
        Assert.False(cal.IsBusinessDay(new DateOnly(2026, 11, 4)));  // holiday
        Assert.False(cal.IsBusinessDay(new DateOnly(2026, 11, 7)));  // Saturday
        Assert.False(cal.IsBusinessDay(new DateOnly(2026, 11, 8)));  // Sunday
    }

    // ------------------------------------------------------------ across midnight / round-the-clock

    [Fact]
    public void RoundTheClockCalendar_CountsAcrossMidnight()
    {
        var cal = new BusinessCalendar(TimeZoneInfo.Utc, TimeSpan.Zero, TimeSpan.FromHours(24), true, Array.Empty<DateOnly>());
        Assert.Equal(At(3, 1), cal.AddBusinessHours(At(2, 23), 2));
        Assert.Equal(At(9, 1), cal.AddBusinessHours(At(6, 23), 2)); // Friday 23:00 +2h -> Monday 01:00 (Sat, Sun skipped)
    }

    [Fact]
    public void MisconfiguredEndBeforeStart_StillTerminatesWithAOneHourWindow()
    {
        var cal = new BusinessCalendar(TimeZoneInfo.Utc, TimeSpan.FromHours(22), TimeSpan.FromHours(6), true, Array.Empty<DateOnly>());
        Assert.Equal(TimeSpan.FromHours(1), cal.BusinessDayLength);
        var result = cal.AddBusinessHours(At(2, 12), 3);
        Assert.True(result > At(2, 12));
    }

    [Fact]
    public void AllDaysHolidays_FallsBackInsteadOfLooping()
    {
        var holidays = Enumerable.Range(0, 4000).Select(i => new DateOnly(2026, 1, 1).AddDays(i)).ToList();
        var cal = new BusinessCalendar(TimeZoneInfo.Utc, TimeSpan.FromHours(8), TimeSpan.FromHours(17), true, holidays);
        Assert.Equal(At(2, 12) + TimeSpan.FromHours(2), cal.AddBusinessHours(At(2, 12), 2));
    }

    // ------------------------------------------------------------ time zones

    [Fact]
    public void BusinessHoursAreInTheConfiguredZone_ResultIsUtc()
    {
        var manila = Zone("Asia/Manila");
        if (manila is null) return;
        var cal = new BusinessCalendar(manila, TimeSpan.FromHours(8), TimeSpan.FromHours(17), true, Array.Empty<DateOnly>());

        // Monday 00:00 UTC = 08:00 Manila (opening). +1h = 09:00 Manila = 01:00 UTC.
        Assert.Equal(At(2, 1), cal.AddBusinessHours(At(2, 0), 1));
        // Monday 09:00 UTC = 17:00 Manila (closing). +1h = Tuesday 09:00 Manila = Tuesday 01:00 UTC.
        var next = cal.AddBusinessHours(At(2, 9), 1);
        Assert.Equal(At(3, 1), next);
        Assert.Equal(TimeSpan.Zero, next.Offset); // stored as UTC
        // Sunday evening in UTC is already Monday morning in Manila: 20:00 UTC Sunday = 04:00 Monday Manila -> opens 08:00 = 00:00 UTC.
        Assert.Equal(At(9, 1), cal.AddBusinessHours(At(8, 20), 1));
    }

    [Fact]
    public void HolidayIsEvaluatedOnTheLocalDate_NotTheUtcDate()
    {
        var manila = Zone("Asia/Manila");
        if (manila is null) return;
        var cal = new BusinessCalendar(manila, TimeSpan.FromHours(8), TimeSpan.FromHours(17), true, new[] { new DateOnly(2026, 11, 3) });
        // Monday 09:00 UTC = 17:00 Manila. Tuesday 3 Nov (local) is a holiday, so the next opening is Wednesday 08:00 Manila = Wed 00:00 UTC.
        Assert.Equal(At(4, 1), cal.AddBusinessHours(At(2, 9), 1));
    }

    [Fact]
    public void SpringForward_BusinessHoursStayAtLocalWallClock()
    {
        var ny = Zone("America/New_York");
        if (ny is null) return;
        var cal = new BusinessCalendar(ny, TimeSpan.FromHours(8), TimeSpan.FromHours(17), true, Array.Empty<DateOnly>());
        // Fri 6 Mar 2026 16:00 EST (21:00Z) +2h -> Mon 9 Mar 09:00 EDT (13:00Z); the clocks changed over the weekend.
        var from = new DateTimeOffset(2026, 3, 6, 21, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 13, 0, 0, TimeSpan.Zero), cal.AddBusinessHours(from, 2));
    }

    [Fact]
    public void FallBack_BusinessHoursStayAtLocalWallClock()
    {
        var ny = Zone("America/New_York");
        if (ny is null) return;
        var cal = new BusinessCalendar(ny, TimeSpan.FromHours(8), TimeSpan.FromHours(17), true, Array.Empty<DateOnly>());
        // Fri 30 Oct 2026 16:00 EDT (20:00Z) +2h -> Mon 2 Nov 09:00 EST (14:00Z).
        var from = new DateTimeOffset(2026, 10, 30, 20, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 14, 0, 0, TimeSpan.Zero), cal.AddBusinessHours(from, 2));
    }

    [Fact]
    public void SpringForward_NonexistentLocalTime_DoesNotGoBackwardsOrThrow()
    {
        var ny = Zone("America/New_York");
        if (ny is null) return;
        var cal = new BusinessCalendar(ny, TimeSpan.Zero, TimeSpan.FromHours(24), false, Array.Empty<DateOnly>());
        // Sat 7 Mar 23:00 EST = Sun 8 Mar 04:00Z. +3h of wall clock lands on 02:00 on the 8th, which does not exist.
        var from = new DateTimeOffset(2026, 3, 8, 4, 0, 0, TimeSpan.Zero);
        var result = cal.AddBusinessHours(from, 3);
        Assert.True(result > from);
        Assert.True(result - from <= TimeSpan.FromHours(4));
    }

    // ------------------------------------------------------------ calendar days + test clock

    [Fact]
    public void CalendarDays_IgnoreWeekendsAndHours() =>
        Assert.Equal(At(10, 9), Utc.AddCalendarDays(At(7, 9), 3)); // Saturday 09:00 + 3 days

    [Fact]
    public void FastTestClock_CompressesAnHourIntoAMinuteAndIgnoresWeekends()
    {
        var fast = BusinessCalendar.FastTestClock();
        Assert.Equal(At(7, 10, 2), fast.AddBusinessHours(At(7, 10), 2));                // Saturday works
        Assert.Equal(At(7, 10, 24), fast.AddBusinessDays(At(7, 10), 1));                // 1 business day = 24 minutes
        Assert.Equal(At(7, 10, 24), fast.AddCalendarDays(At(7, 10), 1));                // 1 calendar day = 24 minutes
    }

    // ------------------------------------------------------------ holiday setting

    [Fact]
    public void ParseHolidays_AcceptsCommasSemicolonsNewlines_DedupesAndSorts()
    {
        var (dates, invalid) = BusinessCalendar.ParseHolidays("2026-12-25, 2026-01-01;2026-12-25\r\n2026-06-12\n");
        Assert.Empty(invalid);
        Assert.Equal(new[] { new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 12), new DateOnly(2026, 12, 25) }, dates);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("  , ; \n")]
    public void ParseHolidays_EmptyInput_GivesNothing(string? text)
    {
        var (dates, invalid) = BusinessCalendar.ParseHolidays(text);
        Assert.Empty(dates);
        Assert.Empty(invalid);
    }

    [Fact]
    public void ParseHolidays_ReportsInvalidTokens()
    {
        var (dates, invalid) = BusinessCalendar.ParseHolidays("2026-12-25, 25/12/2026, 2026-02-30, christmas");
        Assert.Single(dates);
        Assert.Equal(new[] { "25/12/2026", "2026-02-30", "christmas" }, invalid);
    }

    [Fact]
    public async Task LoadAsync_UsesHolidaySetting_WhenNoPolicyExists()
    {
        using var db = TestDbContext.CreateNew();
        db.SystemSettings.Add(new SystemSetting { Key = SystemSettingKeys.SupervisionHolidays, Value = "2026-11-09" });
        await db.SaveChangesAsync();

        var cal = await BusinessCalendar.LoadAsync(db, CancellationToken.None);

        Assert.False(cal.IsBusinessDay(new DateOnly(2026, 11, 9)));
        Assert.Equal(At(10, 9), cal.AddBusinessHours(At(6, 16), 2));
    }

    [Fact]
    public async Task LoadAsync_MergesPolicyHolidaysWithSettingHolidays_AndUsesPolicyZoneAndHours()
    {
        using var db = TestDbContext.CreateNew();
        var policy = new ReminderPolicy
        {
            Name = "Default", Enabled = true, IsDefault = true, TimeZoneId = "UTC",
            BusinessHoursStart = TimeSpan.FromHours(9), BusinessHoursEnd = TimeSpan.FromHours(12),
        };
        policy.Holidays.Add(new ReminderPolicyHoliday { ReminderPolicyId = policy.Id, Date = new DateOnly(2026, 11, 3) });
        db.ReminderPolicies.Add(policy);
        db.SystemSettings.Add(new SystemSetting { Key = SystemSettingKeys.SupervisionHolidays, Value = "2026-11-04" });
        await db.SaveChangesAsync();

        var cal = await BusinessCalendar.LoadAsync(db, CancellationToken.None);

        Assert.False(cal.IsBusinessDay(new DateOnly(2026, 11, 3)));
        Assert.False(cal.IsBusinessDay(new DateOnly(2026, 11, 4)));
        Assert.Equal(TimeSpan.FromHours(3), cal.BusinessDayLength);
        Assert.Equal(At(5, 10), cal.AddBusinessHours(At(2, 11), 2)); // Mon 11:00 +1h to 12:00, then Tue/Wed off, Thu 09:00 +1h
    }

    [Fact]
    public async Task LoadAsync_WithoutPolicy_UsesTheDefaultTimeZoneSetting()
    {
        if (Zone("Asia/Manila") is null) return;
        using var db = TestDbContext.CreateNew();
        db.SystemSettings.Add(new SystemSetting { Key = SystemSettingKeys.DefaultTimeZone, Value = "Asia/Manila" });
        await db.SaveChangesAsync();

        var cal = await BusinessCalendar.LoadAsync(db, CancellationToken.None);

        Assert.Equal(At(2, 1), cal.AddBusinessHours(At(2, 0), 1)); // 08:00 Manila opening
    }

    [Fact]
    public async Task HolidaySetting_IsValidatedAndNormalised()
    {
        using var db = TestDbContext.CreateNew();
        var service = new SystemSettingsService(db, new FakeCurrentUserService(), new NoOpAuditService());

        var bad = await service.UpdateAsync(new UpdateSystemSettingsRequest(new() { [SystemSettingKeys.SupervisionHolidays] = "2026-12-25, tomorrow" }), CancellationToken.None);
        Assert.False(bad.Succeeded);

        var ok = await service.UpdateAsync(new UpdateSystemSettingsRequest(new() { [SystemSettingKeys.SupervisionHolidays] = "2026-12-26\n2026-12-25\n2026-12-25" }), CancellationToken.None);
        Assert.True(ok.Succeeded);
        Assert.Equal("2026-12-25, 2026-12-26", await SystemSettingsService.GetValueAsync(db, SystemSettingKeys.SupervisionHolidays, CancellationToken.None));
    }

    // ------------------------------------------------------------ the older reminder timing helper (DST)

    [Fact]
    public void ReminderTiming_AfterClosing_MovesToNextLocalOpening_AcrossDst()
    {
        var ny = Zone("America/New_York");
        if (ny is null) return;
        var policy = new ReminderPolicy
        {
            TimeZoneId = ny.Id, RestrictToBusinessHours = true, ExcludeWeekends = false,
            BusinessHoursStart = TimeSpan.FromHours(8), BusinessHoursEnd = TimeSpan.FromHours(17),
        };
        // Saturday 7 Mar 2026 18:00 EST (23:00Z): after closing. Next opening is Sunday 8 Mar 08:00 EDT (12:00Z),
        // not 24 UTC hours after Saturday's opening (which would be 09:00 EDT).
        var candidate = new DateTimeOffset(2026, 3, 7, 23, 0, 0, TimeSpan.Zero);
        var adjusted = ReminderTimingCalculator.AdjustToAllowedWindow(policy, candidate, Array.Empty<DateOnly>());
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 12, 0, 0, TimeSpan.Zero), adjusted);
    }

    [Fact]
    public void ReminderTiming_InsideWindow_IsUnchanged_AndWeekendsAndHolidaysMove()
    {
        var policy = new ReminderPolicy { TimeZoneId = "UTC", RestrictToBusinessHours = true, ExcludeWeekends = true };
        Assert.Equal(At(2, 10), ReminderTimingCalculator.AdjustToAllowedWindow(policy, At(2, 10), Array.Empty<DateOnly>()));
        Assert.Equal(At(9, 8), ReminderTimingCalculator.AdjustToAllowedWindow(policy, At(7, 10), Array.Empty<DateOnly>()));
        Assert.Equal(At(10, 8), ReminderTimingCalculator.AdjustToAllowedWindow(policy, At(9, 10), new[] { new DateOnly(2026, 11, 9) }));
        Assert.Equal(At(3, 8), ReminderTimingCalculator.AdjustToAllowedWindow(policy, At(2, 17), Array.Empty<DateOnly>())); // exactly at closing
    }

    [Fact]
    public void ReminderTiming_UnknownTimeZone_FallsBackToUtc() =>
        Assert.Equal(TimeZoneInfo.Utc, ReminderTimingCalculator.ResolveTimeZone("Not/AZone"));
}
