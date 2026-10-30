import { useEffect, useState } from "react";
import { isNative, listen, send } from "./bridge";
import { bytes, shortCpu, shortGpu, uptime } from "./format";
import { useTelemetry } from "./history";
import { useHost } from "./host";
import Overview from "./Overview";
import Battery from "./Battery";
import Processes from "./Processes";
import Sensors from "./Sensors";
import Sessions from "./Sessions";
import SettingsView from "./SettingsView";
import type { Note } from "./types";

type View = "overview" | "processes" | "battery" | "sensors" | "sessions" | "settings";

const VIEWS: { id: View; label: string; key: string }[] = [
  { id: "overview", label: "Overview", key: "1" },
  { id: "processes", label: "Processes", key: "2" },
  { id: "battery", label: "Battery", key: "3" },
  { id: "sensors", label: "All sensors", key: "4" },
  { id: "sessions", label: "Sessions", key: "5" },
  { id: "settings", label: "Settings", key: "6" },
];

function load<T>(k: string, d: T): T {
  try {
    const v = localStorage.getItem(k);
    return v == null ? d : (JSON.parse(v) as T);
  } catch {
    return d;
  }
}

function Notes({ notes }: { notes: Note[] }) {
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<{ ok: boolean; message: string } | null>(null);

  useEffect(
    () =>
      listen<{ ok: boolean; message: string }>("pawnio", (r) => {
        setBusy(false);
        setResult(r);
      }),
    [],
  );

  if (notes.length === 0 && !result) return null;
  return (
    <ul className="notes">
      {notes.map((n) => (
        <li key={n.id}>
          <span>{n.text}</span>
          {n.action === "installPawnIO" && (
            <span className="note-actions">
              <button
                className="primary"
                disabled={busy || !isNative}
                onClick={() => {
                  setBusy(true);
                  setResult(null);
                  send({ type: "installPawnIO" });
                }}
              >
                {busy ? "Installing PawnIO…" : "Install PawnIO"}
              </button>
              <button className="link" onClick={() => send({ type: "link", url: "https://pawnio.eu" })}>
                What is PawnIO?
              </button>
            </span>
          )}
        </li>
      ))}
      {result && <li className={result.ok ? "ok" : undefined}>{result.message}</li>}
    </ul>
  );
}

export default function App() {
  const { snap, history } = useTelemetry();
  const [host, updateSettings] = useHost();
  const [view, setView] = useState<View>(() => load("view", "overview"));

  useEffect(() => {
    try {
      localStorage.setItem("view", JSON.stringify(view));
    } catch {
      /* storage unavailable */
    }
  }, [view]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (!e.ctrlKey) return;
      const v = VIEWS.find((x) => x.key === e.key);
      if (v) {
        e.preventDefault();
        setView(v.id);
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);

  return (
    <div className="shell">
      <nav className="rail" aria-label="Views">
        <img className="mark" src="/icon.png" alt="" width={28} height={28} />
        {VIEWS.map((v) => (
          <button key={v.id} className="rail-btn" aria-current={view === v.id ? "page" : undefined} onClick={() => setView(v.id)} title={`Ctrl+${v.key}`}>
            {v.label}
            {v.id === "sessions" && snap?.recording?.active && <i className="rec-dot" aria-label="recording" />}
          </button>
        ))}
        <div className="rail-foot">
          {host && (
            <>
              <label className="switch">
                <input type="checkbox" checked={host.settings.overlayEnabled} onChange={(e) => updateSettings({ overlayEnabled: e.target.checked })} />
                <span>Overlay</span>
                <small>{host.settings.overlayHotkey !== "None" ? host.settings.overlayHotkey : ""}</small>
              </label>
              <label className="switch">
                <input type="checkbox" checked={host.settings.widgetEnabled} onChange={(e) => updateSettings({ widgetEnabled: e.target.checked })} />
                <span>Widget</span>
              </label>
            </>
          )}
          {snap && <p className={snap.machine.elevated ? "priv ok" : "priv"}>{snap.machine.elevated ? "Administrator" : "Limited access"}</p>}
          {!isNative && <p className="priv">Preview data</p>}
        </div>
      </nav>

      <main className="main">
        {!snap ? (
          <div className="boot">
            <p>Starting sensors…</p>
            <small>The first reading takes a couple of seconds while hardware is detected.</small>
          </div>
        ) : (
          <>
            <header className="head">
              <div>
                <h1>{snap.machine.model || snap.machine.name}</h1>
                <p className="sub">
                  {shortCpu(snap.cpu.name)}, {snap.gpus.map((g) => shortGpu(g.name, snap.cpu.name)).join(" and ")}, {bytes(snap.memory.total)} memory. {snap.machine.os}.
                </p>
              </div>
              <dl className="head-facts">
                <div>
                  <dt>Up for</dt>
                  <dd>{uptime(snap.machine.uptimeSec)}</dd>
                </div>
                {snap.battery && (
                  <div>
                    <dt>{snap.battery.onAc ? "Plugged in" : "On battery"}</dt>
                    <dd>
                      {snap.battery.charge}%
                      {snap.battery.rateW != null && snap.battery.rateW !== 0 && <small> {snap.battery.rateW > 0 ? "+" : ""}{snap.battery.rateW} W</small>}
                    </dd>
                  </div>
                )}
              </dl>
            </header>

            <Notes notes={snap.notes} />

            {view === "overview" && <Overview snap={snap} history={history} onOpenProcesses={() => setView("processes")} />}
            {view === "processes" && <Processes snap={snap} />}
            {view === "battery" && <Battery snap={snap} onReadSensors={(v) => updateSettings({ pollDgpu: v })} />}
            {view === "sensors" && <Sensors snap={snap} />}
            {view === "sessions" && host && <Sessions snap={snap} host={host} />}
            {view === "settings" && host && <SettingsView host={host} update={updateSettings} />}
          </>
        )}
      </main>
    </div>
  );
}
