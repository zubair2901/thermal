import { Ribbon, Trace } from "./Canvas";
import { bytes, heat, num, rate, scale, shortCpu, shortGpu } from "./format";
import type { Series } from "./history";
import type { Gpu, Snapshot } from "./types";

interface Props {
  snap: Snapshot;
  history: Map<string, Series>;
  onOpenProcesses: () => void;
}

function Temp({ c, missing = "No reading" }: { c: number | null; missing?: string }) {
  if (c == null) return <div className="temp temp-missing">{missing}</div>;
  return (
    <div className="temp" style={{ color: heat(scale.temp(c)) }}>
      {Math.round(c)}
      <span className="deg">°C</span>
    </div>
  );
}

const rpm = (r: number | null | undefined) => (r === 0 ? "Stopped" : num(r));

function Stat({ label, value, unit }: { label: string; value: string; unit?: string }) {
  return (
    <div className="stat">
      <dt>{label}</dt>
      <dd>
        {value}
        {unit && /\d/.test(value) && <small> {unit}</small>}
      </dd>
    </div>
  );
}

function CpuInstrument({ snap, history }: { snap: Snapshot; history: Map<string, Series> }) {
  const c = snap.cpu;
  const cpuFan = snap.fans.find((f) => /cpu/i.test(f.name));
  return (
    <section className="inst" aria-label="Processor">
      <h2>{shortCpu(c.name)}</h2>
      <div className="inst-top">
        <Temp c={c.temp} />
        <div className="load">
          <span className="load-n" style={{ color: heat(scale.pct(c.load)) }}>{num(c.load)}%</span>
          <span className="load-l">load</span>
        </div>
      </div>
      <p className="src">{c.temp == null ? "Temperature unavailable" : `Measured at ${c.tempSource}`}</p>
      <div className="cores" aria-label="Load per thread">
        {c.cores.map((v, i) => (
          <i key={i} title={`Thread ${i + 1}: ${num(v)}%`}>
            <b style={{ height: `${Math.max(2, v)}%`, background: heat(scale.pct(v)) }} />
          </i>
        ))}
      </div>
      <Trace series={history.get("cpu.load")} />
      <dl className="stats">
        <Stat label="Clock" value={c.clockAvg == null ? "–" : (c.clockAvg / 1000).toFixed(2)} unit="GHz" />
        <Stat label="Power" value={num(c.power, 1)} unit="W" />
        <Stat label="Fan" value={rpm(cpuFan?.rpm)} unit="rpm" />
        <Stat label="Threads" value={String(c.cores.length)} />
      </dl>
    </section>
  );
}

function GpuInstrument({ g, snap, history }: { g: Gpu; snap: Snapshot; history: Map<string, Series> }) {
  const fan = g.fanRpm ?? (g.kind === "discrete" ? snap.fans.find((f) => /gpu/i.test(f.name))?.rpm ?? null : null);
  const memPct = g.memUsed != null && g.memTotal ? (100 * g.memUsed) / g.memTotal : null;
  // Thermal reads all GPU temperatures over one connection, which it keeps closed while no app uses the dedicated GPU.
  const dgpu = snap.gpus.find((x) => x.kind === "discrete");
  const paused = g.kind === "integrated" && g.temp == null && !snap.gpuSensorsConnected && !!dgpu;
  return (
    <section className={g.active ? "inst" : "inst asleep"} aria-label={g.name}>
      <h2>
        {shortGpu(g.name, snap.cpu.name)}
        <span className="kind">{g.kind === "discrete" ? "dedicated" : "integrated"}</span>
      </h2>
      <div className="inst-top">
        <Temp
          c={g.temp}
          missing={
            g.kind === "discrete"
              ? !g.active
                ? "Asleep"
                : !snap.gpuSensorsConnected
                  ? "Not in use"
                  : "No temp sensor"
              : paused
                ? "Temp paused"
                : "No temp sensor"
          }
        />
        <div className="load">
          <span className="load-n" style={{ color: heat(scale.pct(g.load)) }}>{num(g.load)}%</span>
          <span className="load-l">load</span>
        </div>
      </div>
      <p className="src">
        {g.temp == null
          ? g.kind === "discrete"
            ? g.active
              ? "Sensors connect while an app uses it"
              : "Asleep, drawing no power"
            : paused
              ? `Paused while no app uses the ${shortGpu(dgpu!.name, snap.cpu.name)}, so it can sleep. This GPU shares the CPU die, so the CPU temperature is a close guide.`
              : "This GPU doesn't report a temperature"
          : g.hotspot != null
            ? `Hot spot ${Math.round(g.hotspot)} °C`
            : "Measured at the GPU core"}
      </p>
      <div className="engines">
        {(g.engines.length ? g.engines.slice(0, 3) : [{ name: "Idle", load: 0 }]).map((e) => (
          <div key={e.name} className="engine">
            <span>{e.name}</span>
            <meter min={0} max={100} value={e.load} style={{ ["--c" as string]: heat(scale.pct(e.load)) }} />
            <span className="engine-n">{num(e.load)}%</span>
          </div>
        ))}
      </div>
      <Trace series={history.get(`${g.id}.load`)} />
      <dl className="stats">
        <Stat label="Core clock" value={num(g.clockCore)} unit="MHz" />
        <Stat label="Power" value={num(g.power, 1)} unit="W" />
        <Stat
          label={g.kind === "discrete" ? (g.active ? "Fan" : "Fan, cooling the CPU") : "Shared memory"}
          value={g.kind === "discrete" ? rpm(fan) : bytes(g.memShared)}
          unit={g.kind === "discrete" ? "rpm" : undefined}
        />
        <Stat label={g.kind === "discrete" ? "VRAM" : "Reserved memory"} value={`${bytes(g.memUsed)} of ${bytes(g.memTotal, 0)}`} />
      </dl>
      {memPct != null && <meter className="vram" min={0} max={100} value={memPct} style={{ ["--c" as string]: heat(memPct / 100) }} />}
    </section>
  );
}

