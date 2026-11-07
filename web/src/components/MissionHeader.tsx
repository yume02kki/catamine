import type { ReactNode } from "react";
import { Link } from "react-router-dom";
import { useConfig } from "../api/queries";
import type { Mission } from "../api/types";
import { CopyButton } from "./Bits";
import { IconBack } from "./Icons";

export function MissionHeader({ mission, title, children, back = "/" }: { mission: Mission; title?: ReactNode; children?: ReactNode; back?: string }) {
  const paused = !mission.enabled || !mission.mirrorEnabled || mission.sampleRate === 0;
  return (
    <header className="page-head">
      <Link to={back} className="icon-btn" aria-label="Back"><IconBack /></Link>
      <span className={`dot ${paused ? "dot-off" : "dot-pink"}`} />
      <h1>{title ?? mission.name}</h1>
      <div className="head-actions">{children}</div>
    </header>
  );
}

export function ProxyBar({ mission }: { mission: Mission }) {
  const { data: cfg } = useConfig();
  const url = `${cfg?.proxyPublicUrl ?? ""}${mission.proxyPath}/`;
  const curl = `curl -i '${url}'`;
  return (
    <div className="proxy-bar">
      <b>PROXY</b>
      <span className="mono proxy-url">{url}</span>
      <span className="proxy-meta">→ <span className="mono">{mission.mainUrl}</span></span>
      <span className="proxy-actions">
        <CopyButton text={url} />
        <CopyButton text={curl} label="curl" />
      </span>
    </div>
  );
}
