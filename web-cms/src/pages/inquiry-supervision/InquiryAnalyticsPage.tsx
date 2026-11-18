import { useEffect, useState } from "react";
import { apiClient } from "../../api/client";
import type { EmployeeDto } from "../../api/types";
import type { DurationStatsDto, InquiryAnalyticsDto } from "../../api/analyticsTypes";
import StatCard from "../../components/StatCard";
import { InfoTip } from "../../components/help/Tooltip";

function mins(v: number | null) {
  if (v == null) return "—";
  if (v < 120) return `${v.toFixed(0)} min`;
  if (v < 60 * 48) return `${(v / 60).toFixed(1)} h`;
  return `${(v / 1440).toFixed(1)} d`;
}

function pct(v: number | null) {
  return v == null ? "—" : `${(v * 100).toFixed(0)}%`;
}

function Stats({ s }: { s: DurationStatsDto }) {
  return <>{mins(s.averageMinutes)} avg · {mins(s.medianMinutes)} median · {mins(s.p90Minutes)} p90</>;
}

function isoDay(d: Date) {
  return d.toISOString().slice(0, 10);
}

/// Inquiry analytics: volume, backlog, escalations, response/resolution times (wall-clock) and AI accuracy.
export default function InquiryAnalyticsPage() {
  const [from, setFrom] = useState(isoDay(new Date(Date.now() - 30 * 86400000)));
  const [to, setTo] = useState(isoDay(new Date()));
  const [granularity, setGranularity] = useState("day");
  const [employeeId, setEmployeeId] = useState("");
  const [employees, setEmployees] = useState<EmployeeDto[]>([]);
  const [data, setData] = useState<InquiryAnalyticsDto | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    apiClient.get<EmployeeDto[]>("/employees")
      .then((r) => setEmployees(r.data.filter((e) => e.isActive)))
      .catch(() => setEmployees([]));
  }, []);

  useEffect(() => {
    const params: Record<string, string> = { from: `${from}T00:00:00Z`, to: `${to}T23:59:59Z`, granularity };
    if (employeeId) params.employeeId = employeeId;
    setError(null);
    apiClient.get<InquiryAnalyticsDto>("/inquiries/analytics", { params })
      .then((r) => setData(r.data))
      .catch(() => setError("Failed to load inquiry analytics."));
  }, [from, to, granularity, employeeId]);

  const maxVolume = Math.max(1, ...(data?.volume ?? []).map((v) => Math.max(v.received, v.resolved)));

  return (
    <div>
      <h1>Inquiry Analytics</h1>
      <p style={{ color: "var(--color-text-muted)", fontSize: 13 }}>
        Inquiries whose first email arrived in the chosen dates. Times are wall-clock (calendar) time, not business hours.
      </p>

      <div style={{ display: "flex", gap: 12, flexWrap: "wrap", alignItems: "end", marginBottom: 16 }}>
        <label>From <input type="date" value={from} onChange={(e) => setFrom(e.target.value)} /></label>
        <label>To <input type="date" value={to} onChange={(e) => setTo(e.target.value)} /></label>
        <label>Group by
          <select value={granularity} onChange={(e) => setGranularity(e.target.value)}>
            <option value="day">Day</option>
            <option value="week">Week</option>
          </select>
        </label>
        <label>Agent
          <select value={employeeId} onChange={(e) => setEmployeeId(e.target.value)}>
            <option value="">All agents</option>
            {employees.map((e) => <option key={e.id} value={e.id}>{e.fullName}</option>)}
          </select>
        </label>
      </div>

      {error && <div className="form-error">{error}</div>}

      {data && (
        <>
          <div className="stat-grid">
            <StatCard label="Inquiries" value={data.total} help="Inquiries that started in this period." />
            <StatCard label="Open" value={data.open} help="Still being supervised." />
            <StatCard label="Resolved" value={data.resolved} tone="good" help="Confirmed resolved by the agent." />
            <StatCard label="Overdue" value={data.overdue} tone={data.overdue > 0 ? "bad" : "neutral"} help="Open and past the resolution target." />
            <StatCard label="Escalations" value={data.escalationsTotal} tone={data.escalationsTotal > 0 ? "warn" : "neutral"}
              hint={data.escalationsByLevel.map((l) => `L${l.level}: ${l.count}`).join(" · ") || "none"}
              help="Times management was emailed, by escalation level." />
            <StatCard label="Reopened" value={data.reopenedCases} hint={`${data.resolutionRejections} AI resolutions rejected`}
              help="Inquiries reopened after completion, and times an agent said the AI's 'resolved' suggestion was wrong." />
          </div>

          <div className="section-title">Response and resolution time</div>
          <table style={{ width: "100%", borderCollapse: "separate", borderSpacing: 0 }}>
            <tbody>
              <tr><td style={{ fontWeight: 600, width: 220 }}>First response</td><td><Stats s={data.firstResponse} /> ({data.firstResponse.count} inquiries)</td></tr>
              <tr><td style={{ fontWeight: 600 }}>Resolution</td><td><Stats s={data.resolution} /> ({data.resolution.count} inquiries)</td></tr>
            </tbody>
          </table>

          <div className="section-title">Volume over time<InfoTip text="Blue: inquiries received. Green: inquiries resolved in that period." /></div>
          <div style={{ display: "flex", alignItems: "end", gap: 3, height: 120, overflowX: "auto" }} role="img" aria-label="Inquiry volume over time">
            {data.volume.map((v) => (
              <div key={v.bucketStart} title={`${new Date(v.bucketStart).toLocaleDateString()}: ${v.received} received, ${v.resolved} resolved`}
                style={{ display: "flex", alignItems: "end", gap: 1, minWidth: 12 }}>
                <div style={{ width: 5, height: `${(v.received / maxVolume) * 110}px`, background: "var(--color-primary, #2563eb)" }} />
                <div style={{ width: 5, height: `${(v.resolved / maxVolume) * 110}px`, background: "var(--color-success, #16a34a)" }} />
              </div>
            ))}
          </div>

          <div className="section-title">Per agent</div>
          <table style={{ width: "100%", borderCollapse: "separate", borderSpacing: 0 }}>
            <thead>
              <tr><th style={{ textAlign: "left" }}>Agent</th><th>Inquiries</th><th>Responded</th><th style={{ textAlign: "left" }}>First response</th><th style={{ textAlign: "left" }}>Resolution</th><th>Open</th><th>Overdue</th></tr>
            </thead>
            <tbody>
              {data.employees.map((e) => (
                <tr key={e.employeeId ?? "none"}>
                  <td style={{ fontWeight: 600 }}>{e.agentName}</td>
                  <td style={{ textAlign: "center" }}>{e.inquiries}</td>
                  <td style={{ textAlign: "center" }}>{e.responded}</td>
                  <td><Stats s={e.firstResponse} /></td>
                  <td><Stats s={e.resolution} /></td>
                  <td style={{ textAlign: "center" }}>{e.open}</td>
                  <td style={{ textAlign: "center" }}>{e.overdue}</td>
                </tr>
              ))}
            </tbody>
          </table>

          <div className="section-title">AI accuracy</div>
          <div className="stat-grid">
            <StatCard label="Resolution suggestions accepted" value={pct(data.resolutionAi.acceptanceRate)}
              hint={`${data.resolutionAi.confirmedCases} confirmed · ${data.resolutionAi.rejectedCases} rejected · ${data.resolutionAi.pendingCases} waiting`}
              help="When the AI said an inquiry looks resolved, how often the agent agreed." />
            <StatCard label="Classification accuracy" value={pct(data.classification.accuracy)}
              hint={`${data.classification.manuallyCorrected} corrected · ${data.classification.falsePositiveCases} closed as incorrect, of ${data.classification.confidentDecisions}`}
              help="Confident AI decisions that nobody had to correct or close as 'Incorrect classification'." />
          </div>
        </>
      )}
    </div>
  );
}
