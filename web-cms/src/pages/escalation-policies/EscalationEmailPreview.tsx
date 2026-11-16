import { useEffect, useState } from "react";
import { apiClient } from "../../api/client";
import { InfoTip } from "../../components/help/Tooltip";

interface PreviewDto {
  subject: string;
  html: string;
  text: string;
  recipient: string;
}

/**
 * Shows the escalation email exactly as it is built and sent (the server renders it with the same
 * code as a real escalation, using sample data), so management can see what recipients receive.
 * The HTML is shown in a sandboxed frame without scripts.
 */
export default function EscalationEmailPreview() {
  const [level, setLevel] = useState(1);
  const [view, setView] = useState<"html" | "text">("html");
  const [width, setWidth] = useState<"desktop" | "phone">("desktop");
  const [preview, setPreview] = useState<PreviewDto | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [frameHeight, setFrameHeight] = useState(900);

  useEffect(() => {
    let cancelled = false;
    apiClient
      .get<PreviewDto>("/escalations/email-preview", { params: { level } })
      .then((res) => {
        if (!cancelled) {
          setPreview(res.data);
          setError(null);
        }
      })
      .catch(() => {
        if (!cancelled) setError("Could not load the email preview.");
      });
    return () => {
      cancelled = true;
    };
  }, [level]);

  const tabStyle = (active: boolean): React.CSSProperties => ({
    fontWeight: active ? 700 : 400,
    borderBottom: active ? "2px solid var(--color-primary, #7a4a2c)" : "2px solid transparent",
    borderRadius: 0,
    background: "transparent",
  });

  return (
    <section style={{ marginTop: 40, maxWidth: 900 }}>
      <h2 style={{ fontSize: 18, marginBottom: 4 }}>
        Escalation email preview
        <InfoTip text="This is the email management receives when an inquiry is escalated. It is built by the same code as a real escalation, filled with sample data. Nothing is sent from here." />
      </h2>
      <p style={{ color: "var(--color-text-muted)", fontSize: 13, marginTop: 0 }}>
        What the supervisor, manager or director receives, with sample data. The email is sent in this design and also as
        plain text for mail apps that cannot show it. To send a real sample, use <b>Test escalation</b> on the Employees page.
      </p>

      <div style={{ display: "flex", gap: 16, flexWrap: "wrap", alignItems: "center", marginBottom: 12 }}>
        <label style={{ fontSize: 13 }}>
          Level{" "}
          <select value={level} onChange={(e) => setLevel(Number(e.target.value))}>
            <option value={1}>1 - supervisor</option>
            <option value={2}>2 - department manager</option>
            <option value={3}>3 - director</option>
          </select>
        </label>
        <div style={{ display: "flex", gap: 4 }}>
          <button type="button" style={tabStyle(view === "html")} onClick={() => setView("html")} data-tip="The designed email as most mail apps show it.">Designed email</button>
          <button type="button" style={tabStyle(view === "text")} onClick={() => setView("text")} data-tip="The plain-text version, for mail apps that cannot show the design.">Plain text</button>
        </div>
        {view === "html" && (
          <div style={{ display: "flex", gap: 4 }}>
            <button type="button" style={tabStyle(width === "desktop")} onClick={() => setWidth("desktop")} data-tip="How it looks on a computer.">Desktop</button>
            <button type="button" style={tabStyle(width === "phone")} onClick={() => setWidth("phone")} data-tip="How it looks on a phone.">Phone</button>
          </div>
        )}
      </div>

      {error && <div className="form-error">{error}</div>}
      {!preview && !error && <p>Loading preview...</p>}

      {preview && (
        <div style={{ border: "1px solid var(--color-border, #ddd)", borderRadius: 10, overflow: "hidden", background: "var(--color-surface, #fff)" }}>
          <div style={{ padding: "10px 14px", fontSize: 13, borderBottom: "1px solid var(--color-border, #ddd)", lineHeight: 1.6 }}>
            <div><span style={{ color: "var(--color-text-muted)" }}>From:</span> IEMAS &lt;your outbound email account&gt;</div>
            <div><span style={{ color: "var(--color-text-muted)" }}>To:</span> {preview.recipient}</div>
            <div><span style={{ color: "var(--color-text-muted)" }}>Subject:</span> <b>{preview.subject}</b></div>
          </div>

          {view === "html" ? (
            <div style={{ background: "#f4eee8", display: "flex", justifyContent: "center" }}>
              <iframe
                title="Escalation email preview"
                sandbox="allow-same-origin"
                srcDoc={preview.html}
                onLoad={(e) => {
                  const doc = e.currentTarget.contentDocument;
                  if (doc) setFrameHeight(doc.documentElement.scrollHeight + 8);
                }}
                style={{ width: width === "phone" ? 390 : "100%", height: frameHeight, border: 0, background: "#f4eee8" }}
              />
            </div>
          ) : (
            <pre style={{ margin: 0, padding: 16, fontSize: 13, lineHeight: 1.6, whiteSpace: "pre-wrap", wordBreak: "break-word" }}>{preview.text}</pre>
          )}
        </div>
      )}
    </section>
  );
}
