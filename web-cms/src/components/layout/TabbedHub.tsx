import { useSearchParams } from "react-router-dom";
import { MENU } from "./navigation";

export interface HubTab {
  id: string;
  element: React.ReactNode;
}

/**
 * One menu entry that holds several pages as tabs. The open tab lives in the address (?tab=...), so a tab can be
 * bookmarked or linked to, and the browser's Back button moves between tabs. Tab names come from the menu.
 */
export default function TabbedHub({ menuId, tabs }: { menuId: string; tabs: HubTab[] }) {
  const [params, setParams] = useSearchParams();
  const labels = MENU.find((m) => m.id === menuId)?.tabs ?? [];
  const active = tabs.find((t) => t.id === params.get("tab")) ?? tabs[0];

  return (
    <div>
      <div role="tablist" style={barStyle}>
        {tabs.map((t) => {
          const selected = t.id === active.id;
          return (
            <button key={t.id} role="tab" aria-selected={selected} onClick={() => setParams({ tab: t.id })}
              style={{ ...tabStyle, ...(selected ? selectedStyle : null) }}>
              {labels.find((l) => l.id === t.id)?.label ?? t.id}
            </button>
          );
        })}
      </div>
      <div key={active.id}>{active.element}</div>
    </div>
  );
}

const barStyle: React.CSSProperties = { display: "flex", gap: 4, flexWrap: "wrap", borderBottom: "1px solid var(--color-border)", marginBottom: 16 };
const tabStyle: React.CSSProperties = {
  background: "transparent", border: "none", borderBottom: "2px solid transparent", borderRadius: 0, padding: "8px 14px",
  cursor: "pointer", color: "var(--color-text-muted)", fontSize: 14, marginBottom: -1,
};
const selectedStyle: React.CSSProperties = { color: "var(--color-text)", borderBottomColor: "var(--color-accent)", fontWeight: 600 };
