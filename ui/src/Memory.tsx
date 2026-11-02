import { useEffect, useRef, useState } from "react";
import { isNative, listen, send } from "./bridge";
import { bytes } from "./format";
import type { MemoryApp, MemoryDetail, Snapshot, StartupApp } from "./types";

type Pending = { name: string; label: string; action: "restart" | "close"; used: number; appBytes: number; at: number };

function Where({ d, used }: { d: MemoryDetail; used: number }) {
  const parts = [
    { label: "Apps", value: d.appBytes, cls: "m-apps", note: "Memory each app keeps to itself. This is what Task Manager's list adds up to." },
    { label: "Compressed", value: d.compressed, cls: "m-comp", note: "Idle app memory Windows squeezed to make room. It isn't listed under any app." },
    { label: "Windows and drivers", value: d.kernel, cls: "m-kernel", note: "The Windows kernel and hardware drivers. It isn't listed under any app." },
    { label: "Graphics", value: d.graphics, cls: "m-gpu", note: "System memory the GPUs borrowed for textures and video." },
    { label: "Shared and other", value: d.other, cls: "m-other", note: "Code and data several apps share, open files and virtual machines. Windows doesn't split this by app." },
    { label: "Cached", value: d.cached, cls: "m-cache", note: "Recently used files, kept in case they're needed again. Windows gives this up the moment an app asks, so it counts as available." },
    { label: "Free", value: d.free, cls: "m-free", note: "Not used for anything." },
    { label: "Reserved by hardware", value: d.reserved, cls: "m-hw", note: "Set aside at startup, mostly for integrated graphics, so Windows can never use it. Some laptops let you lower this in the BIOS." },
  ].filter((p) => p.value > 1 << 24);
  const hidden = used - d.appBytes;

  return (
    <section className="panel">
      <h3>Where {bytes(d.installed)} goes</h3>
      <div className="split" role="img" aria-label="Memory by use">
        {parts.map((p) => (
          <span key={p.label} className={p.cls} style={{ flexGrow: p.value }} title={`${p.label}: ${bytes(p.value)}`} />
        ))}
      </div>
      <ul className="mem-parts">
        {parts.map((p) => (
          <li key={p.label}>
            <i className={p.cls} />
            <span>
              {p.label}
              <small>{p.note}</small>
            </span>
            <b>{bytes(p.value)}</b>
          </li>
        ))}
      </ul>
      {hidden > 1 << 29 && (
        <p className="muted">
          Task Manager's list only adds up to about {bytes(d.appBytes)}. The other {bytes(hidden)} in use is the memory no app owns, itemised above.
        </p>
      )}
    </section>
  );
}

function explain(a: MemoryApp) {
  if (a.kind === "browser") return `Every site, extension and helper runs as its own process, so ${a.processes} processes is normal. Thermal can't see inside tabs: to find a heavy one, open ${a.title} and press Shift+Esc.`;
  if (a.kind === "webapp") return `${a.title} is built on a browser engine, so it runs like a small web browser, with its own pages, graphics and helper processes.${a.canClose ? " Restarting it clears what it has built up." : ""}`;
  return "";
}

function AppRow({ app, max, onAct }: { app: MemoryApp; max: number; onAct: (a: MemoryApp, action: "restart" | "close") => void }) {
  const [confirm, setConfirm] = useState<"restart" | "close" | null>(null);
  const note = explain(app);
  return (
    <li className="wake mem-app">
      <div className="wake-name">
        <span className="app-bar" style={{ width: `${(100 * app.bytes) / max}%` }} />
        <span className="app-name">
          {app.title} <b className="mem-size">{bytes(app.bytes)}</b>
          {app.processes > 1 && <small> {app.processes} processes</small>}
        </span>
        {app.roles.length > 0 && <p className="wake-status">{app.roles.map((r) => `${r.name} ${bytes(r.bytes)}${r.count > 1 ? ` (${r.count})` : ""}`).join("  ·  ")}</p>}
        {note && <p className="wake-status">{note}</p>}
        {app.growth > 0 && (
          <p className="wake-status climb">
            Up {bytes(app.growth)} in the last {app.growthMinutes} minutes and still climbing. Restarting it should get that back.
          </p>
        )}
      </div>
      {app.canClose && (
        <div className="wake-actions">
          {confirm ? (
            <>
              <span className="muted">
                {confirm === "restart" ? "Restart" : "Close"} {app.title}? Unsaved work may be lost.
              </span>
              <button
                className="danger"
                onClick={() => {
                  onAct(app, confirm);
                  setConfirm(null);
                }}
              >
                {confirm === "restart" ? "Restart" : "Close"}
              </button>
              <button onClick={() => setConfirm(null)}>Cancel</button>
            </>
          ) : (
            <>
              <button disabled={!isNative} onClick={() => setConfirm("restart")}>
                Restart
              </button>
              <button disabled={!isNative} onClick={() => setConfirm("close")}>
                Close
              </button>
            </>
          )}
        </div>
      )}
    </li>
  );
}

