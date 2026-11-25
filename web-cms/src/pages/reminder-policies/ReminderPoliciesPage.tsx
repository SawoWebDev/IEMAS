import { useEffect, useState } from "react";
import { apiClient } from "../../api/client";
import type { ClassificationProfileDto, ReminderPolicyDto } from "../../api/types";
import { InfoTip, TipLabel } from "../../components/help/Tooltip";
import WeeklyHoursEditor from "./WeeklyHoursEditor";
import { SAWO_HOURS } from "./weeklyHours";

const EMPTY_GUID = "00000000-0000-0000-0000-000000000000";

interface HolidayRow {
  date: string;
  label: string;
}

function timeSpanToHours(ts: string): number {
  const parts = ts.split(":");
  const hours = Number(parts[0] ?? 0);
  const minutes = Number(parts[1] ?? 0);
  return hours + minutes / 60;
}

function hoursToTimeSpan(hours: number): string {
  const totalMinutes = Math.round(hours * 60);
  const h = Math.floor(totalMinutes / 60);
  const m = totalMinutes % 60;
  return `${String(h).padStart(2, "0")}:${String(m).padStart(2, "0")}:00`;
}

interface FormState {
  name: string;
  description: string;
  enabled: boolean;
  isDefault: boolean;
  initialDelayHours: number;
  reminderIntervalHours: number;
  maxReminders: number;
  minimumIntervalHours: number;
  restrictToBusinessHours: boolean;
  businessHoursStart: string;
  businessHoursEnd: string;
  excludeWeekends: boolean;
  /** "" = the same hours every working day. */
  weeklyHours: string;
  timeZoneId: string;
  expirationDays: number | "";
  classificationProfileId: string;
  escalationThresholdReminderCount: number | null;
  holidays: HolidayRow[];
}

const emptyForm: FormState = {
  name: "",
  description: "",
  enabled: true,
  isDefault: false,
  initialDelayHours: 4,
  reminderIntervalHours: 24,
  maxReminders: 3,
  minimumIntervalHours: 1,
  restrictToBusinessHours: true,
  businessHoursStart: "07:00",
  businessHoursEnd: "18:00",
  excludeWeekends: true,
  weeklyHours: SAWO_HOURS,
  timeZoneId: "Asia/Manila",
  expirationDays: 3,
  classificationProfileId: "",
  escalationThresholdReminderCount: null,
  holidays: [],
};

