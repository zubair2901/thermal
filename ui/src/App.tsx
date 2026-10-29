import { useEffect, useState } from "react";
import { isNative, send } from "./bridge";
import { bytes, shortCpu, shortGpu, uptime } from "./format";
import { useTelemetry } from "./history";
import Overview from "./Overview";
import Processes from "./Processes";
import Sensors from "./Sensors";

type View = "overview" | "processes" | "sensors";

const VIEWS: { id: View; label: string; key: string }[] = [
  { id: "overview", label: "Overview", key: "1" },
  { id: "processes", label: "Processes", key: "2" },
  { id: "sensors", label: "All sensors", key: "3" },
];

function load<T>(k: string, d: T): T {
  try {
    const v = localStorage.getItem(k);
    return v == null ? d : (JSON.parse(v) as T);
  } catch {
    return d;
  }
}

export default function App() {
  const { snap, history } = useTelemetry();
  const [view, setView] = useState<View>(() => load("view", "overview"));
  const [pollDgpu, setPollDgpu] = useState<boolean>(() => load("pollDgpu", true));

  useEffect(() => {
    try {
      localStorage.setItem("view", JSON.stringify(view));
    } catch {
      /* storage unavailable */
    }
  }, [view]);

  useEffect(() => {
    try {
      localStorage.setItem("pollDgpu", JSON.stringify(pollDgpu));
    } catch {
      /* storage unavailable */
    }
    send({ type: "pollDgpu", value: pollDgpu });
  }, [pollDgpu]);

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
          </button>
        ))}
        <div className="rail-foot">
          <label className="switch" title="Reading the RTX sensors keeps it awake. Turn off on battery to let it sleep.">
            <input type="checkbox" checked={pollDgpu} onChange={(e) => setPollDgpu(e.target.checked)} />
            <span>Read RTX sensors</span>
          </label>
          {snap && (
            <p className={snap.machine.elevated ? "priv ok" : "priv"}>{snap.machine.elevated ? "Administrator" : "Limited access"}</p>
          )}
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
                  {shortCpu(snap.cpu.name)}, {snap.gpus.map((g) => shortGpu(g.name)).join(" and ")}, {bytes(snap.memory.total)} memory. {snap.machine.os}.
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

            {snap.notes.length > 0 && (
              <ul className="notes">
                {snap.notes.map((n) => (
                  <li key={n}>{n}</li>
                ))}
              </ul>
            )}

            {view === "overview" && <Overview snap={snap} history={history} onOpenProcesses={() => setView("processes")} />}
            {view === "processes" && <Processes snap={snap} />}
            {view === "sensors" && <Sensors snap={snap} />}
          </>
        )}
      </main>
    </div>
  );
}