function Fans({ snap }: { snap: Snapshot }) {
  const max = 6000;
  return (
    <section className="panel" aria-label="Fans">
      <h3>Fans</h3>
      {snap.fans.some((f) => f.source === "HP BIOS") && (
        <p className="muted fan-note">Both fans cool one shared heatsink, so the GPU fan also spins for the CPU, even while the GPU sleeps.</p>
      )}
      {snap.fans.length === 0 ? (
        <p className="empty">No fan readings. Run Thermal as administrator so it can query the HP BIOS.</p>
      ) : (
        snap.fans.map((f) => (
          <div className="fan" key={f.name}>
            <div className="fan-row">
              <span>{f.name}</span>
              <strong style={{ color: f.rpm ? heat(scale.rpm(f.rpm, max)) : undefined }}>{f.rpm ? f.rpm.toLocaleString() : "Stopped"}</strong>
              {!!f.rpm && <small>rpm</small>}
            </div>
            <meter min={0} max={max} value={f.rpm ?? 0} style={{ ["--c" as string]: heat(scale.rpm(f.rpm, max)) }} />
          </div>
        ))
      )}
    </section>
  );
}

function Memory({ snap }: { snap: Snapshot }) {
  const m = snap.memory;
  const usedPct = (100 * m.used) / m.total;
  const top = [...snap.processes].sort((a, b) => b.ram - a.ram).slice(0, 4);
  return (
    <section className="panel" aria-label="Memory">
      <h3>Memory</h3>
      <p className="big">
        {bytes(m.used)} <small>of {bytes(m.total)} in use</small>
      </p>
      <div className="membar" role="img" aria-label={`${usedPct.toFixed(0)} percent in use`}>
        {top.map((p) => (
          <span key={p.pid} style={{ width: `${(100 * p.ram) / m.total}%` }} title={`${p.title || p.name}: ${bytes(p.ram)}`} />
        ))}
        <span className="rest" style={{ width: `${Math.max(0, usedPct - (100 * top.reduce((a, p) => a + p.ram, 0)) / m.total)}%` }} />
      </div>
      <ol className="memtop">
        {top.map((p) => (
          <li key={p.pid}>
            <span>{p.title || p.name}</span>
            <span>{bytes(p.ram)}</span>
          </li>
        ))}
      </ol>
      <dl className="stats two">
        <Stat label="Committed" value={`${bytes(m.committed)} of ${bytes(m.commitLimit)}`} />
        <Stat label="File cache" value={bytes(m.cached)} />
      </dl>
    </section>
  );
}