export default function ReminderPoliciesPage() {
  const [policies, setPolicies] = useState<ReminderPolicyDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [form, setForm] = useState<FormState>(emptyForm);
  const [showForm, setShowForm] = useState(false);
  const [profiles, setProfiles] = useState<ClassificationProfileDto[]>([]);

  async function load() {
    setLoading(true);
    try {
      const res = await apiClient.get<ReminderPolicyDto[]>("/reminder-policies");
      setPolicies(res.data);
      try {
        const prof = await apiClient.get<ClassificationProfileDto[]>("/classification-profiles");
        setProfiles(prof.data);
      } catch {
        setProfiles([]);
      }
    } catch {
      setError("Failed to load Reminder Policies.");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    load();
  }, []);

  function startCreate() {
    setForm(emptyForm);
    setEditingId(null);
    setShowForm(true);
  }

  function startEdit(p: ReminderPolicyDto) {
    setForm({
      name: p.name,
      description: p.description ?? "",
      enabled: p.enabled,
      isDefault: p.isDefault,
      initialDelayHours: timeSpanToHours(p.initialDelay),
      reminderIntervalHours: timeSpanToHours(p.reminderInterval),
      maxReminders: p.maxReminders,
      minimumIntervalHours: timeSpanToHours(p.minimumInterval),
      restrictToBusinessHours: p.restrictToBusinessHours,
      businessHoursStart: p.businessHoursStart.slice(0, 5),
      businessHoursEnd: p.businessHoursEnd.slice(0, 5),
      excludeWeekends: p.excludeWeekends,
      weeklyHours: p.weeklyHours ?? "",
      timeZoneId: p.timeZoneId,
      expirationDays: p.expirationWindow ? Math.round(timeSpanToHours(p.expirationWindow) / 24) : "",
      classificationProfileId: p.classificationProfileId ?? "",
      escalationThresholdReminderCount: p.escalationThresholdReminderCount,
      holidays: p.holidays.map((h) => ({ date: h.date, label: h.label ?? "" })),
    });
    setEditingId(p.id);
    setShowForm(true);
  }

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError(null);

    const payload = {
      name: form.name,
      description: form.description || null,
      enabled: form.enabled,
      isDefault: form.isDefault,
      classificationProfileId: form.classificationProfileId || null,
      initialDelay: hoursToTimeSpan(form.initialDelayHours),
      reminderInterval: hoursToTimeSpan(form.reminderIntervalHours),
      maxReminders: form.maxReminders,
      minimumInterval: hoursToTimeSpan(form.minimumIntervalHours),
      restrictToBusinessHours: form.restrictToBusinessHours,
      businessHoursStart: `${form.businessHoursStart}:00`,
      businessHoursEnd: `${form.businessHoursEnd}:00`,
      excludeWeekends: form.excludeWeekends,
      weeklyHours: form.restrictToBusinessHours ? form.weeklyHours || null : null,
      timeZoneId: form.timeZoneId,
      expirationWindow: form.expirationDays === "" ? null : hoursToTimeSpan(Number(form.expirationDays) * 24),
      escalationThresholdReminderCount: form.escalationThresholdReminderCount,
      holidays: form.holidays
        .filter((h) => h.date)
        .map((h) => ({ id: EMPTY_GUID, date: h.date, label: h.label.trim() || null })),
    };

    try {
      if (editingId) {
        await apiClient.put(`/reminder-policies/${editingId}`, payload);
      } else {
        await apiClient.post("/reminder-policies", payload);
      }
      setShowForm(false);
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to save Reminder Policy.");
    }
  }

  async function handleDelete(id: string) {
    setError(null);
    try {
      await apiClient.delete(`/reminder-policies/${id}`);
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to delete Reminder Policy.");
    }
  }

  async function handleToggleEnabled(p: ReminderPolicyDto) {
    setError(null);
    try {
      await apiClient.put(`/reminder-policies/${p.id}`, {
        name: p.name, description: p.description, enabled: !p.enabled, isDefault: p.isDefault,
        classificationProfileId: p.classificationProfileId,
        initialDelay: p.initialDelay, reminderInterval: p.reminderInterval, maxReminders: p.maxReminders,
        minimumInterval: p.minimumInterval, restrictToBusinessHours: p.restrictToBusinessHours,
        businessHoursStart: p.businessHoursStart, businessHoursEnd: p.businessHoursEnd,
        excludeWeekends: p.excludeWeekends, weeklyHours: p.weeklyHours, timeZoneId: p.timeZoneId, expirationWindow: p.expirationWindow,
        escalationThresholdReminderCount: p.escalationThresholdReminderCount,
        holidays: p.holidays.map((h) => ({ id: h.id, date: h.date, label: h.label })),
      });
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to update Reminder Policy.");
    }
  }

  if (loading) return <p>Loading...</p>;

  return (
    <div>
      <h1>Reminder Policies</h1>
      <p style={{ color: "var(--color-text-muted)", fontSize: 13 }}>
        How often the Case owner is reminded by pop-up while no reply has been found in the Sent folder: first reminder,
        time between reminders, maximum number, business hours and whether weekends count. Reminders stop as soon as
        a reply is found. One policy is the default.
      </p>

      {error && <div className="form-error">{error}</div>}

      <table style={{ width: "100%", borderCollapse: "separate", borderSpacing: 0, marginBottom: 24 }}>
        <thead>
          <tr>
            <th style={thStyle}>Name</th>
            <th style={thStyle}>Enabled</th>
            <th style={thStyle}>Default<InfoTip text="The policy used when no other applies. Its business hours, weekends and holidays are also the calendar Inquiry Supervision counts deadlines in." /></th>
            <th style={thStyle}>Initial Delay<InfoTip text="How long after a Case is created before the first reminder, if no reply has been found." /></th>
            <th style={thStyle}>Interval<InfoTip text="Time between one reminder and the next." /></th>
            <th style={thStyle}>Max Reminders<InfoTip text="After this many reminders, no more are sent (escalation may take over)." /></th>
            <th style={thStyle}>Business Hours<InfoTip text="If set, reminders only go out (and time only counts) during these hours." /></th>
            <th style={thStyle}></th>
          </tr>
        </thead>
        <tbody>
          {policies.map((p) => (
            <tr key={p.id}>
              <td style={tdStyle}>
                {p.name}
                {p.description && <div style={{ fontSize: 12, color: "var(--color-text-muted)" }}>{p.description}</div>}
              </td>
              <td style={tdStyle}>{p.enabled ? "Yes" : "No"}</td>
              <td style={tdStyle}>
                {p.isDefault ? "Yes" : ""}
                {p.classificationProfileName && <div style={{ fontSize: 12, color: "var(--color-text-muted)" }}>Profile: {p.classificationProfileName}</div>}
              </td>
              <td style={tdStyle}>{timeSpanToHours(p.initialDelay)}h</td>
              <td style={tdStyle}>{timeSpanToHours(p.reminderInterval)}h</td>
              <td style={tdStyle}>{p.maxReminders}</td>
              <td style={tdStyle}>
                {!p.restrictToBusinessHours ? "Any time" : p.weeklyHours ?? `${p.businessHoursStart.slice(0, 5)}-${p.businessHoursEnd.slice(0, 5)}`}
                {p.excludeWeekends && !(p.restrictToBusinessHours && p.weeklyHours) ? ", no weekends" : ""}
                {p.holidays.length > 0 && `, ${p.holidays.length} holiday${p.holidays.length === 1 ? "" : "s"}`}
              </td>
              <td style={tdStyle}>
                <button onClick={() => startEdit(p)} style={{ marginRight: 8 }} data-tip="Change this policy's timings and business hours.">Edit</button>
                <button onClick={() => handleToggleEnabled(p)} style={{ marginRight: 8 }} data-tip="Turn this policy on or off without deleting it.">{p.enabled ? "Disable" : "Enable"}</button>
                <button onClick={() => handleDelete(p.id)} data-tip="Delete this policy permanently.">Delete</button>
              </td>
            </tr>
          ))}
          {policies.length === 0 && (
            <tr><td style={tdStyle} colSpan={8}>No Reminder Policies configured.</td></tr>
          )}
        </tbody>
      </table>

      {!showForm && <button onClick={startCreate} data-tip="Create a new set of reminder timings.">Add Reminder Policy</button>}

      {showForm && (
        <form onSubmit={handleSubmit} style={{ display: "flex", gap: 12, flexWrap: "wrap", alignItems: "end", maxWidth: 900 }}>
          <h2 style={{ width: "100%", fontSize: 16 }}>{editingId ? "Edit Policy" : "Add Policy"}</h2>
          <div>
            <label><TipLabel tip="A short name, e.g. Standard or Weekend support.">Name</TipLabel></label><br />
            <input value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} required />
          </div>
          <div>
            <label><TipLabel tip="Optional note about when this policy is used.">Description</TipLabel></label><br />
            <input value={form.description} onChange={(e) => setForm({ ...form, description: e.target.value })} />
          </div>
          <div>
            <label><TipLabel tip="Hours to wait after a Case arrives before the first reminder. Example: 4 means the agent gets 4 hours to reply first.">Initial delay (hours)</TipLabel></label><br />
            <input type="number" min={0} step={0.5} value={form.initialDelayHours}
              onChange={(e) => setForm({ ...form, initialDelayHours: Number(e.target.value) })} style={{ width: 80 }} />
          </div>
          <div>
            <label><TipLabel tip="Hours between reminders while there is still no reply.">Reminder interval (hours)</TipLabel></label><br />
            <input type="number" min={0.1} step={0.5} value={form.reminderIntervalHours}
              onChange={(e) => setForm({ ...form, reminderIntervalHours: Number(e.target.value) })} style={{ width: 80 }} />
          </div>
          <div>
            <label><TipLabel tip="Stop reminding after this many. Example: 3.">Max reminders</TipLabel></label><br />
            <input type="number" min={1} value={form.maxReminders}
              onChange={(e) => setForm({ ...form, maxReminders: Number(e.target.value) })} style={{ width: 80 }} />
          </div>
          <div>
            <label><TipLabel tip="Never send two reminders closer together than this, even if other rules would.">Minimum interval (hours)</TipLabel></label><br />
            <input type="number" min={0} step={0.5} value={form.minimumIntervalHours}
              onChange={(e) => setForm({ ...form, minimumIntervalHours: Number(e.target.value) })} style={{ width: 80 }} />
          </div>
          <div>
            <label><TipLabel tip="Stop reminding about a Case after this many days. Leave blank to keep reminding.">Expiration (days, blank = never)</TipLabel></label><br />
            <input type="number" min={0} value={form.expirationDays}
              onChange={(e) => setForm({ ...form, expirationDays: e.target.value === "" ? "" : Number(e.target.value) })} style={{ width: 80 }} />
          </div>
          <div>
            <label><TipLabel tip="Time zone for the business hours, e.g. Europe/Helsinki or Asia/Manila.">Time zone</TipLabel></label><br />
            <input value={form.timeZoneId} onChange={(e) => setForm({ ...form, timeZoneId: e.target.value })} style={{ width: 160 }} />
          </div>
          <div>
            <label><TipLabel tip="Only use this policy for Cases from mailboxes with this Classification Profile. Leave as 'All mailboxes' for a general policy.">Classification Profile</TipLabel></label><br />
            <select value={form.classificationProfileId} onChange={(e) => setForm({ ...form, classificationProfileId: e.target.value })}>
              <option value="">All mailboxes</option>
              {profiles.map((pr) => (
                <option key={pr.id} value={pr.id}>{pr.name}{pr.enabled ? "" : " (disabled)"}</option>
              ))}
            </select>
          </div>
          <div>
            <label><input type="checkbox" checked={form.restrictToBusinessHours}
              onChange={(e) => setForm({ ...form, restrictToBusinessHours: e.target.checked })} /> Restrict to business hours<InfoTip text="Only send reminders, and only count time, during the hours below." /></label>
          </div>
          {form.restrictToBusinessHours && (
            <div>
              <label><input type="checkbox" checked={form.weeklyHours !== ""}
                onChange={(e) => setForm({ ...form, weeklyHours: e.target.checked ? SAWO_HOURS : "" })} /> Different hours per day<InfoTip text="Use this when days differ, for example a shorter Friday." /></label>
            </div>
          )}
          {form.restrictToBusinessHours && form.weeklyHours !== "" && (
            <WeeklyHoursEditor value={form.weeklyHours} onChange={(weeklyHours) => setForm({ ...form, weeklyHours })} />
          )}
          {form.restrictToBusinessHours && form.weeklyHours === "" && (
            <>
              <div>
                <label><TipLabel tip="When the working day starts.">Start</TipLabel></label><br />
                <input type="time" value={form.businessHoursStart} onChange={(e) => setForm({ ...form, businessHoursStart: e.target.value })} />
              </div>
              <div>
                <label><TipLabel tip="When the working day ends.">End</TipLabel></label><br />
                <input type="time" value={form.businessHoursEnd} onChange={(e) => setForm({ ...form, businessHoursEnd: e.target.value })} />
              </div>
            </>
          )}
          {!(form.restrictToBusinessHours && form.weeklyHours !== "") && (
            <div>
              <label><input type="checkbox" checked={form.excludeWeekends}
                onChange={(e) => setForm({ ...form, excludeWeekends: e.target.checked })} /> Exclude weekends<InfoTip text="Saturdays and Sundays do not count and no reminders are sent then." /></label>
            </div>
          )}
          <div>
            <label><input type="checkbox" checked={form.enabled}
              onChange={(e) => setForm({ ...form, enabled: e.target.checked })} /> Enabled</label>
          </div>
          <div>
            <label><input type="checkbox" checked={form.isDefault}
              onChange={(e) => setForm({ ...form, isDefault: e.target.checked })} /> Default policy<InfoTip text="Use this policy whenever no other is chosen. Its calendar is also used by Inquiry Supervision." /></label>
          </div>
          <fieldset style={{ width: "100%", border: "1px solid var(--color-border, #ccc)", borderRadius: 6, padding: 12 }}>
            <legend style={{ fontSize: 13 }}>
              Holidays<InfoTip text="Days that do not count as business time: no reminders are sent and supervision deadlines pause. The default policy's holidays are the calendar Inquiry Supervision uses." />
            </legend>
            {form.holidays.length === 0 && (
              <p style={{ fontSize: 12, color: "var(--color-text-muted)", margin: "0 0 8px" }}>No holidays.</p>
            )}
            {form.holidays.map((h, i) => (
              <div key={i} style={{ display: "flex", gap: 8, alignItems: "center", marginBottom: 6 }}>
                <input type="date" value={h.date} required
                  onChange={(e) => setForm({ ...form, holidays: form.holidays.map((x, j) => (j === i ? { ...x, date: e.target.value } : x)) })} />
                <input placeholder="Label (optional), e.g. Midsummer" value={h.label} style={{ width: 240 }}
                  onChange={(e) => setForm({ ...form, holidays: form.holidays.map((x, j) => (j === i ? { ...x, label: e.target.value } : x)) })} />
                <button type="button" onClick={() => setForm({ ...form, holidays: form.holidays.filter((_, j) => j !== i) })}
                  data-tip="Remove this holiday.">Remove</button>
              </div>
            ))}
            <button type="button" onClick={() => setForm({ ...form, holidays: [...form.holidays, { date: "", label: "" }] })}
              data-tip="Add a day that should not count as business time.">Add holiday</button>
          </fieldset>
          <div style={{ width: "100%" }}>
            <button type="submit" style={{ marginRight: 8 }} data-tip="Save the policy.">Save</button>
            <button type="button" onClick={() => setShowForm(false)}>Cancel</button>
          </div>
        </form>
      )}
    </div>
  );
}

const thStyle: React.CSSProperties = { textAlign: "left" };
const tdStyle: React.CSSProperties = {};
