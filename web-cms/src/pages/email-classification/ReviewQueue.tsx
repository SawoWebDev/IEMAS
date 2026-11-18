import { useEffect, useState } from "react";
import { apiClient } from "../../api/client";
import { Badge } from "../../components/StatCard";
import { InfoTip } from "../../components/help/Tooltip";

interface PendingReview {
  emailMessageId: string;
  subject: string;
  fromAddress: string;
  fromDisplayName: string | null;
  mailbox: string;
  receivedAt: string;
  snippet: string;
  aiRelevance: string | null;
  aiCategory: string | null;
  aiPriority: string | null;
  aiConfidence: number | null;
  aiSummary: string | null;
  decisionReason: string | null;
  filterScore: string | null;
  processingError: string | null;
  classifiedAt: string;
  isStale: boolean;
}

interface ReviewResult {
  decision: string;
  caseOutcome: string;
  caseNumber: string | null;
}

interface Draft {
  note: string;
  category: string;
  priority: string;
  relevance: string;
}

const emptyDraft: Draft = { note: "", category: "", priority: "", relevance: "" };

const outcomeText: Record<string, string> = {
  NewCase: "New Case created",
  JoinedExistingCase: "Added to existing Case",
  ReopenedCase: "Existing Case reopened",
  NoCase: "No Case",
};

