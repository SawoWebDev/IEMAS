/** Working hours per weekday, in the server's text form: "Mon-Thu 07:00-18:00, Fri 07:00-16:00". */
export const SAWO_HOURS = "Mon-Thu 07:00-18:00, Fri 07:00-16:00";

export const DAYS = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"] as const;

export interface DayHours {
  on: boolean;
  start: string;
  end: string;
}

/** The server stores the canonical form, so only that form needs reading here. */
export function parseWeeklyHours(text: string): DayHours[] {
  const days: DayHours[] = DAYS.map(() => ({ on: false, start: "07:00", end: "18:00" }));
  for (const part of text.split(",")) {
    const m = part.trim().match(/^(\w{3})(?:-(\w{3}))? (\d{2}:\d{2})-(\d{2}:\d{2})$/);
    if (!m) continue;
    const from = DAYS.indexOf(m[1] as (typeof DAYS)[number]);
    const to = DAYS.indexOf((m[2] ?? m[1]) as (typeof DAYS)[number]);
    for (let i = from; i >= 0 && i <= to; i++) days[i] = { on: true, start: m[3], end: m[4] };
  }
  return days;
}

/** One entry per working day; the server joins neighbouring days that share hours. */
export function formatWeeklyHours(days: DayHours[]): string {
  return days
    .map((d, i) => (d.on ? `${DAYS[i]} ${d.start}-${d.end}` : null))
    .filter(Boolean)
    .join(", ");
}
