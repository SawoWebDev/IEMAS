import { useEffect, useState } from "react";
import { apiClient } from "../../api/client";
import type { AgentDto, DepartmentDto, EmailAccountDto, EmployeeDto } from "../../api/types";
import { InfoTip, TipLabel } from "../../components/help/Tooltip";

export default function EmployeesPage() {
  const [employees, setEmployees] = useState<EmployeeDto[]>([]);
  const [departments, setDepartments] = useState<DepartmentDto[]>([]);
  const [mailboxes, setMailboxes] = useState<EmailAccountDto[]>([]);
  const [agents, setAgents] = useState<AgentDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const [fullName, setFullName] = useState("");
  const [email, setEmail] = useState("");
  const [mailboxPassword, setMailboxPassword] = useState("");
  const [adding, setAdding] = useState(false);
  const [departmentId, setDepartmentId] = useState("");
  const [supervisorEmployeeId, setSupervisorEmployeeId] = useState("");

  const [editingId, setEditingId] = useState<string | null>(null);
  const [editForm, setEditForm] = useState<EditForm>(emptyEditForm);
  const [saving, setSaving] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);

  async function load() {
    setLoading(true);
    try {
      const [empRes, deptRes, mailRes, agentRes] = await Promise.all([
        apiClient.get<EmployeeDto[]>("/employees"),
        apiClient.get<DepartmentDto[]>("/departments"),
        apiClient.get<EmailAccountDto[]>("/email-accounts?purpose=0"),
        apiClient.get<AgentDto[]>("/agents"),
      ]);
      setEmployees(empRes.data);
      setDepartments(deptRes.data);
      setMailboxes(mailRes.data);
      setAgents(agentRes.data);
    } catch {
      setError("Failed to load employees or departments.");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    load();
  }, []);

  async function handleCreate(e: React.FormEvent) {
    e.preventDefault();
    setError(null);
    setNotice(null);
    setAdding(true);
    try {
      const created = await apiClient.post<EmployeeDto>("/employees", {
        fullName,
        email,
        departmentId: departmentId || null,
        supervisorEmployeeId: supervisorEmployeeId || null,
      });
      let result = `Added ${fullName}.`;

      // With a password the mailbox is set up too: IEMAS finds the mail server by logging in, then saves the mailbox.
      if (mailboxPassword) {
        try {
          const found = await apiClient.post<{ host: string; port: number; encryption: string; username: string }>(
            "/email-accounts/detect-settings", { emailAddress: email, password: mailboxPassword });
          await apiClient.post("/email-accounts", {
            emailAddress: email, displayName: fullName, purpose: 0, kind: 0, protocol: 0,
            host: found.data.host, port: found.data.port, encryption: found.data.encryption, username: found.data.username,
            authMethod: 0, secret: mailboxPassword, ownerEmployeeId: created.data.id, classificationProfileName: null, monitoringEnabled: true,
          });
          result += " Their mailbox is connected and being watched.";
        } catch (err: any) {
          setError(`${fullName} was added, but the mailbox was not: ${err.response?.data?.message ?? "it could not be set up."} Add it on the Mailboxes tab.`);
        }
      } else {
        result += " Add a mailbox on the Mailboxes tab, or let them set up their own PC with the company installer.";
      }
      setNotice(result);
      setFullName("");
      setEmail("");
      setMailboxPassword("");
      setDepartmentId("");
      setSupervisorEmployeeId("");
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to create employee.");
    } finally {
      setAdding(false);
    }
  }

  function startEdit(emp: EmployeeDto) {
    setError(null);
    setNotice(null);
    setEditingId(emp.id);
    setEditForm({
      fullName: emp.fullName,
      email: emp.email,
      isActive: emp.isActive,
      departmentId: emp.departmentId ?? "",
      supervisorEmployeeId: emp.supervisorEmployeeId ?? "",
    });
  }

  async function saveEdit(emp: EmployeeDto) {
    setError(null);
    setNotice(null);
    setSaving(true);
    try {
      await apiClient.put(`/employees/${emp.id}`, {
        fullName: editForm.fullName,
        email: editForm.email,
        isActive: editForm.isActive,
        departmentId: editForm.departmentId || null,
        supervisorEmployeeId: editForm.supervisorEmployeeId || null,
      });
      setEditingId(null);
      setNotice(`Saved ${editForm.fullName}.`);
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to update employee.");
    } finally {
      setSaving(false);
    }
  }

  async function handleToggleActive(emp: EmployeeDto) {
    setError(null);
    try {
      await apiClient.post(`/employees/${emp.id}/${emp.isActive ? "deactivate" : "activate"}`);
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to update employee status.");
    }
  }

  async function handleDelete(emp: EmployeeDto) {
    if (!window.confirm(`Permanently delete ${emp.fullName} (${emp.email})? This cannot be undone.`)) return;
    setError(null);
    try {
      await apiClient.delete(`/employees/${emp.id}`);
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to delete employee.");
    }
  }

  async function handleTestEscalation(emp: EmployeeDto) {
    if (!window.confirm(`Send a sample escalation email, marked [TEST], to ${emp.supervisorEmployeeName} (${emp.fullName}'s supervisor)? No Case is changed.`)) return;
    setError(null);
    setNotice(null);
    try {
      const res = await apiClient.post<{ sent: boolean; recipientName: string; recipientEmail: string; error: string | null }>(
        "/escalations/test-email", { agentEmployeeId: emp.id });
      if (res.data.sent) {
        setNotice(`Test escalation email sent to ${res.data.recipientName} <${res.data.recipientEmail}>.`);
      } else {
        setError(`The test email could not be sent: ${res.data.error ?? "unknown error"}. Check Outbound Email.`);
      }
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to send the test escalation email.");
    }
  }

  async function handlePracticeInquiry(emp: EmployeeDto) {
    if (!window.confirm(
      `Send a PRACTICE inquiry to ${emp.fullName}'s mailbox?

` +
      "It is a sample customer email from the outbound account. On their PC they will see the new-email pop-up, then " +
      "(if they do not reply) the reminder, the escalation warning and the escalation, all within about 10 minutes. " +
      "The escalation email goes only to them, never to their supervisor.")) return;
    setError(null);
    setNotice(null);
    try {
      const res = await apiClient.post<{ sent: boolean; message: string; error: string | null }>(`/employees/${emp.id}/practice-inquiry`);
      if (res.data.sent) setNotice(res.data.message);
      else setError(res.data.message);
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to send the practice inquiry.");
    }
  }

  function mailboxCell(emp: EmployeeDto) {
    const own = mailboxes.filter((m) => m.ownerEmployeeId === emp.id);
    if (own.length === 0) return <span style={{ color: "var(--color-text-muted)" }}>None</span>;
    return own.map((m) => (
      <div key={m.id} title={m.lastTestError ?? undefined}>
        {m.emailAddress}{" "}
        <span style={{ color: !m.isActive || m.lastTestSucceeded === false ? "var(--color-danger)" : m.lastTestSucceeded ? "var(--color-success)" : "var(--color-text-muted)", fontSize: 12 }}>
          {!m.isActive ? "off" : m.lastTestSucceeded === false ? "login failed" : m.lastTestSucceeded ? "ok" : "not tested"}
        </span>
      </div>
    ));
  }

  function pcCell(emp: EmployeeDto) {
    const own = agents.filter((a) => a.employeeId === emp.id && a.registrationStatus === 1);
    if (own.length === 0) return <span style={{ color: "var(--color-text-muted)" }}>Not installed</span>;
    const online = own.filter((a) => a.connectionStatus === 1).length;
    return <span style={{ color: online > 0 ? "var(--color-success)" : "var(--color-text-muted)" }}>{online > 0 ? `${online} online` : "Offline"}{own.length > 1 ? ` (of ${own.length})` : ""}</span>;
  }

  if (loading) return <p>Loading...</p>;

  return (
    <div>
      <h1>Employees</h1>
      <p style={{ color: "var(--color-text-muted)", fontSize: 13 }}>
        The people who own mailboxes and Cases, with their mailbox and PC at a glance. Most people never need adding by hand: when an
        employee runs the company installer and types their email and password, they are added here automatically. A Super
        Administrator sign-in such as admin@sawo.com is for access only and can't be used as an employee's email.
      </p>

      {error && <div className="form-error">{error}</div>}
      {notice && <div style={{ color: "var(--color-success)", marginBottom: 12 }}>{notice}</div>}

      <form onSubmit={handleCreate} style={{ marginBottom: 24, display: "flex", gap: 8, flexWrap: "wrap", alignItems: "end" }}>
        <div>
          <label><TipLabel tip="The person's name as it should appear in pop-ups and reports.">Full name</TipLabel></label><br />
          <input value={fullName} onChange={(e) => setFullName(e.target.value)} required />
        </div>
        <div>
          <label><TipLabel tip="Their work email address.">Email</TipLabel></label><br />
          <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} required />
        </div>
        <div>
          <label><TipLabel tip="Their department. The department's manager can be an escalation recipient.">Department</TipLabel></label><br />
          <select value={departmentId} onChange={(e) => setDepartmentId(e.target.value)}>
            <option value="">—</option>
            {departments.map((d) => (
              <option key={d.id} value={d.id}>{d.name}</option>
            ))}
          </select>
        </div>
        <div>
          <label><TipLabel tip="Their direct supervisor. Usually the first person told when one of their Cases is escalated.">Supervisor</TipLabel></label><br />
          <select value={supervisorEmployeeId} onChange={(e) => setSupervisorEmployeeId(e.target.value)}>
            <option value="">—</option>
            {employees.map((e) => (
              <option key={e.id} value={e.id}>{e.fullName}</option>
            ))}
          </select>
        </div>
        <div>
          <label><TipLabel tip="Optional. Their email password: IEMAS finds the mail server, checks the login and starts watching the mailbox. It is stored encrypted and never shown again. Leave blank to add the person only.">Email password</TipLabel></label><br />
          <input type="password" autoComplete="new-password" value={mailboxPassword} onChange={(e) => setMailboxPassword(e.target.value)} />
        </div>
        <button type="submit" disabled={adding} data-tip="Add this person. With a password their mailbox is connected too. Their PC sets itself up from the company installer on the PCs tab.">{adding ? "Adding..." : "Add Employee"}</button>
      </form>

      <table style={{ width: "100%", borderCollapse: "separate", borderSpacing: 0 }}>
        <thead>
          <tr>
            <th style={thStyle}>Name</th>
            <th style={thStyle}>Email</th>
            <th style={thStyle}>Department</th>
            <th style={thStyle}>Supervisor</th>
            <th style={thStyle}>Mailbox<InfoTip text="Whether IEMAS watches this person's mailbox, and whether the last connection test worked. Manage mailboxes on the Mailboxes tab." /></th>
            <th style={thStyle}>PC<InfoTip text="Their Windows Agent: connected now, offline, or not installed yet. Manage PCs on the PCs tab." /></th>
            <th style={thStyle}>Resolved Manager<InfoTip text="The manager IEMAS will use for escalations, taken from the employee's department." /></th>
            <th style={thStyle}>Active<InfoTip text="Inactive employees get no new Cases or pop-ups, but their history is kept." /></th>
            <th style={thStyle}></th>
          </tr>
        </thead>
        <tbody>
          {employees.map((emp) => editingId === emp.id ? (
            <tr key={emp.id} style={{ background: "var(--color-surface)" }}>
              <td style={tdStyle} colSpan={9}>
                <div style={{ display: "flex", gap: 12, flexWrap: "wrap", alignItems: "end" }}>
                  <div>
                    <label><TipLabel tip="The person's name as it should appear in pop-ups and reports.">Full name</TipLabel></label><br />
                    <input value={editForm.fullName} onChange={(e) => setEditForm({ ...editForm, fullName: e.target.value })} required />
                  </div>
                  <div>
                    <label><TipLabel tip="Their work email address.">Email</TipLabel></label><br />
                    <input type="email" value={editForm.email} onChange={(e) => setEditForm({ ...editForm, email: e.target.value })} required />
                  </div>
                  <div>
                    <label><TipLabel tip="Their department. The department's manager can be an escalation recipient.">Department</TipLabel></label><br />
                    <select value={editForm.departmentId} onChange={(e) => setEditForm({ ...editForm, departmentId: e.target.value })}>
                      <option value="">—</option>
                      {departments.map((d) => (
                        <option key={d.id} value={d.id}>{d.name}</option>
                      ))}
                    </select>
                  </div>
                  <div>
                    <label><TipLabel tip="Their direct supervisor. Usually the first person told when one of their Cases is escalated.">Supervisor</TipLabel></label><br />
                    <select value={editForm.supervisorEmployeeId} onChange={(e) => setEditForm({ ...editForm, supervisorEmployeeId: e.target.value })}>
                      <option value="">—</option>
                      {employees
                        .filter((o) => o.id !== emp.id && (o.isActive || o.id === editForm.supervisorEmployeeId))
                        .map((o) => (
                          <option key={o.id} value={o.id}>{o.fullName}{o.isActive ? "" : " (inactive)"}</option>
                        ))}
                    </select>
                  </div>
                  <div>
                    <label>
                      <input type="checkbox" checked={editForm.isActive} onChange={(e) => setEditForm({ ...editForm, isActive: e.target.checked })} />{" "}
                      Active
                      <InfoTip text="Untick when someone leaves. Their history stays; they get no new work." />
                    </label>
                  </div>
                </div>
                <p style={{ color: "var(--color-text-muted)", fontSize: 12, margin: "8px 0 0" }}>
                  The resolved manager comes from the department's manager. Changing the supervisor affects who gets escalations for this employee's Cases.
                </p>
                <div style={{ display: "flex", gap: 8, marginTop: 12 }}>
                  <button className="btn-primary" disabled={saving} onClick={() => saveEdit(emp)}>{saving ? "Saving..." : "Save"}</button>
                  <button disabled={saving} onClick={() => setEditingId(null)}>Cancel</button>
                </div>
              </td>
            </tr>
          ) : (
            <tr key={emp.id}>
              <td style={tdStyle}>{emp.fullName}</td>
              <td style={tdStyle}>{emp.email}</td>
              <td style={tdStyle}>{emp.departmentName ?? "—"}</td>
              <td style={tdStyle}>{emp.supervisorEmployeeName ?? "—"}</td>
              <td style={tdStyle}>{mailboxCell(emp)}</td>
              <td style={tdStyle}>{pcCell(emp)}</td>
              <td style={tdStyle}>{emp.managerEmployeeName ?? "—"}</td>
              <td style={tdStyle}>{emp.isActive ? "Yes" : "No"}</td>
              <td style={tdStyle}>
                <div style={{ display: "flex", gap: 8 }}>
                  <button onClick={() => startEdit(emp)} disabled={editingId !== null} data-tip="Change name, email, department or supervisor.">Edit</button>
                  <button onClick={() => handleToggleActive(emp)} style={{ minWidth: 80 }} data-tip="Deactivate someone who left: they get no new work, history is kept. Activate brings them back.">
                    {emp.isActive ? "Deactivate" : "Activate"}
                  </button>
                  <button
                    onClick={() => handleTestEscalation(emp)}
                    disabled={!emp.supervisorEmployeeName}
                    data-tip={emp.supervisorEmployeeName
                      ? `Send a sample escalation email, marked [TEST], to ${emp.supervisorEmployeeName}. Checks that escalations for this employee reach their supervisor.`
                      : "Set a supervisor first: level-1 escalations go to the supervisor."}
                  >
                    Test escalation
                  </button>
                  <button
                    onClick={() => handlePracticeInquiry(emp)}
                    disabled={!emp.isActive}
                    data-tip="Send a sample customer inquiry to this person's mailbox so they can see the pop-up, reminder, warning and escalation on their PC within about 10 minutes. Management is never emailed."
                  >
                    Practice inquiry
                  </button>
                  <button
                    className="btn-danger"
                    disabled={emp.isActive}
                    title={emp.isActive ? "Deactivate this employee before deleting" : "Permanently delete this employee"}
                    onClick={() => handleDelete(emp)}
                  >
                    Delete
                  </button>
                </div>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

interface EditForm {
  fullName: string;
  email: string;
  isActive: boolean;
  departmentId: string;
  supervisorEmployeeId: string;
}

const emptyEditForm: EditForm = { fullName: "", email: "", isActive: true, departmentId: "", supervisorEmployeeId: "" };

const thStyle: React.CSSProperties = { textAlign: "left" };
const tdStyle: React.CSSProperties = {};
