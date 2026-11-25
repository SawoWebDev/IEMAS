using Iemas.Application.Common.Interfaces;
using Iemas.Application.Operations;
using Iemas.Application.Reminders;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Inquiries;

/// <summary>
/// Q1 — agent response and resolution deadlines count business time only: business hours, no
/// weekends, no configured holidays. Customer waits use calendar days and do not use this class.
///
/// The calendar comes from the default Reminder Policy (time zone, hours, weekends, holidays), so
/// holidays are managed in one place in the CMS. Without a default policy: Mon-Fri 08:00-17:00 in
/// the system's default time zone. Shorter working days ("Fri 16:00") come from their own setting.
/// </summary>
public sealed class BusinessCalendar
{
    private const int MaxDays = 3660;

    private readonly TimeZoneInfo _zone;
    private readonly TimeSpan _start;
    private readonly TimeSpan _end;
    private readonly bool _excludeWeekends;
    private readonly HashSet<DateOnly> _holidays;
    private readonly IReadOnlyDictionary<DayOfWeek, (TimeSpan? Start, TimeSpan End)> _dayHours;
    private readonly double _scale = 1.0;

    /// <param name="dayHours">
    /// Working hours that differ on some weekdays, e.g. Friday 07:00-16:00 while Monday-Thursday run 07:00-18:00
    /// (the "Shorter working days" setting). Days not listed use <paramref name="start"/>-<paramref name="end"/>.
    /// </param>
    public BusinessCalendar(TimeZoneInfo zone, TimeSpan start, TimeSpan end, bool excludeWeekends, IEnumerable<DateOnly> holidays,
        IReadOnlyDictionary<DayOfWeek, (TimeSpan? Start, TimeSpan End)>? dayHours = null)
    {
        _zone = zone;
        _start = start;
        _end = end > start ? end : start + TimeSpan.FromHours(1);
        _excludeWeekends = excludeWeekends;
        _holidays = holidays.ToHashSet();
        _dayHours = dayHours ?? new Dictionary<DayOfWeek, (TimeSpan? Start, TimeSpan End)>();
    }

    /// <summary>Business hours on a given weekday: the regular hours unless a shorter day is configured for it.</summary>
    public (TimeSpan Start, TimeSpan End) HoursOn(DayOfWeek day)
    {
        if (!_dayHours.TryGetValue(day, out var hours)) return (_start, _end);
        var start = hours.Start ?? _start;
        return hours.End > start ? (start, hours.End) : (_start, _end);
    }

    /// <summary>
    /// Length of one regular business day, e.g. 11 hours for 07:00-18:00. "1 business day" means this much business
    /// time, so on a week with a shorter Friday a deadline of 1 business day can reach into the next morning.
    /// </summary>
    public TimeSpan BusinessDayLength => _end - _start;

    /// <summary>
    /// Test mode only: round the clock, every day, and 60 times faster (1 hour of configured time = 1 minute,
    /// 1 business day = 24 minutes). Lets a person see reminders and escalation within minutes.
    /// </summary>
    public static BusinessCalendar FastTestClock() =>
        new(TimeZoneInfo.Utc, TimeSpan.Zero, TimeSpan.FromHours(24), false, Array.Empty<DateOnly>(), 1.0 / 60);

    /// <summary>
    /// Practice inquiries (the employee's first-run training email): round the clock and 180 times faster
    /// (1 business hour = 20 seconds), so with the normal settings the employee sees the reminder, the
    /// escalation warning and the escalation within about 6 minutes instead of 1.5 working days.
    /// </summary>
    public static BusinessCalendar PracticeClock() =>
        new(TimeZoneInfo.Utc, TimeSpan.Zero, TimeSpan.FromHours(24), false, Array.Empty<DateOnly>(), 1.0 / 180);

    private BusinessCalendar(TimeZoneInfo zone, TimeSpan start, TimeSpan end, bool excludeWeekends, IEnumerable<DateOnly> holidays, double scale)
        : this(zone, start, end, excludeWeekends, holidays) => _scale = scale;

    public static readonly BusinessCalendar Default = new(TimeZoneInfo.Utc, TimeSpan.FromHours(8), TimeSpan.FromHours(17), true, Array.Empty<DateOnly>());