/** Emails the AI could not decide. A person approves (becomes an inquiry), rejects, or corrects then decides. */
export default function ReviewQueue() {
  const [items, setItems] = useState<PendingReview[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [openId, setOpenId] = useState<string | null>(null);
  const [draft, setDraft] = useState<Draft>(emptyDraft);
  const [busy, setBusy] = useState(false);

  async function load() {
    try {
      const res = await apiClient.get<PendingReview[]>("/classification-reviews");
      setItems(res.data);
      setError(null);
    } catch (err: any) {
      setError(err.response?.status === 403 ? "Your role cannot review classifications." : "Failed to load the review queue.");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    load();
  }, []);

  function open(id: string) {
    setOpenId(openId === id ? null : id);
    setDraft(emptyDraft);
  }

  async function decide(item: PendingReview, decision: "Approve" | "Reject") {
    setBusy(true);
    setMessage(null);
    setError(null);
    try {
      const res = await apiClient.post<ReviewResult>(`/classification-reviews/${item.emailMessageId}/review`, {
        decision,
        note: draft.note || null,
        correctedRelevance: draft.relevance || null,
        correctedCategory: draft.category || null,
        correctedPriority: draft.priority || null,
      });
      const r = res.data;
      setMessage(
        decision === "Approve"
          ? `Approved: ${outcomeText[r.caseOutcome] ?? r.caseOutcome}${r.caseNumber ? ` (${r.caseNumber})` : ""}.`
          : "Rejected: not an inquiry, no Case created.",
      );
      setOpenId(null);
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to save the review.");
      if (err.response?.status === 409) await load();
    } finally {
      setBusy(false);
    }
  }

  if (loading) return <p>Loading review queue...</p>;

  const stale = items.filter((i) => i.isStale).length;

  return (
    <section style={{ marginBottom: 32 }}>
      <h2 style={{ fontSize: 16, display: "flex", alignItems: "center", gap: 8 }}>
        Review queue
        <Badge tone={items.length > 0 ? "warn" : "neutral"}>{items.length}</Badge>
        {stale > 0 && <Badge tone="bad">{stale} waiting over 24h</Badge>}
        <InfoTip text="Emails the AI was not sure about, or could not read. They never become a Case on their own: approve one to make it an inquiry (the agent is notified), or reject it. Each decision is recorded with your name." />
      </h2>

      {error && <div className="form-error">{error}</div>}
      {message && <p style={{ color: "var(--color-text-muted)", fontSize: 13 }}>{message}</p>}
      {items.length === 0 && !error && <p style={{ color: "var(--color-text-muted)", fontSize: 13 }}>Nothing is waiting for review.</p>}

      {items.map((i) => (
        <div key={i.emailMessageId} style={{ border: "1px solid var(--color-border)", borderRadius: 4, padding: 12, marginBottom: 8 }}>
          <div style={{ display: "flex", justifyContent: "space-between", gap: 12, flexWrap: "wrap" }}>
            <div>
              <strong>{i.subject || "(no subject)"}</strong>
              {i.isStale && <span style={{ marginLeft: 8 }}><Badge tone="bad">stale</Badge></span>}
              <div style={{ fontSize: 12, color: "var(--color-text-muted)" }}>
                {i.fromDisplayName ? `${i.fromDisplayName} <${i.fromAddress}>` : i.fromAddress} to {i.mailbox} · {new Date(i.receivedAt).toLocaleString()}
              </div>
            </div>
            <button type="button" onClick={() => open(i.emailMessageId)} aria-expanded={openId === i.emailMessageId}>
              {openId === i.emailMessageId ? "Close" : "Review"}
            </button>
          </div>
          <p style={{ margin: "8px 0", fontSize: 13 }}>{i.snippet || "(no text)"}</p>
          <div style={{ display: "flex", flexWrap: "wrap", gap: 6, fontSize: 12 }}>
            {i.aiRelevance && <Badge tone="info">AI: {i.aiRelevance}</Badge>}
            {i.aiCategory && <Badge tone="info">{i.aiCategory}</Badge>}
            {i.aiPriority && <Badge tone="neutral">{i.aiPriority} priority</Badge>}
            {i.aiConfidence != null && <Badge tone="neutral">{(i.aiConfidence * 100).toFixed(0)}% sure</Badge>}
            {i.processingError && <Badge tone="bad">AI failed</Badge>}
          </div>
          {i.decisionReason && <div style={{ fontSize: 12, color: "var(--color-text-muted)", marginTop: 6 }}>Why it is here: {i.decisionReason}</div>}
          {i.filterScore && <div style={{ fontSize: 12, color: "var(--color-text-muted)" }}>Filter: {i.filterScore}</div>}

          {openId === i.emailMessageId && (
            <div style={{ marginTop: 10, display: "grid", gap: 8, maxWidth: 520 }}>
              <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
                <label>
                  Category<br />
                  <input value={draft.category} placeholder={i.aiCategory ?? ""} onChange={(e) => setDraft({ ...draft, category: e.target.value })} />
                </label>
                <label>
                  Priority<br />
                  <select value={draft.priority} onChange={(e) => setDraft({ ...draft, priority: e.target.value })}>
                    <option value="">(keep AI: {i.aiPriority ?? "none"})</option>
                    <option value="Low">Low</option>
                    <option value="Medium">Medium</option>
                    <option value="High">High</option>
                  </select>
                </label>
                <label>
                  Relevance<br />
                  <select value={draft.relevance} onChange={(e) => setDraft({ ...draft, relevance: e.target.value })}>
                    <option value="">(keep AI: {i.aiRelevance ?? "none"})</option>
                    <option value="Relevant">Relevant</option>
                    <option value="NotRelevant">Not relevant</option>
                  </select>
                </label>
              </div>
              <label>
                Note (optional)<br />
                <textarea rows={2} style={{ width: "100%" }} value={draft.note} onChange={(e) => setDraft({ ...draft, note: e.target.value })} />
              </label>
              <div style={{ display: "flex", gap: 8 }}>
                <button type="button" disabled={busy} onClick={() => decide(i, "Approve")} data-tip="This is a customer inquiry: create or join a Case and notify the agent.">Approve as inquiry</button>
                <button type="button" className="btn-danger" disabled={busy} onClick={() => decide(i, "Reject")} data-tip="Not an inquiry: stored as not important, no Case.">Reject (not an inquiry)</button>
              </div>
            </div>
          )}
        </div>
      ))}
    </section>
  );
}
