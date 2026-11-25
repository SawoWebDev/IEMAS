using System.Globalization;
using System.Text.RegularExpressions;

namespace Iemas.Application.Reminders;

/// <summary>
/// Working hours per weekday, for companies whose days differ in length (for example Monday-Thursday
/// 07:00-18:00 and Friday 07:00-16:00). A day that is not listed is not a working day.
///
/// Text form: entries separated by commas, semicolons or new lines, each "Day HH:mm-HH:mm" or
/// "Day-Day HH:mm-HH:mm", e.g. <c>Mon-Thu 07:00-18:00, Fri 07:00-16:00</c>.
/// </summary>
public sealed class WeeklySchedule
{
    /// <summary>SAWO office hours. Friday closes at 16:00: people who stay later are never chased or escalated for it.</summary>
    public const string SawoDefault = "Mon-Thu 07:00-18:00, Fri 07:00-16:00";

    /// <summary>The time zone SAWO's office hours are written in.</summary>
    public const string SawoTimeZone = "Asia/Manila";

    private static readonly DayOfWeek[] MondayFirst =
        { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday };

    private static readonly Regex Entry = new(
        @"^\s*(?<from>[A-Za-z]{3,9})\s*(?:(?:-|–|to)\s*(?<to>[A-Za-z]{3,9}))?\s*[ =:]\s*(?<start>\d{1,2}:\d{2})\s*(?:-|–|to)\s*(?<end>\d{1,2}:\d{2})\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Dictionary<DayOfWeek, (TimeSpan Start, TimeSpan End)> _days;

    private WeeklySchedule(Dictionary<DayOfWeek, (TimeSpan Start, TimeSpan End)> days) => _days = days;

    /// <summary>The longest working day: what "1 business day" means.</summary>
    public TimeSpan LongestDay => _days.Values.Max(w => w.End - w.Start);

    public bool TryGet(DayOfWeek day, out (TimeSpan Start, TimeSpan End) window) => _days.TryGetValue(day, out window);

    /// <summary>The schedule in <paramref name="text"/>, or null when it is empty or invalid.</summary>
    public static WeeklySchedule? ParseOrNull(string? text) => TryParse(text, out var schedule, out _) ? schedule : null;

    public static bool TryParse(string? text, out WeeklySchedule? schedule, out string? error)
    {
        schedule = null;
        error = null;
        var parts = (text ?? "").Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            error = "Enter at least one working day, for example: " + SawoDefault;
            return false;
        }

        var days = new Dictionary<DayOfWeek, (TimeSpan, TimeSpan)>();
        foreach (var part in parts)
        {
            var m = Entry.Match(part);
            if (!m.Success)
            {
                error = $"\"{part}\" is not understood. Write each entry like \"Mon-Thu 07:00-18:00\" or \"Fri 07:00-16:00\".";
                return false;
            }
            if (!TryDay(m.Groups["from"].Value, out var from) || !TryDay(m.Groups["to"].Success ? m.Groups["to"].Value : m.Groups["from"].Value, out var to))
            {
                error = $"\"{part}\" has an unknown day name. Use Mon, Tue, Wed, Thu, Fri, Sat or Sun.";
                return false;
            }
            if (!TryTime(m.Groups["start"].Value, out var start) || !TryTime(m.Groups["end"].Value, out var end) || end <= start)
            {
                error = $"\"{part}\" has invalid hours: use 24-hour times where the end is after the start.";
                return false;
            }

            var first = Array.IndexOf(MondayFirst, from);
            var last = Array.IndexOf(MondayFirst, to);
            if (last < first)
            {
                error = $"\"{part}\": write the days in week order, Monday first.";
                return false;
            }
            for (var i = first; i <= last; i++)
            {
                if (!days.TryAdd(MondayFirst[i], (start, end)))
                {
                    error = $"{MondayFirst[i]} is listed twice.";
                    return false;
                }
            }
        }

        schedule = new WeeklySchedule(days);
        return true;
    }

    /// <summary>The canonical text, with neighbouring days that share hours joined into one range.</summary>
    public override string ToString()
    {
        var entries = new List<string>();
        for (var i = 0; i < MondayFirst.Length;)
        {
            if (!_days.TryGetValue(MondayFirst[i], out var window))
            {
                i++;
                continue;
            }
            var j = i;
            while (j + 1 < MondayFirst.Length && _days.TryGetValue(MondayFirst[j + 1], out var next) && next == window) j++;
            var name = i == j ? Short(MondayFirst[i]) : $"{Short(MondayFirst[i])}-{Short(MondayFirst[j])}";
            entries.Add($"{name} {window.Start:hh\\:mm}-{window.End:hh\\:mm}");
            i = j + 1;
        }
        return string.Join(", ", entries);
    }

    private static string Short(DayOfWeek day) => day.ToString()[..3];

    private static bool TryDay(string text, out DayOfWeek day)
    {
        foreach (var d in MondayFirst)
        {
            if (d.ToString().StartsWith(text, StringComparison.OrdinalIgnoreCase))
            {
                day = d;
                return true;
            }
        }
        day = default;
        return false;
    }

    private static bool TryTime(string text, out TimeSpan time)
    {
        if (text == "24:00")
        {
            time = TimeSpan.FromHours(24);
            return true;
        }
        return TimeSpan.TryParseExact(text, new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out time);
    }
}