    public static async Task<BusinessCalendar> LoadAsync(IAppDbContext db, CancellationToken cancellationToken)
    {
        var policy = await db.ReminderPolicies.AsNoTracking().Include(p => p.Holidays)
            .Where(p => p.Enabled && p.IsDefault)
            .FirstOrDefaultAsync(cancellationToken);
        var settingHolidays = ParseHolidays(await SystemSettingsService.GetValueAsync(db, SystemSettingKeys.SupervisionHolidays, cancellationToken)).Dates;
        var shorterDays = ParseShorterDays(await SystemSettingsService.GetValueAsync(db, SystemSettingKeys.ShorterWorkingDays, cancellationToken)).Days;

        if (policy is null)
        {
            var zone = await SystemSettingsService.GetDefaultTimeZoneAsync(db, cancellationToken);
            return new BusinessCalendar(zone, TimeSpan.FromHours(8), TimeSpan.FromHours(17), true, settingHolidays, shorterDays);
        }

        var start = policy.RestrictToBusinessHours ? policy.BusinessHoursStart : TimeSpan.Zero;
        var end = policy.RestrictToBusinessHours ? policy.BusinessHoursEnd : TimeSpan.FromHours(24);
        return new BusinessCalendar(ReminderTimingCalculator.ResolveTimeZone(policy.TimeZoneId), start, end, policy.ExcludeWeekends,
            policy.Holidays.Select(h => h.Date).Concat(settingHolidays),
            policy.RestrictToBusinessHours ? shorterDays : null);
    }

    /// <summary>
    /// Parses the "Shorter working days" setting: entries separated by commas or new lines, each a weekday and
    /// the time work ends, optionally with its own start: "Fri 16:00" or "Friday 07:00-16:00". A null start means
    /// the regular start time. Returns the parsed days, every entry that could not be read, and the entries in
    /// their stored form ("Fri 16:00").
    /// </summary>
    public static (Dictionary<DayOfWeek, (TimeSpan? Start, TimeSpan End)> Days, List<string> Invalid, List<string> Normalized) ParseShorterDays(string? text)
    {
        var days = new Dictionary<DayOfWeek, (TimeSpan? Start, TimeSpan End)>();
        var invalid = new List<string>();
        foreach (var token in (text ?? "").Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = System.Text.RegularExpressions.Regex.Match(token,
                @"^(?<day>[A-Za-z]{3,9})\.?\s+(?:(?<start>\d{1,2}:\d{2})\s*-\s*)?(?<end>\d{1,2}:\d{2})$");
            if (!m.Success || ParseDay(m.Groups["day"].Value) is not DayOfWeek day
                || !TryParseClock(m.Groups["end"].Value, out var end) || end <= TimeSpan.Zero)
            {
                invalid.Add(token);
                continue;
            }
            TimeSpan? start = null;
            if (m.Groups["start"].Success)
            {
                if (!TryParseClock(m.Groups["start"].Value, out var s) || s >= end)
                {
                    invalid.Add(token);
                    continue;
                }
                start = s;
            }
            days[day] = (start, end);
        }
        var normalized = days.OrderBy(d => ((int)d.Key + 6) % 7)
            .Select(d => d.Value.Start is TimeSpan st
                ? $@"{d.Key.ToString()[..3]} {st:hh\:mm}-{d.Value.End:hh\:mm}"
                : $@"{d.Key.ToString()[..3]} {d.Value.End:hh\:mm}")
            .ToList();
        return (days, invalid, normalized);
    }

    private static bool TryParseClock(string text, out TimeSpan value) =>
        TimeSpan.TryParseExact(text, @"h\:mm", System.Globalization.CultureInfo.InvariantCulture, out value) && value <= TimeSpan.FromHours(24);

