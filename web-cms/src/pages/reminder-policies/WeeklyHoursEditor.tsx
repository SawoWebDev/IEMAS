import { useState } from "react";
import { DAYS, formatWeeklyHours, parseWeeklyHours, type DayHours } from "./weeklyHours";

interface Props {
  value: string;
  onChange: (value: string) => void;
}

/** A row per weekday: working or not, and from when to when. */
export default function WeeklyHoursEditor({ value, onChange }: Props) {
  const [days, setDays] = useState<DayHours[]>(() => parseWeeklyHours(value));

  function update(index: number, patch: Partial<DayHours>) {
    const next = days.map((d, i) => (i === index ? { ...d, ...patch } : d));
    // At least one working day: an empty schedule would mean "no deadlines ever".
    if (!next.some((d) => d.on)) return;
    setDays(next);
    onChange(formatWeeklyHours(next));
  }

  return (
    <fieldset style={{ width: "100%", border: "1px solid var(--color-border, #ccc)", borderRadius: 6, padding: 12 }}>
      <legend style={{ fontSize: 13 }}>Working hours</legend>
      <p style={{ fontSize: 12, color: "var(--color-text-muted)", margin: "0 0 8px" }}>
        Deadlines count only these hours, and reminders and escalations are sent only then. If people sometimes stay later
        on a day, enter the earlier closing time: nobody is then chased for leaving on time, and a late reply still counts.
      </p>
      {DAYS.map((day, i) => (
        <div key={day} style={{ display: "flex", gap: 10, alignItems: "center", marginBottom: 4 }}>
          <label style={{ width: 80, display: "flex", gap: 6, alignItems: "center" }}>
            <input type="checkbox" checked={days[i].on} onChange={(e) => update(i, { on: e.target.checked })} /> {day}
          </label>
          {days[i].on ? (
            <>
              <input type="time" aria-label={`${day} start`} required value={days[i].start} onChange={(e) => update(i, { start: e.target.value })} />
              <span>to</span>
              <input type="time" aria-label={`${day} end`} required value={days[i].end} onChange={(e) => update(i, { end: e.target.value })} />
            </>
          ) : (
            <span style={{ fontSize: 12, color: "var(--color-text-muted)" }}>Not a working day</span>
          )}
        </div>
      ))}
    </fieldset>
  );
}
