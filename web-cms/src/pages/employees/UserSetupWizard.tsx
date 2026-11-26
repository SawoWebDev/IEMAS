import { useCallback, useEffect, useState } from "react";
import { apiClient } from "../../api/client";
import type { AgentDto, EmailAccountDto, EmployeeDto, TestConnectionResult } from "../../api/types";
import AgentInstallerPanel from "../agents/AgentInstallerPanel";

interface PracticeStep { key: string; label: string; realLife: string; state: "done" | "current" | "pending"; at: string | null }
interface PracticeProgress { found: boolean; replied: boolean; supervisorName: string | null; summary: string; steps: PracticeStep[] }
interface TestEscalationResult { sent: boolean; recipientName: string; recipientEmail: string; error: string | null }

interface Props {
  employee: EmployeeDto;
  employees: EmployeeDto[];
  onClose: () => void;
  onChanged: () => void;
}

/**
 * Guided setup after a user is added. A Supervisor only has to prove the escalation email reaches them. An Agent goes
 * step by step: who they report to, their mailbox (login proven), the Windows Agent on their PC (detected, so a PC
 * that already has it skips ahead), then a practice inquiry that shows the whole sequence up to the escalation.
 */
export default function UserSetupWizard({ employee, employees, onClose, onChanged }: Props) {
  return (
    <section style={panel}>
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", gap: 12 }}>
        <h2 style={{ fontSize: 17, margin: 0 }}>
          Set up {employee.fullName}
          <span style={{ marginLeft: 8, fontSize: 11, fontWeight: 700, color: "var(--color-text-muted)" }}>
            {employee.isManagement ? "SUPERVISOR" : "AGENT"}
          </span>
        </h2>
        <button onClick={onClose}>Close</button>
      </div>
      {employee.isManagement
        ? <SupervisorSteps employee={employee} onChanged={onChanged} />
        : <AgentSteps employee={employee} employees={employees} onChanged={onChanged} />}
    </section>
  );
}

