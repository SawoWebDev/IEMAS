import { useEffect, useState } from "react";
import { apiClient } from "../../api/client";
import type { EmailAccountDto } from "../../api/types";
import { InfoTip } from "../../components/help/Tooltip";

interface Item {
  kind: string;
  id: string;
  name: string;
  detail: string;
}

interface Plan {
  remove: Item[];
  keep: Item[];
}

interface Props {
  /** null = the option is off; otherwise the ids of the mailboxes to keep. */
  value: string[] | null;
  onChange: (value: string[] | null) => void;
}

/** "Clean start": keep only the ticked mailboxes, and show exactly what would be removed before anything happens. */
export default function KeepOnlySelector({ value, onChange }: Props) {
  const [accounts, setAccounts] = useState<EmailAccountDto[]>([]);
  const [plan, setPlan] = useState<Plan | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    apiClient.get<EmailAccountDto[]>("/email-accounts")
      .then((res) => setAccounts(res.data))
      .catch(() => setError("Failed to load the mailboxes."));
  }, []);

  const chosen = value?.join(",") ?? "";
  useEffect(() => {
    setPlan(null);
    if (!chosen) return;
    let current = true;
    apiClient.post<Plan>("/maintenance/reset-email-data/keep-only-preview", { keepEmailAccountIds: chosen.split(",") })
      .then((res) => { if (current) setPlan(res.data); })
      .catch(() => { if (current) setError("Failed to work out what would be removed."); });
    return () => { current = false; };
  }, [chosen]);

  function toggle(id: string) {
    if (!value) return;
    onChange(value.includes(id) ? value.filter((x) => x !== id) : [...value, id]);
  }

  return (
    <div style={{ marginTop: 16, borderTop: "1px solid var(--color-border)", paddingTop: 14 }}>
      <label style={{ display: "flex", gap: 8, alignItems: "center", fontWeight: 600 }}>
        <input type="checkbox" checked={value !== null} onChange={(e) => onChange(e.target.checked ? [] : null)} />
        Clean start: also remove every mailbox except the ones I tick
        <InfoTip text="Use this to clear out test setup. Employees, departments and PCs that only the removed mailboxes needed go too. Users, policies, settings and AI setup are kept." />
      </label>
      {error && <div className="form-error">{error}</div>}

      {value !== null && (
        <>
          <div style={{ margin: "10px 0", display: "grid", gap: 4 }}>
            {accounts.map((a) => (
              <label key={a.id} style={{ display: "flex", gap: 8, alignItems: "center" }}>
                <input type="checkbox" checked={value.includes(a.id)} onChange={() => toggle(a.id)} />
                <strong>{a.emailAddress}</strong>
                <span style={{ color: "var(--color-text-muted)", fontSize: 12 }}>
                  {a.purpose === 1 ? "Outbound (sends IEMAS email)" : "Monitored mailbox"} · {a.host}:{a.port}
                </span>
              </label>
            ))}
          </div>
          {value.length === 0 && <div style={{ fontSize: 13, color: "var(--color-warning)" }}>Tick at least one mailbox to keep, including the outbound one.</div>}
          {plan && !accounts.some((a) => a.purpose === 1 && value.includes(a.id)) && accounts.some((a) => a.purpose === 1) && (
            <div style={{ fontSize: 13, color: "var(--color-warning)" }}>No outbound account is ticked: IEMAS could no longer send escalation emails.</div>
          )}
          {plan && (
            <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(300px, 1fr))", gap: 16, marginTop: 10 }}>
              <List title="Will be removed" items={plan.remove} color="var(--color-danger)" />
              <List title="Will be kept" items={plan.keep} color="var(--color-success)" />
            </div>
          )}
        </>
      )}
    </div>
  );
}

function List({ title, items, color }: { title: string; items: Item[]; color: string }) {
  return (
    <div>
      <div className="section-title" style={{ marginTop: 0, color }}>{title} ({items.length})</div>
      <ul style={{ margin: 0, paddingLeft: 18, fontSize: 13, lineHeight: 1.7 }}>
        {items.map((i) => (
          <li key={i.id}>{i.kind}: <strong>{i.name}</strong>{i.detail && <span style={{ color: "var(--color-text-muted)" }}> ({i.detail})</span>}</li>
        ))}
        {items.length === 0 && <li>Nothing</li>}
      </ul>
    </div>
  );
}
