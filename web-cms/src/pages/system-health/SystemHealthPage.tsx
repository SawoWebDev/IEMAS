import { useEffect, useState } from "react";
import { apiClient } from "../../api/client";
import type { SystemHealthDto } from "../../api/types";
import type { JobMonitoringReport } from "../../api/analyticsTypes";
import StatCard, { Badge, type Tone } from "../../components/StatCard";
import { InfoTip } from "../../components/help/Tooltip";

const STATUS_TONE: Record<string, Tone> = {
  Healthy: "good",
  Degraded: "warn",
  Unhealthy: "bad",
};

const JOB_TONE: Record<string, Tone> = { Healthy: "good", Stale: "warn", Failing: "bad", NotRegistered: "neutral" };

function ago(value: string | null) {
  if (!value) return "never";
  const mins = Math.round((Date.now() - new Date(value).getTime()) / 60000);
  return mins < 1 ? "just now" : mins < 120 ? `${mins} min ago` : `${Math.round(mins / 60)} h ago`;
}

const STATUS_SUMMARY: Record<string, string> = {
  Healthy: "All systems are working normally.",
  Degraded: "Working, but something needs attention — see below.",
  Unhealthy: "Something is not working — see below.",
};

/// Requirements §106 (monitor database, email provider, AI provider, background jobs, queue depth,
/// failed jobs; health states HEALTHY/WARNING/FAILED), §86 (SYSTEM nav → System Health). Polls the
/// authenticated GET /api/v1/system-health, which reuses the same health checks as the
/// unauthenticated /health/ready infra endpoint plus Hangfire job counts.
export default function SystemHealthPage() {
  const [health, setHealth] = useState<SystemHealthDto | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [checkedAt, setCheckedAt] = useState<Date | null>(null);
  const [jobs, setJobs] = useState<JobMonitoringReport | null>(null);

  async function load() {
    setError(null);
    try {
      const res = await apiClient.get<SystemHealthDto>("/system-health");
      setHealth(res.data);
      setCheckedAt(new Date());
      try {
        setJobs((await apiClient.get<JobMonitoringReport>("/system-health/jobs")).data);
      } catch {
        setJobs(null);
      }
    } catch (err: any) {
      setError(err.response?.status === 403
        ? "You do not have permission to view System Health (requires Auditor role or above)."
        : "Failed to load System Health.");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    load();
    const interval = setInterval(load, 30000); // auto-refresh every 30s — this is a live status page
    return () => clearInterval(interval);
  }, []);

  if (loading) return <p>Loading...</p>;

  return (
    <div>
      <h1>System Health</h1>
      <p style={{ color: "var(--color-text-muted)", fontSize: 13 }}>
        Whether the database, mailboxes, AI provider and background jobs are working. Refreshes every 30 seconds.
      </p>

      {error && <div className="form-error">{error}</div>}

      {health && (
        <>
          <div className={`health-banner tone-${STATUS_TONE[health.status] ?? "neutral"}`}>
            <span className="health-dot" aria-hidden="true" />
            <div style={{ flex: 1 }}>
              <div style={{ fontSize: 16, fontWeight: 600 }}>{health.status}</div>
              <div style={{ color: "var(--color-text-muted)", fontSize: 13 }}>
                {STATUS_SUMMARY[health.status] ?? ""}
                {checkedAt && ` Last checked ${checkedAt.toLocaleTimeString()} (${health.totalDurationMs.toFixed(0)} ms).`}
              </div>
            </div>
            <button onClick={load} data-tip="Re-check every part of the system right now.">Refresh now</button>
          </div>

          <div className="section-title" style={{ display: "flex", alignItems: "center" }}>Dependencies<InfoTip text="The services IEMAS needs to work: the database, the mailboxes and the AI provider. Anything not green needs attention." /></div>
          <table style={{ width: "100%", borderCollapse: "separate", borderSpacing: 0 }}>
            <thead>
              <tr>
                <th style={{ ...thStyle, width: 220 }}>Dependency</th>
                <th style={{ ...thStyle, width: 130 }}>Status</th>
                <th style={thStyle}>Details</th>
                <th style={{ ...thStyle, width: 110, textAlign: "right" }}>Response<InfoTip text="How long the service took to answer the health check." /></th>
              </tr>
            </thead>
            <tbody>
              {health.checks.map((c) => (
                <tr key={c.name}>
                  <td style={{ ...tdStyle, fontWeight: 600 }}>{c.name}</td>
                  <td style={tdStyle}><Badge tone={STATUS_TONE[c.status] ?? "neutral"}>{c.status}</Badge></td>
                  <td style={{ ...tdStyle, color: "var(--color-text-muted)" }}>{c.description ?? "—"}</td>
                  <td style={{ ...tdStyle, textAlign: "right", fontVariantNumeric: "tabular-nums" }}>{c.durationMs.toFixed(0)} ms</td>
                </tr>
              ))}
            </tbody>
          </table>

          <div className="section-title" style={{ display: "flex", alignItems: "center" }}>Background jobs<InfoTip text="The automatic tasks that run on a schedule: checking mail, reminders, escalation and so on." /></div>
          <div className="stat-grid">
            <StatCard label="Running now" value={health.processingJobCount ?? "—"} help="Automatic tasks being worked on at this moment."
              hint="jobs being worked on at this moment" />
            <StatCard label="Queued for later" value={health.scheduledJobCount ?? "—"} help="One-off tasks waiting for their start time, e.g. a delayed retry."
              hint="one-off jobs waiting for their start time" />
            <StatCard label="Failed" value={health.failedJobCount ?? "—"} help="Tasks that kept failing after their retries. A non-zero number means something needs fixing; a developer can check the API log."
              tone={(health.failedJobCount ?? 0) > 0 ? "bad" : "neutral"}
              hint={(health.failedJobCount ?? 0) > 0 ? "jobs that gave up after retries — check the API log" : "no failed jobs"} />
          </div>
          {jobs && (
            <>
              <div className="section-title" style={{ display: "flex", alignItems: "center" }}>Scheduled jobs<InfoTip text="Each recurring job: when it last succeeded, how long it takes, and whether it is Healthy, Stale (no success in 3x its interval) or Failing." /></div>
              <table style={{ width: "100%", borderCollapse: "separate", borderSpacing: 0 }}>
                <thead>
                  <tr>
                    <th style={thStyle}>Job</th>
                    <th style={{ ...thStyle, width: 100 }}>Status</th>
                    <th style={thStyle}>Last success</th>
                    <th style={thStyle}>Last failure</th>
                    <th style={{ ...thStyle, textAlign: "right" }}>Last / avg time</th>
                    <th style={{ ...thStyle, textAlign: "right" }}>Failed 24h</th>
                    <th style={{ ...thStyle, textAlign: "right" }}>Queued / retrying</th>
                  </tr>
                </thead>
                <tbody>
                  {jobs.jobs.filter((j) => j.status !== "NotRegistered").map((j) => (
                    <tr key={j.id}>
                      <td style={{ ...tdStyle, fontWeight: 600 }}>{j.label}</td>
                      <td style={tdStyle}><Badge tone={JOB_TONE[j.status] ?? "neutral"}>{j.status}</Badge></td>
                      <td style={tdStyle} title={j.statusReason}>{ago(j.lastSuccessAt)}</td>
                      <td style={{ ...tdStyle, color: "var(--color-text-muted)" }} title={j.lastError ?? undefined}>
                        {j.lastFailureAt ? `${ago(j.lastFailureAt)}${j.lastError ? `: ${j.lastError.slice(0, 60)}` : ""}` : "—"}
                      </td>
                      <td style={{ ...tdStyle, textAlign: "right", fontVariantNumeric: "tabular-nums" }}>
                        {j.lastDurationMs == null ? "—" : `${j.lastDurationMs.toFixed(0)} / ${(j.averageDurationMs ?? 0).toFixed(0)} ms`}
                      </td>
                      <td style={{ ...tdStyle, textAlign: "right" }}>{j.failedLast24h}</td>
                      <td style={{ ...tdStyle, textAlign: "right" }}>{j.enqueuedCount} / {j.retryingCount}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </>
          )}
          <p style={{ color: "var(--color-text-muted)", fontSize: 12, marginTop: 10 }}>
            Regular work — checking mailboxes, AI classification, reply checks, reminders and escalations — runs on its own
            schedule every few minutes and is not counted as queued.
          </p>
        </>
      )}
    </div>
  );
}

const thStyle: React.CSSProperties = { textAlign: "left" };
const tdStyle: React.CSSProperties = { verticalAlign: "middle" };
