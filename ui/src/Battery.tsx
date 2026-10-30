import { num, shortGpu } from "./format";
import GpuWake from "./GpuWake";
import type { Snapshot } from "./types";

function hm(minutes: number) {
  const h = Math.floor(minutes / 60);
  const m = Math.round(minutes % 60);
  return h ? `${h} h ${m} min` : `${m} min`;
}

export default function Battery({ snap, reading, onReadSensors }: { snap: Snapshot; reading: boolean; onReadSensors: (v: boolean) => void }) {
  const p = snap.power;
  const bat = snap.battery;
  if (!p) return <p className="empty">Power readings start after the first few seconds.</p>;

  const dgpu = p.gpus.find((g) => g.kind === "discrete");
  const igpu = p.gpus.find((g) => g.kind === "integrated");
  const u = p.unplugged;
  const parts = [
    // The integrated GPU is on the CPU die, so the processor's package power already includes it.
    { label: igpu ? `Processor and ${shortGpu(igpu.name, snap.cpu.name)}` : "Processor", w: p.cpuW, cls: "cpu" },
    ...p.gpus.filter((g) => g.kind === "discrete").map((g) => ({ label: shortGpu(g.name, snap.cpu.name), w: g.watts, cls: "dgpu" })),
    { label: "Screen, Wi-Fi, storage and the rest", w: p.restW, cls: "rest" },
  ].filter((x) => x.w != null && x.w > 0.05) as { label: string; w: number; cls: string }[];
  const total = p.drainW ?? parts.reduce((a, x) => a + x.w, 0);
  const maxApp = Math.max(0.001, ...p.apps.map((a) => a.watts ?? a.share));
  const noWatts = p.cpuW == null;

  return (
    <div className="battery">
      <section className="drain">
        {p.onBattery && p.drainW != null ? (
          <>
            <p className="drain-n">
              {num(p.drainW, 1)}
              <small> W</small>
            </p>
            <p className="drain-l">
              drawn from the battery right now
              {bat?.minutesLeft != null && <>. At this rate {bat.charge}% lasts about {hm(bat.minutesLeft)}.</>}
            </p>
          </>
        ) : (
          <>
            <p className="drain-n small">{p.onBattery ? "On battery" : "Plugged in"}</p>
            <p className="drain-l">
              {p.onBattery
                ? "Windows hasn't reported a drain rate yet."
                : "The battery isn't draining, but the breakdown below still shows which parts and apps use the most power."}
            </p>
          </>
        )}
      </section>

      {parts.length > 0 && (
        <section className="panel">
          <h3>Where the power goes</h3>
          <div className="split" role="img" aria-label="Power by component">
            {parts.map((x) => (
              <span key={x.label} className={x.cls} style={{ flexGrow: x.w }} title={`${x.label}: ${num(x.w, 1)} W`} />
            ))}
          </div>
          <ul className="split-legend">
            {parts.map((x) => (
              <li key={x.label}>
                <i className={x.cls} />
                {x.label}
                <b>{num(x.w, 1)} W</b>
                <small>{Math.round((100 * x.w) / Math.max(total, 0.001))}%</small>
              </li>
            ))}
          </ul>
          {p.restW == null && p.onBattery && <p className="muted">The rest of the laptop (screen, Wi-Fi, storage) shows once Windows reports the drain rate.</p>}
        </section>
      )}

      {dgpu && (
        <GpuWake gpu={dgpu} igpuName={igpu ? shortGpu(igpu.name, snap.cpu.name) : "integrated GPU"} onBattery={p.onBattery} reading={reading} onReadSensors={onReadSensors} />
      )}

      <div className="battery-cols">
        <section className="panel">
          <h3>Apps using the most power now</h3>
          {noWatts && <p className="muted">Watts per app need CPU package power, which comes from the PawnIO driver. Until then apps are ranked by their share.</p>}
          <table className="grid compact">
            <thead>
              <tr>
                <th>App</th>
                <th className="n">{noWatts ? "Share" : "Estimate"}</th>
                <th className="n">CPU</th>
                <th className="n">GPU</th>
              </tr>
            </thead>
            <tbody>
              {p.apps.map((a) => (
                <tr key={a.name}>
                  <td className="app-cell">
                    <span className="app-bar" style={{ width: `${(100 * (a.watts ?? a.share)) / maxApp}%` }} />
                    <span className="app-name">{a.title || a.name}</span>
                  </td>
                  <td className="n">{a.watts != null ? `${num(a.watts, 1)} W` : `${Math.round(a.share * 100)}%`}</td>
                  <td className="n muted">{a.cpuW ? `${num(a.cpuW, 1)} W` : "–"}</td>
                  <td className="n muted">{a.gpuW ? `${num(a.gpuW, 1)} W` : "–"}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </section>

        <section className="panel">
          {u ? (
            <>
              <h3>
                Unplugged at {new Date(u.at).toLocaleTimeString([], { hour: "numeric", minute: "2-digit" })}, {hm((Date.now() - u.at) / 60000)} ago
              </h3>
              <p className="big">
                {num(u.usedWh, 1)}{" "}
                <small>
                  Wh used of the {num(u.chargeAtWh, 1)} Wh it had
                  {p.health?.fullWh ? `, ${Math.round((100 * u.usedWh) / p.health.fullWh)}% of a full charge` : ""}
                </small>
              </p>
              <dl className="stats two">
                <div className="stat">
                  <dt>Awake</dt>
                  <dd>
                    {hm(u.awakeMinutes)}, {num(u.awakeWh, 1)} Wh
                    {u.awakeMinutes > 5 && <small> avg {num(u.awakeWh / (u.awakeMinutes / 60), 1)} W</small>}
                  </dd>
                </div>
                <div className="stat">
                  <dt>Asleep</dt>
                  <dd>
                    {hm(u.asleepMinutes)}, {num(u.asleepWh, 1)} Wh
                    {u.asleepMinutes > 5 && <small> avg {num(u.asleepWh / (u.asleepMinutes / 60), 1)} W</small>}
                  </dd>
                </div>
              </dl>
            </>
          ) : (
            <h3>{p.onBattery ? "Since you unplugged" : "On battery"}</h3>
          )}
          {!p.onBattery && <p className="empty">Unplug the charger and this shows when you unplugged, how much charge you have used, and which apps used it.</p>}
          {p.onBattery && !u && <p className="muted">Reading the battery history Windows keeps…</p>}
          {p.sinceWh != null && p.sinceApps.length > 0 && (
            <>
              <h4 className="sub-h">By app, while Thermal was running ({hm(p.sinceMinutes)})</h4>
              <ol className="memtop">
                {p.sinceApps.map((a) => (
                  <li key={a.name}>
                    <span>{a.title || a.name}</span>
                    <span>
                      {num(a.wh, 1)} Wh <small>{Math.round(a.share * 100)}%</small>
                    </span>
                  </li>
                ))}
              </ol>
            </>
          )}
        </section>
      </div>

      {p.health && (
        <section className="panel">
          <h3>Battery health</h3>
          <dl className="stats health">
            <div className="stat">
              <dt>Holds when full</dt>
              <dd>{num(p.health.fullWh, 1)} Wh</dd>
            </div>
            <div className="stat">
              <dt>Designed to hold</dt>
              <dd>{num(p.health.designWh, 1)} Wh</dd>
            </div>
            <div className="stat">
              <dt>Capacity lost to wear</dt>
              <dd>{p.health.wearPct != null ? `${p.health.wearPct}%` : "–"}</dd>
            </div>
            <div className="stat">
              <dt>Charge cycles</dt>
              <dd>{p.health.cycles ?? "Not reported"}</dd>
            </div>
          </dl>
        </section>
      )}

      <p className="footnote">
        Windows doesn't measure power per app. Thermal splits the measured CPU and GPU power between apps by how much processor and graphics time each one uses,
        so treat app figures as estimates.
      </p>
    </div>
  );
}