    private static DayOfWeek? ParseDay(string text)
    {
        var t = text.ToLowerInvariant();
        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            var name = day.ToString().ToLowerInvariant();
            if (t == name || (t.Length >= 3 && name.StartsWith(t, StringComparison.Ordinal))) return day;
        }
        return null;
    }

    /// <summary>
    /// Parses the "Company holidays" setting: ISO dates (yyyy-MM-dd) separated by commas, semicolons or
    /// new lines. Returns the distinct dates in order, and every token that is not a valid date.
    /// </summary>
    public static (List<DateOnly> Dates, List<string> Invalid) ParseHolidays(string? text)
    {
        var dates = new List<DateOnly>();
        var invalid = new List<string>();
        foreach (var token in (text ?? "").Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (DateOnly.TryParseExact(token, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d))
            {
                if (!dates.Contains(d)) dates.Add(d);
            }
            else
            {
                invalid.Add(token);
            }
        }
        dates.Sort();
        return (dates, invalid);
    }

    public bool IsBusinessDay(DateOnly localDate) =>
        !(_excludeWeekends && localDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) && !_holidays.Contains(localDate);

    /// <summary>The instant <paramref name="amount"/> of business time after <paramref name="fromUtc"/>. Time outside business hours does not count.</summary>
    public DateTimeOffset AddBusinessTime(DateTimeOffset fromUtc, TimeSpan amount)
    {
        if (amount <= TimeSpan.Zero) return fromUtc;
        amount = TimeSpan.FromTicks((long)(amount.Ticks * _scale));

        var local = TimeZoneInfo.ConvertTime(fromUtc, _zone);
        var date = DateOnly.FromDateTime(local.DateTime);
        var timeOfDay = local.TimeOfDay;
        var remaining = amount;

        for (var i = 0; i < MaxDays; i++)
        {
            if (IsBusinessDay(date))
            {
                var (dayStart, dayEnd) = HoursOn(date.DayOfWeek);
                var windowStart = timeOfDay < dayStart ? dayStart : timeOfDay;
                if (windowStart < dayEnd)
                {
                    var available = dayEnd - windowStart;
                    if (remaining <= available)
                    {
                        return ToUtc(date, windowStart + remaining);
                    }
                    remaining -= available;
                }
            }

            date = date.AddDays(1);
            timeOfDay = TimeSpan.Zero;
        }

        return fromUtc + amount;
    }

    public DateTimeOffset AddBusinessDays(DateTimeOffset fromUtc, int days) =>
        AddBusinessTime(fromUtc, TimeSpan.FromTicks(BusinessDayLength.Ticks * days));

    public DateTimeOffset AddBusinessHours(DateTimeOffset fromUtc, double hours) =>
        AddBusinessTime(fromUtc, TimeSpan.FromHours(hours));

    /// <summary>Plain calendar days (customer waits). Only shortened in test mode.</summary>
    public DateTimeOffset AddCalendarDays(DateTimeOffset fromUtc, int days) =>
        fromUtc + TimeSpan.FromTicks((long)(TimeSpan.FromDays(days).Ticks * _scale));

    private DateTimeOffset ToUtc(DateOnly date, TimeSpan timeOfDay)
    {
        var unspecified = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue) + timeOfDay, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, _zone.GetUtcOffset(unspecified)).ToUniversalTime();
    }
}

/// <summary>All Q1/Q2 supervision settings, read once per run.</summary>
public sealed record SupervisionSettings(
    bool Enabled,
    int FirstResponseBusinessHours,
    int ReminderIntervalBusinessHours,
    int RemindersBeforeEscalation,
    int IdleBusinessDays,
    int CustomerWaitCalendarDays,
    int InternalWaitBusinessDays,
    int ResolutionTargetBusinessDays,
    int EscalationRepeatBusinessDays,
    bool AiResolutionEnabled,
    int AiResolutionMinConfidencePercent,
    int AgentReplyRetentionDays,
    bool TestClockMinutes = false)
{
    /// <summary>The product owner's Q1/Q2 defaults - also what an empty settings table yields.</summary>
    public static readonly SupervisionSettings Defaults = new(true, 8, 3, 2, 1, 5, 2, 10, 2, true, 80, 365);

    public static async Task<SupervisionSettings> LoadAsync(IAppDbContext db, CancellationToken cancellationToken)
    {
        Task<int> Int(string key) => SystemSettingsService.GetIntAsync(db, key, cancellationToken);
        return new SupervisionSettings(
            await SystemSettingsService.GetBoolAsync(db, SystemSettingKeys.SupervisionEnabled, cancellationToken),
            await Int(SystemSettingKeys.FirstResponseBusinessHours),
            await Int(SystemSettingKeys.ReminderIntervalBusinessHours),
            await Int(SystemSettingKeys.RemindersBeforeEscalation),
            await Int(SystemSettingKeys.IdleBusinessDays),
            await Int(SystemSettingKeys.CustomerWaitCalendarDays),
            await Int(SystemSettingKeys.InternalWaitBusinessDays),
            await Int(SystemSettingKeys.ResolutionTargetBusinessDays),
            await Int(SystemSettingKeys.EscalationRepeatBusinessDays),
            await SystemSettingsService.GetBoolAsync(db, SystemSettingKeys.AiResolutionEnabled, cancellationToken),
            await Int(SystemSettingKeys.AiResolutionMinConfidence),
            await Int(SystemSettingKeys.AgentReplyRetentionDays),
            await SystemSettingsService.GetBoolAsync(db, SystemSettingKeys.TestClockMinutes, cancellationToken));
    }
}