function Startup() {
  const [apps, setApps] = useState<StartupApp[] | null>(isNative ? null : []);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    const off = listen<{ apps: StartupApp[]; error: string | null }>("startupApps", (r) => {
      setApps(r.apps);
      setError(r.error);
    });
    send({ type: "startupApps" });
    return off;
  }, []);

  const on = apps?.filter((a) => a.enabled) ?? [];
  const heavy = on.reduce((t, a) => t + a.bytes, 0);
  return (
    <section className="panel">
      <h3>Apps that start with Windows</h3>
      <p className="muted">
        {apps == null
          ? "Reading the startup list…"
          : `${on.length} turned on, using ${bytes(heavy)} right now. Turning one off keeps it installed and takes effect the next time you sign in, so a copy that's already running stays open.`}
      </p>
      {error && <p className="wake-status err">{error}</p>}
      <ul className="startup">
        {apps?.map((a) => (
          <li key={a.id}>
            <label className="check">
              <input type="checkbox" checked={a.enabled} onChange={(e) => send({ type: "startupSet", id: a.id, enabled: e.target.checked })} />
              <span>{a.name}</span>
            </label>
            <span className="muted">{a.running ? bytes(a.bytes) : "Not running"}</span>
          </li>
        ))}
      </ul>
    </section>
  );
}

export default function Memory({ snap }: { snap: Snapshot }) {
  const d = snap.memoryDetail;
  const [results, setResults] = useState<string[]>([]);
  const pending = useRef<Pending[]>([]);

  useEffect(
    () =>
      listen<{ name: string; action: string; error: string | null }>("appAction", (r) => {
        const p = pending.current.find((x) => x.name === r.name);
        if (!p || !r.error) return;
        pending.current = pending.current.filter((x) => x !== p);
        setResults((s) => [...s, r.error as string]);
      }),
    [],
  );

  // Report what actually came back, once Windows has reclaimed it and a restarted app has settled.
  useEffect(() => {
    const due = pending.current.filter((p) => snap.t - p.at > (p.action === "close" ? 5000 : 15000));
    if (due.length === 0) return;
    pending.current = pending.current.filter((p) => !due.includes(p));
    setResults((s) => [
      ...s,
      ...due.map((p) => {
        if (p.action === "close") {
          const freed = p.used - snap.memory.used;
          return freed > 1 << 24 ? `Closed ${p.label}. That freed ${bytes(freed)}.` : `Closed ${p.label}. Other apps took up the memory it freed.`;
        }
        const now = snap.memoryDetail?.apps.find((a) => a.name === p.name)?.bytes;
        if (now == null) return `Restarted ${p.label}. It's using under 100 MB so far, down from ${bytes(p.appBytes)}.`;
        return now < p.appBytes ? `Restarted ${p.label}. It's using ${bytes(now)}, down from ${bytes(p.appBytes)}.` : `Restarted ${p.label}. It's back up to ${bytes(now)} while it loads.`;
      }),
    ]);
  }, [snap]);

  if (!d) return <p className="empty">Memory readings start after the first few seconds.</p>;

  const m = snap.memory;
  const act = (a: MemoryApp, action: "restart" | "close") => {
    pending.current.push({ name: a.name, label: a.title, action, used: m.used, appBytes: a.bytes, at: snap.t });
    send(action === "restart" ? { type: "restartApp", name: a.name, path: a.path, appId: a.appId } : { type: "closeApp", name: a.name, path: a.path });
  };
  const verdict = {
    fine: `You have room. ${bytes(d.available)} is ready for apps to use, so nothing needs closing.`,
    tight: `Getting tight. ${bytes(d.available)} is left before Windows has to start moving apps to disk, which slows things down.`,
    low: `Running out. Only ${bytes(d.available)} is left, so Windows is likely moving apps to disk. Close or restart something below.`,
  }[d.pressure];
  const max = Math.max(1, ...d.apps.map((a) => a.bytes));

  return (
    <div className="battery">
      <section className="drain">
        <p className="drain-n">
          {(m.used / 1024 ** 3).toFixed(1)}
          <small> GB in use</small>
        </p>
        <p className="drain-l">{verdict}</p>
      </section>

      <Where d={d} used={m.used} />

      <section className="panel">
        <h3>Apps using the most</h3>
        {results.length > 0 && (
          <div className="callout">
            {results.map((r, i) => (
              <p key={i}>{r}</p>
            ))}
          </div>
        )}
        <ul className="wakes">
          {d.apps.map((a) => (
            <AppRow key={`${a.name}|${a.path}`} app={a} max={max} onAct={act} />
          ))}
        </ul>
      </section>

      <Startup />

      <p className="footnote">
        There's no button here to "clean" memory, on purpose. Those tools push apps out to disk: the free number jumps, then everything slows down while apps load back in. Memory is only
        really freed when an app closes or restarts.
      </p>
    </div>
  );
}