function Io({ snap }: { snap: Snapshot }) {
  return (
    <section className="panel" aria-label="Storage and network">
      <h3>Storage and network</h3>
      {snap.disks.map((d) => (
        <div className="io" key={d.name}>
          <p className="io-name">
            {d.name}
            {d.temp != null && <span style={{ color: heat(scale.temp(d.temp)) }}> {d.temp} °C</span>}
          </p>
          <p className="io-rates">
            <span>Read {rate(d.read)}</span>
            <span>Write {rate(d.write)}</span>
            {d.activity != null && <span>{num(d.activity)}% busy</span>}
          </p>
        </div>
      ))}
      {snap.net.slice(0, 2).map((n) => (
        <div className="io" key={n.name}>
          <p className="io-name">{n.name}</p>
          <p className="io-rates">
            <span>Down {rate(n.down)}</span>
            <span>Up {rate(n.up)}</span>
          </p>
        </div>
      ))}
      {snap.disks.length + snap.net.length === 0 && <p className="empty">No storage or network sensors reported.</p>}
    </section>
  );
}

function Heaviest({ snap, onMore }: { snap: Snapshot; onMore: () => void }) {
  // Rank by the share of each resource a process holds, so a GPU-bound game and a RAM-heavy browser both surface.
  const memTotal = snap.memory.total;
  const ranked = [...snap.processes]
    .map((p) => ({ p, score: p.cpu / 100 + p.gpu / 100 + p.ram / memTotal }))
    .sort((a, b) => b.score - a.score)
    .slice(0, 7);
  return (
    <section className="panel wide" aria-label="Heaviest apps">
      <div className="panel-head">
        <h3>Doing the most work right now</h3>
        <button className="link" onClick={onMore}>
          All processes
        </button>
      </div>
      <table className="grid compact">
        <thead>
          <tr>
            <th>App</th>
            <th className="n">CPU</th>
            <th className="n">Memory</th>
            <th className="n">GPU</th>
            <th>On</th>
          </tr>
        </thead>
        <tbody>
          {ranked.map(({ p }) => (
            <tr key={p.pid}>
              <td>
                {p.title || p.name}
                {p.title && <small> {p.name}</small>}
              </td>
              <td className="n" style={{ background: heat(p.cpu / 50, Math.min(0.4, p.cpu / 80)) }}>{num(p.cpu, 1)}%</td>
              <td className="n">{bytes(p.ram)}</td>
              <td className="n" style={{ background: heat(p.gpu / 100, Math.min(0.4, p.gpu / 140)) }}>{p.gpu ? `${num(p.gpu)}%` : "–"}</td>
              <td className="muted">{p.gpu ? `${shortGpu(p.gpuName, snap.cpu.name)} ${p.gpuEngine}` : ""}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}

export default function Overview({ snap, history, onOpenProcesses }: Props) {
  const rows = [...history.values()];
  return (
    <div className="overview">
      {snap.fps && (
        <p className="game" aria-live="polite">
          <span className="game-fps">{Math.round(snap.fps.fps)}</span>
          <span>
            frames per second in <b>{snap.fps.name}</b>
            {snap.fps.low1 != null && <>, 1% low {snap.fps.low1}</>}, {snap.fps.frameTimeMs} ms per frame
          </span>
        </p>
      )}
      <div className="band">
        <CpuInstrument snap={snap} history={history} />
        {snap.gpus.map((g) => (
          <GpuInstrument key={g.id} g={g} snap={snap} history={history} />
        ))}
      </div>

      <section className="ribbon" aria-label="Last five minutes">
        <div className="panel-head">
          <h3>Last five minutes</h3>
          <div className="legend" aria-hidden>
            <span>cool</span>
            <i />
            <span>hot</span>
          </div>
        </div>
        <div className="ribbon-body">
          <ul className="ribbon-labels">
            {rows.map((s) => {
              const v = s.values[s.values.length - 1];
              return (
                <li key={s.key}>
                  <span>{s.label}</span>
                  <b>{v == null ? "–" : `${Math.round(v).toLocaleString()} ${s.unit}`}</b>
                </li>
              );
            })}
          </ul>
          <Ribbon rows={rows} />
        </div>
        <div className="ribbon-axis" aria-hidden>
          <span>5 min ago</span>
          <span>now</span>
        </div>
      </section>

      <div className="trio">
        <Fans snap={snap} />
        <Memory snap={snap} />
        <Io snap={snap} />
      </div>

      <Heaviest snap={snap} onMore={onOpenProcesses} />
    </div>
  );
}