function SupervisorSteps({ employee, onChanged }: { employee: EmployeeDto; onChanged: () => void }) {
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<TestEscalationResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [confirmed, setConfirmed] = useState(false);

  async function send() {
    setBusy(true);
    setError(null);
    setConfirmed(false);
    try {
      const res = await apiClient.post<TestEscalationResult>(`/employees/${employee.id}/test-escalation-receipt`);
      setResult(res.data);
      if (!res.data.sent) setError(`The test email could not be sent: ${res.data.error ?? "unknown error"}. Check Outbound Email.`);
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Could not send the test email.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <div>
      <p style={muted}>
        A supervisor does not handle customer email, so their mailbox is not read. They only receive escalations and notifications.
        Check that a real escalation email reaches them.
      </p>
      <Step n={1} title={`Send a test escalation email to ${employee.email}`} done={!!result?.sent}>
        <button className="btn-primary" onClick={send} disabled={busy}>{busy ? "Sending..." : result?.sent ? "Send again" : "Send test email"}</button>
        {result?.sent && <span style={{ marginLeft: 10, color: "var(--color-success)" }}>Sent to {result.recipientEmail}.</span>}
        {error && <div className="form-error" style={{ marginTop: 8 }}>{error}</div>}
      </Step>
      <Step n={2} title="Ask them to check their inbox" done={confirmed} disabled={!result?.sent}>
        <p style={{ ...muted, margin: "0 0 8px" }}>Look for an email with the subject starting <b>[TEST]</b>. Also check the spam folder.</p>
        {confirmed
          ? <span style={{ color: "var(--color-success)", fontWeight: 600 }}>✓ {employee.fullName} received it. Escalations will reach them.</span>
          : <div style={{ display: "flex", gap: 8 }}>
              <button className="btn-primary" disabled={!result?.sent} onClick={() => { setConfirmed(true); onChanged(); }}>Yes, they received it</button>
              <button disabled={!result?.sent || busy} onClick={send}>Not yet, send again</button>
            </div>}
      </Step>
      <Step n={3} title="Make them the one who gets escalations" done={false} optional>
        <p style={{ ...muted, margin: 0 }}>
          Open an agent's <b>Edit</b> and pick {employee.fullName} as their <b>Supervisor</b>, or add them as a recipient under
          Escalation Policies. A supervisor with nobody reporting to them is only notified when a policy names them.
        </p>
      </Step>
    </div>
  );
}

function AgentSteps({ employee, employees, onChanged }: { employee: EmployeeDto; employees: EmployeeDto[]; onChanged: () => void }) {
  const [mailboxes, setMailboxes] = useState<EmailAccountDto[]>([]);
  const [agents, setAgents] = useState<AgentDto[]>([]);
  const [progress, setProgress] = useState<PracticeProgress | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [password, setPassword] = useState("");
  const [showInstaller, setShowInstaller] = useState(false);
  const [supervisorId, setSupervisorId] = useState(employee.supervisorEmployeeId ?? "");

  const loadAll = useCallback(async () => {
    try {
      const [m, a] = await Promise.all([
        apiClient.get<EmailAccountDto[]>("/email-accounts?purpose=0"),
        apiClient.get<AgentDto[]>("/agents"),
      ]);
      setMailboxes(m.data.filter((x) => x.ownerEmployeeId === employee.id));
      setAgents(a.data.filter((x) => x.employeeId === employee.id && x.registrationStatus === 1));
    } catch {
      setError("Could not load this person's mailbox and PC.");
    }
  }, [employee.id]);

  const loadProgress = useCallback(async () => {
    try {
      const res = await apiClient.get<PracticeProgress>(`/employees/${employee.id}/practice-progress`);
      setProgress(res.data);
    } catch { /* keep the last picture */ }
  }, [employee.id]);

  useEffect(() => { loadAll(); loadProgress(); }, [loadAll, loadProgress]);

  // While a practice run is going, and while the PC has not appeared yet, look again every few seconds.
  const pcInstalled = agents.length > 0;
  const runFinished = !!progress && progress.found && (progress.replied || progress.steps.every((s) => s.state === "done"));
  useEffect(() => {
    const t = setInterval(() => {
      if (progress?.found && !runFinished) loadProgress();
      if (!pcInstalled) loadAll();
    }, 8000);
    return () => clearInterval(t);
  }, [progress?.found, runFinished, pcInstalled, loadProgress, loadAll]);

  const mailbox = mailboxes[0];
  const mailboxOk = !!mailbox && mailbox.lastTestSucceeded === true;
  const online = agents.some((a) => a.connectionStatus === 1);
  const supervisorSet = !!employee.supervisorEmployeeId;
  const supervisorName = employee.supervisorEmployeeName;

  async function saveSupervisor() {
    setBusy("supervisor"); setError(null);
    try {
      await apiClient.put(`/employees/${employee.id}`, {
        fullName: employee.fullName, email: employee.email, isActive: employee.isActive,
        departmentId: employee.departmentId, supervisorEmployeeId: supervisorId || null, isManagement: false,
      });
      setNotice("Supervisor saved.");
      onChanged();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Could not save the supervisor.");
    } finally { setBusy(null); }
  }

  async function testMailbox(id: string) {
    setBusy("mailbox"); setError(null); setNotice(null);
    try {
      const res = await apiClient.post<TestConnectionResult>(`/email-accounts/${id}/test-connection`);
      if (!res.data.succeeded) setError(`The mailbox login failed: ${res.data.errorMessage ?? "unknown error"}.`);
      else setNotice("The mailbox login works.");
      await loadAll();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Could not test the mailbox.");
    } finally { setBusy(null); }
  }

  async function addMailbox() {
    setBusy("mailbox"); setError(null); setNotice(null);
    try {
      const found = await apiClient.post<{ host: string; port: number; encryption: string; username: string }>(
        "/email-accounts/detect-settings", { emailAddress: employee.email, password });
      const created = await apiClient.post<EmailAccountDto>("/email-accounts", {
        emailAddress: employee.email, displayName: employee.fullName, purpose: 0, kind: 0, protocol: 0,
        host: found.data.host, port: found.data.port, encryption: found.data.encryption, username: found.data.username,
        authMethod: 0, secret: password, ownerEmployeeId: employee.id, classificationProfileName: null, monitoringEnabled: true,
      });
      setPassword("");
      await testMailbox(created.data.id);
      onChanged();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "The mailbox could not be set up. Check the email password.");
      setBusy(null);
    }
  }

  async function sendPractice() {
    setBusy("practice"); setError(null); setNotice(null);
    try {
      const res = await apiClient.post<{ sent: boolean; message: string }>(`/employees/${employee.id}/practice-inquiry`);
      if (res.data.sent) setNotice(res.data.message); else setError(res.data.message);
      await loadProgress();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Could not send the practice inquiry.");
    } finally { setBusy(null); }
  }

  const allGood = supervisorSet && mailboxOk && pcInstalled && !!progress?.found;

  return (
    <div>
      <p style={muted}>
        Do the steps in order. Anything already done is ticked automatically, so a person who already has the Agent installed, or a
        mailbox already added, moves straight on.
      </p>
      {error && <div className="form-error">{error}</div>}
      {notice && <div style={{ color: "var(--color-success)", marginBottom: 8 }}>{notice}</div>}

      <Step n={1} title="Who gets their escalations" done={supervisorSet}>
        <div style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "center" }}>
          <select value={supervisorId} onChange={(e) => setSupervisorId(e.target.value)}>
            <option value="">Pick a supervisor</option>
            {employees.filter((o) => o.id !== employee.id && o.isActive).map((o) => (
              <option key={o.id} value={o.id}>{o.fullName}{o.isManagement ? " (supervisor)" : ""}</option>
            ))}
          </select>
          <button onClick={saveSupervisor} disabled={busy === "supervisor" || supervisorId === (employee.supervisorEmployeeId ?? "")}>Save</button>
          {supervisorSet && <span style={{ color: "var(--color-success)" }}>Escalations go to {supervisorName}.</span>}
        </div>
      </Step>

      <Step n={2} title="Their mailbox, so IEMAS can read it" done={mailboxOk}>
        {mailbox ? (
          <div style={{ display: "flex", gap: 10, alignItems: "center", flexWrap: "wrap" }}>
            <span>{mailbox.emailAddress} on {mailbox.host}:{mailbox.port}</span>
            <span style={{ color: mailboxOk ? "var(--color-success)" : mailbox.lastTestSucceeded === false ? "var(--color-danger)" : "var(--color-text-muted)" }}>
              {mailboxOk ? "✓ login works" : mailbox.lastTestSucceeded === false ? `✗ ${mailbox.lastTestError ?? "login failed"}` : "not tested yet"}
            </span>
            <button onClick={() => testMailbox(mailbox.id)} disabled={busy === "mailbox"}>{busy === "mailbox" ? "Testing..." : "Test login"}</button>
          </div>
        ) : (
          <div style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "center" }}>
            <span style={muted}>Their email password for {employee.email}:</span>
            <input type="password" autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} />
            <button className="btn-primary" onClick={addMailbox} disabled={!password || busy === "mailbox"}>
              {busy === "mailbox" ? "Checking..." : "Add mailbox and test login"}
            </button>
            <span style={muted}>IEMAS finds the mail server and checks the password before saving.</span>
          </div>
        )}
      </Step>

      <Step n={3} title="The IEMAS Agent on their PC" done={pcInstalled}>
        {pcInstalled ? (
          <span style={{ color: online ? "var(--color-success)" : "var(--color-text-muted)" }}>
            ✓ Installed{online ? " and online." : ", but the PC is offline right now."}
          </span>
        ) : (
          <>
            <p style={{ ...muted, margin: "0 0 8px" }}>
              Not installed yet. Download the installer, run it on their PC and have them type their email and password. This page notices the PC by itself.
            </p>
            <button onClick={() => setShowInstaller((s) => !s)}>{showInstaller ? "Hide installer" : "Get the installer"}</button>
            <button onClick={loadAll} style={{ marginLeft: 8 }}>Check again</button>
            {showInstaller && <div style={{ marginTop: 10 }}><AgentInstallerPanel employees={employees} onChanged={loadAll} /></div>}
          </>
        )}
      </Step>

      <Step n={4} title="Practice run: see the whole sequence" done={!!progress && progress.found && progress.steps.every((s) => s.state === "done")} disabled={!mailboxOk}>
        <p style={{ ...muted, margin: "0 0 8px" }}>
          Sends a sample customer inquiry to their mailbox. Do not reply and each step below lights up in turn, from the pop-up to the
          escalation (about 10 minutes instead of days). Reply to it from Outlook and the reminders stop.
        </p>
        <button className="btn-primary" onClick={sendPractice} disabled={!mailboxOk || busy === "practice"}>
          {busy === "practice" ? "Sending..." : progress?.found ? "Send another practice inquiry" : "Send practice inquiry"}
        </button>
        {!pcInstalled && mailboxOk && <span style={{ ...muted, marginLeft: 10 }}>Without the PC Agent they will not see the pop-ups, but the steps still run.</span>}
        {progress?.found && (
          <div style={{ marginTop: 12 }}>
            <div style={{ fontSize: 13, marginBottom: 6 }}><b>{progress.summary}</b></div>
            <ol style={{ listStyle: "none", margin: 0, padding: 0 }}>
              {progress.steps.map((s) => (
                <li key={s.key} style={{ display: "flex", gap: 10, padding: "5px 0", alignItems: "baseline", opacity: s.state === "pending" ? 0.55 : 1 }}>
                  <span style={{ width: 18, fontWeight: 700, color: s.state === "done" ? "var(--color-success)" : "var(--color-text-muted)" }}>
                    {s.state === "done" ? "✓" : s.state === "current" ? "▶" : "○"}
                  </span>
                  <span style={{ flex: 1 }}>
                    <span style={{ fontWeight: s.state === "current" ? 700 : 400 }}>{s.label}</span>
                    <span style={{ ...muted, marginLeft: 8, fontSize: 12 }}>In real use: {s.realLife}</span>
                  </span>
                  {s.at && <span style={{ ...muted, fontSize: 12 }}>{new Date(s.at).toLocaleTimeString()}</span>}
                </li>
              ))}
            </ol>
            <p style={{ ...muted, fontSize: 12, margin: "8px 0 0" }}>
              The practice escalation email goes only to {employee.fullName}, never to the supervisor. In real use {supervisorName ?? "their supervisor"} is emailed at that last step.
            </p>
          </div>
        )}
      </Step>

      {allGood && <div style={{ marginTop: 12, color: "var(--color-success)", fontWeight: 600 }}>✓ {employee.fullName} is ready.</div>}
    </div>
  );
}

