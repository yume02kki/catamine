import { useEffect, useState } from "react";
import { Link, NavLink, Outlet, matchPath, useLocation } from "react-router-dom";
import { useJobs, useMission, useProxies } from "../api/queries";
import { CommandPalette } from "./CommandPalette";
import { CatLogo, IconFleet, IconGrid, IconJobs, IconPanel, IconRules } from "./Icons";
import { Kitty, type Mood } from "./Kitty";

function MissionSubnav({ id }: { id: string }) {
  const { data: m } = useMission(id);
  return (
    <div className="subnav">
      <div className="subnav-title" title={m?.name}>{m?.name ?? "…"}</div>
      <NavLink end to={`/missions/${id}`} className="subnav-link">Traffic</NavLink>
      <NavLink to={`/missions/${id}/rules`} className="subnav-link">Rules{m ? ` · ${m.ruleCount}` : ""}</NavLink>
      <NavLink to={`/missions/${id}/replay`} className="subnav-link">Replay &amp; fuzz</NavLink>
      <NavLink to={`/missions/${id}/settings`} className="subnav-link">Settings</NavLink>
    </div>
  );
}

function useKittyStatus(): { mood: Mood; text: string; tone: "ok" | "warn" | "off" } {
  const { data: proxies, isError } = useProxies();
  if (isError) return { mood: "alert", text: "api unreachable", tone: "warn" };
  if (!proxies) return { mood: "sleepy", text: "waking up…", tone: "off" };
  if (proxies.length === 0) return { mood: "sleepy", text: "no proxy heartbeat", tone: "off" };
  const open = proxies.flatMap((p) => Object.entries(p.metrics.circuits ?? {}).filter(([, s]) => s !== "closed").map(([m]) => m));
  if (open.length > 0) return { mood: "alert", text: `circuit open: ${[...new Set(open)].join(", ")}`, tone: "warn" };
  const queue = proxies.reduce((s, p) => s + p.metrics.queueDepth, 0);
  return { mood: queue === 0 ? "happy" : "idle", text: `${proxies.length} ${proxies.length === 1 ? "proxy" : "proxies"} purring`, tone: "ok" };
}

export function Shell() {
  const loc = useLocation();
  const missionMatch = matchPath("/missions/:id/*", loc.pathname);
  const missionId = missionMatch?.params.id;
  const onDiff = !!matchPath("/missions/:id/logs/:logId", loc.pathname);
  const [collapsed, setCollapsed] = useState(onDiff);
  useEffect(() => setCollapsed(onDiff), [onDiff]);
  const { data: jobs } = useJobs();
  const running = jobs?.items.filter((j) => j.status === "running" || j.status === "queued").length ?? 0;
  const kitty = useKittyStatus();

  return (
    <div className={`app ${collapsed ? "app-collapsed" : ""}`}>
      <nav className="sidebar" aria-label="Primary">
        <div className="brand">
          <Link to="/" className="brand-logo" aria-label="KittyParty home"><CatLogo width={20} height={20} /></Link>
          {!collapsed && (
            <div className="brand-text"><span className="brand-name">KittyParty</span><span className="brand-sub">API Diff Mirror</span></div>
          )}
          <button type="button" className="icon-btn collapse-btn" aria-label={collapsed ? "Expand sidebar" : "Collapse sidebar"} onClick={() => setCollapsed((c) => !c)}>
            <IconPanel />
          </button>
        </div>
        <NavLink to="/" end={false} className={({ isActive }) => `nav-link ${isActive && (loc.pathname === "/" || missionId) ? "active" : ""}`} title="Missions">
          <IconGrid /> {!collapsed && <span>Missions</span>}
        </NavLink>
        {missionId && !collapsed && <MissionSubnav id={missionId} />}
        {missionId && collapsed && (
          <NavLink to={`/missions/${missionId}/rules`} className="nav-link" title="Rules"><IconRules /></NavLink>
        )}
        <NavLink to="/jobs" className="nav-link" title="Jobs">
          <IconJobs /> {!collapsed && <span>Jobs</span>}
          {!collapsed && running > 0 && <span className="nav-count warn">{running} running</span>}
        </NavLink>
        <NavLink to="/fleet" className="nav-link" title="Proxy fleet">
          <IconFleet /> {!collapsed && <span>Proxy fleet</span>}
        </NavLink>
        <div className="sidebar-foot">
          <Kitty mood={kitty.mood} size={collapsed ? "face" : "lg"} />
          {!collapsed && (
            <div className="foot-status"><span className={`dot dot-${kitty.tone}`} />{kitty.text}</div>
          )}
          {!collapsed && <div className="foot-hint"><kbd>⌘K</kbd> jump anywhere</div>}
        </div>
      </nav>
      <main className="content">
        <Outlet />
      </main>
      <CommandPalette />
    </div>
  );
}
