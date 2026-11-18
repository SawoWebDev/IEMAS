import { useEffect, useState } from "react";

/**
 * The rollout tutorial (server setup, CMS setup, Windows Agent install, first use, alert simulator, go-live
 * checklist). It is a self-contained static page in public/rollout-guide so it can also be opened on its own
 * or handed to IT; here it is shown inside the CMS and follows the CMS's light/dark choice.
 */
const GUIDE_PATH = "/rollout-guide/index.html";

const currentTheme = () => (document.documentElement.dataset.theme === "light" ? "light" : "dark");

export default function TutorialPage() {
  const [theme, setTheme] = useState(currentTheme);

  // Follow the theme switch in the header while the tutorial is open.
  useEffect(() => {
    const observer = new MutationObserver(() => setTheme(currentTheme()));
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });
    return () => observer.disconnect();
  }, []);

  const src = `${GUIDE_PATH}?theme=${theme}`;

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 12, height: "calc(100vh - 120px)", minHeight: 520 }}>
      <div style={{ display: "flex", flexWrap: "wrap", alignItems: "flex-end", justifyContent: "space-between", gap: 12 }}>
        <div>
          <h1 style={{ margin: 0 }}>Rollout Tutorial</h1>
          <p style={{ color: "var(--color-text-muted)", fontSize: 13, maxWidth: 820, margin: "6px 0 0" }}>
            How to put this version live: set up the server PC, configure the CMS, install the IEMAS Agent on each
            employee&apos;s PC, and what employees see the first time, including a simulator of every pop-up alert.
          </p>
        </div>
        <a href={src} target="_blank" rel="noopener" style={{ fontSize: 13 }}
           data-tip="Opens the tutorial on its own page, for example to print it or send the address to IT.">
          Open in a new tab ↗
        </a>
      </div>
      <iframe
        key={theme}
        src={src}
        title="IEMAS rollout tutorial"
        style={{ flex: 1, width: "100%", border: "1px solid var(--color-border)", borderRadius: 8, background: "var(--color-surface)" }}
      />
    </div>
  );
}