function Step({ n, title, done, disabled, optional, children }: { n: number; title: string; done: boolean; disabled?: boolean; optional?: boolean; children: React.ReactNode }) {
  return (
    <div style={{ display: "flex", gap: 12, padding: "12px 0", borderTop: "1px solid var(--color-border, #ddd)", opacity: disabled ? 0.5 : 1 }}>
      <div style={{
        width: 26, height: 26, borderRadius: 13, flex: "none", display: "flex", alignItems: "center", justifyContent: "center", fontSize: 13, fontWeight: 700,
        background: done ? "var(--color-success)" : "var(--color-surface)", color: done ? "#fff" : "var(--color-text-muted)", border: "1px solid var(--color-border, #ccc)",
      }}>{done ? "✓" : n}</div>
      <div style={{ flex: 1, minWidth: 0 }}>
        <div style={{ fontWeight: 600, marginBottom: 6 }}>{title}{optional && <span style={{ ...muted, fontWeight: 400, marginLeft: 6 }}>(good to know)</span>}</div>
        {children}
      </div>
    </div>
  );
}

const panel: React.CSSProperties = { border: "1px solid var(--color-border, #ddd)", borderRadius: 8, padding: 16, marginBottom: 24, background: "var(--color-surface)" };
const muted: React.CSSProperties = { color: "var(--color-text-muted)", fontSize: 13 };
