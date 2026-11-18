import { useEffect, useMemo, useState } from "react";
import { apiClient } from "../../api/client";
import type { EmailAccountDto, EmployeeDto } from "../../api/types";
import { InfoTip } from "../../components/help/Tooltip";

interface InstallKeyDto {
  id: string;
  emailAddress: string | null;
  ownerName: string | null;
  label: string | null;
  createdAt: string;
  createdBy: string | null;
  expiresAt: string;
  maxUses: number;
  useCount: number;
  lastUsedAt: string | null;
  revokedAt: string | null;
  usable: boolean;
}

interface Availability {
  available: boolean;
  message: string | null;
}

const EVERYONE = "";

/**
 * One-click installer for employees' PCs: a zip with the Agent, Install-IEMAS-Agent.bat and a setup file that
 * holds the server address and a new installer key. PCs installed with it connect without waiting for approval.
 */
export default function AgentInstallerPanel({ employees, onChanged }: { employees: EmployeeDto[]; onChanged: () => void }) {
  const [availability, setAvailability] = useState<Availability | null>(null);
  const [mailboxes, setMailboxes] = useState<EmailAccountDto[]>([]);
  const [keys, setKeys] = useState<InstallKeyDto[]>([]);
  const [open, setOpen] = useState(false);

  const [forEmail, setForEmail] = useState(EVERYONE);
  const [serverUrl, setServerUrl] = useState(window.location.origin);
  const [allowSelfSigned, setAllowSelfSigned] = useState(window.location.protocol === "https:");
  const [validDays, setValidDays] = useState(7);
  const [maxUses, setMaxUses] = useState(25);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function load() {
    try {
      const [a, m, k] = await Promise.all([
        apiClient.get<Availability>("/agent-installers/availability"),
        apiClient.get<EmailAccountDto[]>("/email-accounts?purpose=0"),
        apiClient.get<InstallKeyDto[]>("/agent-installers/keys"),
      ]);
      setAvailability(a.data);
      setMailboxes(m.data.filter((x) => x.ownerEmployeeId));
      setKeys(k.data);
    } catch {
      setError("Could not load the installer settings.");
    }
  }

  useEffect(() => {
    load();
  }, []);

  const ownerName = (id: string | null) => employees.find((e) => e.id === id)?.fullName ?? "";
  const mailboxOptions = useMemo(
    () => [...mailboxes].sort((a, b) => ownerName(a.ownerEmployeeId).localeCompare(ownerName(b.ownerEmployeeId))),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [mailboxes, employees],
  );

  function choose(email: string) {
    setForEmail(email);
    setMaxUses(email === EVERYONE ? 25 : 2);
  }

  async function download() {
    setBusy(true);
    setError(null);
    setMessage(null);
    try {
      const res = await apiClient.post(
        "/agent-installers/package",
        { emailAddress: forEmail || null, serverUrl, allowSelfSignedCertificate: allowSelfSigned, validDays, maxUses },
        { responseType: "blob" },
      );
      const who = forEmail ? forEmail.split("@")[0] : "everyone";
      const url = URL.createObjectURL(res.data as Blob);
      const a = document.createElement("a");
      a.href = url;
      a.download = `IEMAS-Agent-Installer-${who}.zip`;
      document.body.appendChild(a);
      a.click();
      a.remove();
      setTimeout(() => URL.revokeObjectURL(url), 10000);
      setMessage(
        forEmail
          ? `Installer for ${forEmail} downloaded. Send it to that person (or run it on their PC): it installs everything and connects by itself.`
          : "Installer downloaded. Share it with the employees: they run it and type their own email address once.",
      );
      await load();
      onChanged();
    } catch (err: any) {
      let text = "The installer could not be created.";
      const data = err.response?.data;
      if (data instanceof Blob) {
        try { text = JSON.parse(await data.text()).message ?? text; } catch { /* keep the default text */ }
      }
      setError(text);
    } finally {
      setBusy(false);
    }
  }

  async function withdraw(key: InstallKeyDto) {
    setError(null);
    try {
      await apiClient.post(`/agent-installers/keys/${key.id}/revoke`);
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Could not withdraw the installer.");
    }
  }

  const fmt = (iso: string) => new Date(iso).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });
  const localServer = /^https?:\/\/(localhost|127\.0\.0\.1)/i.test(serverUrl);

  return (
    <section style={panelStyle}>
      <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between", gap: 12, flexWrap: "wrap" }}>
        <div style={{ minWidth: 0 }}>
          <h2 style={{ fontSize: 16, margin: 0 }}>Install the Agent on a PC</h2>
          <p style={{ color: "var(--color-text-muted)", fontSize: 13, margin: "4px 0 0", maxWidth: 760 }}>
            Download a ready-made installer. On the PC, extract the zip and double-click <b>Install-IEMAS-Agent.bat</b>: it installs
            the .NET runtime if needed, installs and starts the Agent, and connects it. PCs installed this way are approved
            automatically; nothing to approve below.
          </p>
        </div>
        <button className="btn-primary" onClick={() => setOpen((o) => !o)} disabled={availability?.available === false}
                data-tip="Choose who the installer is for, then download the zip.">
          {open ? "Close" : "Download installer"}
        </button>
      </div>

      {availability?.available === false && <div className="form-error" style={{ marginTop: 10 }}>{availability.message}</div>}
      {error && <div className="form-error" style={{ marginTop: 10 }}>{error}</div>}
      {message && <div style={noticeStyle}>{message}</div>}

      {open && availability?.available && (
        <div style={formStyle}>
          <label style={labelStyle}>
            <span>Who is it for?<InfoTip text="For one person: the PC connects with no typing at all. For everyone: one zip for many PCs; each person types their own email address once." /></span>
            <select value={forEmail} onChange={(e) => choose(e.target.value)} id="installer-for">
              <option value={EVERYONE}>Everyone (each person types their email once)</option>
              {mailboxOptions.map((m) => (
                <option key={m.id} value={m.emailAddress}>{ownerName(m.ownerEmployeeId)} — {m.emailAddress}</option>
              ))}
            </select>
          </label>
          <label style={labelStyle}>
            <span>Server address the PCs use<InfoTip text="Normally the address this CMS is open on, for example https://172.16.2.68:8443. It must be reachable from the employees' PCs." /></span>
            <input id="installer-server" value={serverUrl} onChange={(e) => setServerUrl(e.target.value)} />
          </label>
          {localServer && (
            <div className="form-error" style={{ margin: 0 }}>
              "localhost" only works on this PC. Use the server's network address, for example https://172.16.2.68:8443.
            </div>
          )}
          <label style={{ ...labelStyle, flexDirection: "row", alignItems: "center", gap: 8 }}>
            <input type="checkbox" id="installer-selfsigned" checked={allowSelfSigned} onChange={(e) => setAllowSelfSigned(e.target.checked)} />
            <span>Server uses its own certificate (an IP address, no public domain)</span>
          </label>
          <div style={{ display: "flex", gap: 16, flexWrap: "wrap" }}>
            <label style={labelStyle}>
              <span>Valid for (days)<InfoTip text="After this the installer no longer connects PCs automatically; they then wait for approval below." /></span>
              <input id="installer-days" type="number" min={1} max={90} value={validDays} onChange={(e) => setValidDays(Number(e.target.value))} style={{ width: 110 }} />
            </label>
            <label style={labelStyle}>
              <span>Number of PCs<InfoTip text="How many PCs the installer may connect. Reinstalling a PC counts again." /></span>
              <input id="installer-uses" type="number" min={1} max={500} value={maxUses} onChange={(e) => setMaxUses(Number(e.target.value))} style={{ width: 110 }} />
            </label>
          </div>
          <div style={{ display: "flex", alignItems: "center", gap: 12, flexWrap: "wrap" }}>
            <button className="btn-primary" onClick={download} disabled={busy || !serverUrl.trim()}>
              {busy ? "Preparing..." : "Download zip"}
            </button>
            <span style={{ color: "var(--color-text-muted)", fontSize: 12 }}>
              About 25 MB. Treat it like a password: anyone with it can connect a PC until it expires.
            </span>
          </div>
        </div>
      )}

      {keys.length > 0 && (
        <details style={{ marginTop: 12 }}>
          <summary style={{ cursor: "pointer", fontSize: 13 }}>Installers downloaded ({keys.filter((k) => k.usable).length} still usable)</summary>
          <table style={{ width: "100%", marginTop: 8, fontSize: 13 }}>
            <thead>
              <tr>
                <th style={{ textAlign: "left" }}>For</th>
                <th style={{ textAlign: "left" }}>Created</th>
                <th style={{ textAlign: "left" }}>Expires</th>
                <th style={{ textAlign: "left" }}>PCs connected</th>
                <th style={{ textAlign: "left" }}>Status</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {keys.map((k) => (
                <tr key={k.id}>
                  <td>{k.emailAddress ? `${k.ownerName ?? ""} (${k.emailAddress})` : "Everyone"}</td>
                  <td>{fmt(k.createdAt)}{k.createdBy ? ` · ${k.createdBy}` : ""}</td>
                  <td>{fmt(k.expiresAt)}</td>
                  <td>{k.useCount} of {k.maxUses}</td>
                  <td>{k.revokedAt ? "Withdrawn" : k.usable ? "Usable" : new Date(k.expiresAt) < new Date() ? "Expired" : "Used up"}</td>
                  <td>
                    {k.usable && (
                      <button className="btn-danger" onClick={() => withdraw(k)} data-tip="Stops this installer from connecting more PCs. PCs already connected keep working.">
                        Withdraw
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </details>
      )}
    </section>
  );
}

const panelStyle: React.CSSProperties = { border: "1px solid var(--color-border)", borderRadius: 8, padding: "14px 16px", background: "var(--color-surface)", margin: "8px 0 18px" };
const formStyle: React.CSSProperties = { display: "flex", flexDirection: "column", gap: 12, marginTop: 14, maxWidth: 620 };
const labelStyle: React.CSSProperties = { display: "flex", flexDirection: "column", gap: 4, fontSize: 13 };
const noticeStyle: React.CSSProperties = { marginTop: 10, padding: "8px 12px", borderRadius: 6, background: "var(--color-accent-soft)", fontSize: 13 };
