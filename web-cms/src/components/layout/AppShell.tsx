import { useEffect, useState } from "react";
import { NavLink, Outlet, useLocation } from "react-router-dom";
import { apiClient } from "../../api/client";
import type { PauseControlDto } from "../../api/types";
import { useAuthStore } from "../../store/authStore";
import { setThemePreference } from "../../theme";
import { MENU, findMenuLocation, helpAnchor } from "./navigation";
import NavIcon from "./NavIcon";
import PageIntro from "../help/PageIntro";
import { hasGuide, loadSeenGuides, saveSeenGuides } from "../help/guideStorage";
import { TooltipLayer } from "../help/Tooltip";

export default function AppShell() {
  const user = useAuthStore((s) => s.user);
  const logout = useAuthStore((s) => s.logout);
  const location = useLocation();
  const [organizationName, setOrganizationName] = useState("IEMAS");
  const [pausedLabels, setPausedLabels] = useState<string[]>([]);
  // The theme in force right now (light or dark), also when it follows the Windows setting.
  const [theme, setTheme] = useState<"light" | "dark">(() => (document.documentElement.dataset.theme === "light" ? "light" : "dark"));

  const here = findMenuLocation(location.pathname, location.search);
  // The page the user is on: the open tab when the menu entry has tabs. Its old address keys the help text and the page guide.
  const activeItem = here ? { path: here.tab?.legacyPath ?? here.item.path, label: here.tab?.label ?? here.item.label } : undefined;

  // First-visit page guide: shown until dismissed on this browser, and re-opened from "Page guide" in the header.
  const [seenGuides, setSeenGuides] = useState<string[]>(loadSeenGuides);
  const [guideOpenFor, setGuideOpenFor] = useState<string | null>(null);
  const guidePath = activeItem && hasGuide(activeItem.path) ? activeItem.path : null;
  const showGuide = guidePath !== null && (guideOpenFor === guidePath || !seenGuides.includes(guidePath));

  function dismissGuide() {
    if (!guidePath) return;
    setGuideOpenFor(null);
    if (!seenGuides.includes(guidePath)) {
      const next = [...seenGuides, guidePath];
      setSeenGuides(next);
      saveSeenGuides(next);
    }
  }

  function toggleGuide() {
    if (!guidePath) return;
    if (showGuide) dismissGuide();
    else setGuideOpenFor(guidePath);
  }

  function toggleTheme() {
    const next = theme === "dark" ? "light" : "dark";
    setTheme(next);
    setThemePreference(next);
  }

  useEffect(() => {
    apiClient
      .get<{ organizationName: string }>("/system-settings/public")
      .then((r) => setOrganizationName(r.data.organizationName))
      .catch(() => undefined);
  }, []);

  // Re-checked on navigation so a pause set on the Maintenance page shows up immediately elsewhere.
  useEffect(() => {
    apiClient
      .get<PauseControlDto[]>("/maintenance/pause")
      .then((r) => setPausedLabels(r.data.filter((c) => c.isPaused).map((c) => c.label.replace(/^Pause /, ""))))
      .catch(() => setPausedLabels([]));
  }, [location.pathname]);

  return (
    <div className="app-shell">
      <aside className="app-sidebar">
        <div className="app-brand" title="IEMAS — Intelligent Email Monitoring & Alert System">
          <img src="/sawo-logo.webp" alt="SAWO" className="brand-logo" />
          <span className="app-brand-name">{organizationName}</span>
        </div>
        <nav>
          {MENU.map((item) => [
            item.heading && <div key={`${item.id}-heading`} className="nav-heading">{item.heading}</div>,
            <NavLink key={item.id} to={item.path} end={item.path === "/"}
              className={() => "nav-top" + (here?.item.id === item.id ? " active" : "")}>
              <span className="nav-chip"><NavIcon name={item.icon} size={16} /></span>
              <span>{item.label}</span>
            </NavLink>,
          ])}
        </nav>
        <div className="sidebar-footer">
          <div className="sidebar-footer-card">
            <div className="sidebar-footer-id">
              <div className="sidebar-footer-avatar">{(user?.displayName ?? "?").trim().charAt(0).toUpperCase()}</div>
              <div className="sidebar-footer-username" title={user?.displayName}>{user?.displayName}</div>
            </div>
            <button className="app-header-logout" onClick={() => logout()}>Sign out</button>
          </div>
        </div>
      </aside>

      <div className="app-main">
        <header className="app-header">
          <div className="app-header-titles">
            <div className="app-header-title">{here?.item.label ?? "IEMAS"}</div>
            {here?.tab && here.tab.label !== here.item.label && <div className="app-header-sub">{here.tab.label}</div>}
          </div>
          {activeItem && activeItem.path !== "/help" && (
            <NavLink to={`/help#${helpAnchor(activeItem.path)}`} className="app-header-help" title={`What is ${activeItem.label}?`}>
              <NavIcon name="help" size={16} /> Help for this page
            </NavLink>
          )}
          {guidePath && (
            <button type="button" className="app-header-guide" aria-pressed={showGuide} onClick={toggleGuide}
              data-tip={showGuide ? "Hide the guide for this page." : "Show the short guide for this page again."}>
              Page guide
            </button>
          )}
          <button type="button" className="theme-toggle" onClick={toggleTheme}
            aria-label={theme === "dark" ? "Switch to the light theme" : "Switch to the dark theme"}
            title={theme === "dark" ? "Switch to the light theme" : "Switch to the dark theme"}>
            <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
              {theme === "dark"
                ? <path d="M12 16a4 4 0 1 0 0-8 4 4 0 0 0 0 8zM12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4" />
                : <path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z" />}
            </svg>
          </button>
        </header>
        <main className="app-content">
          {pausedLabels.length > 0 && (
            <NavLink to="/maintenance" style={pauseBannerStyle}>
              Emergency Pause active: {pausedLabels.join(", ")}
            </NavLink>
          )}
          {showGuide && guidePath && activeItem && (
            <PageIntro key={guidePath} path={guidePath} pageTitle={activeItem.label}
              firstEver={seenGuides.length === 0} onDismiss={dismissGuide} />
          )}
          <Outlet />
        </main>
      </div>
      <TooltipLayer />
    </div>
  );
}

const pauseBannerStyle: React.CSSProperties = {
  display: "block",
  background: "var(--color-danger)",
  color: "#fff",
  padding: "8px 12px",
  borderRadius: 6,
  marginBottom: 16,
  textDecoration: "none",
};
