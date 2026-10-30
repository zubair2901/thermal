import { num, shortGpu } from "./format";
import type { Snapshot } from "./types";

function hm(minutes: number) {
  const h = Math.floor(minutes / 60);
  const m = Math.round(minutes % 60);
  return h ? `${h} h ${m} min` : `${m} min`;
}

export default function Battery({ snap, onReadSensors }: { snap: Snapshot; onReadSensors: (v: boolean) => void }) {
  const p = snap.power;
  const bat = snap.battery;
  if (!p) return <p className="empty">Power readings start after the first few seconds.</p>;

  const dgpu = p.gpus.find((g) => g.kind === "discrete");
  const parts = [
    { label: "Processor", w: p.cpuW, cls: "cpu" },
    ...p.gpus.map((g) => ({ label: shortGpu(g.name, snap.cpu.name), w: g.watts, cls: g.kind === "discrete" ? "dgpu" : "igpu" })),
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
        <section className={dgpu.awake && p.onBattery ? "callout hot" : "callout"}>
          {!dgpu.awake ? (
            <p>
              <b>{shortGpu(dgpu.name)} is asleep.</b> Nothing is using it, so it draws no power.
            </p>
          ) : dgpu.wakeApps.length > 0 ? (
            <p>
              <b>{shortGpu(dgpu.name)} is awake</b>
              {dgpu.watts != null && <>, drawing {num(dgpu.watts, 1)} W</>}. These apps are keeping it on: <b>{dgpu.wakeApps.join(", ")}</b>.
              {p.onBattery && " Closing them, or setting them to Power saving in Windows Settings > System > Display > Graphics, lets it power off."}
            </p>
          ) : dgpu.wokenByThermal ? (
            <p>
              <b>Thermal is keeping {shortGpu(dgpu.name)} awake</b> to read its temperature and clocks. No app needs it right now.{" "}
              <button className="link" onClick={() => onReadSensors(false)}>
                Stop reading its sensors
              </button>{" "}
              to let it sleep.
            </p>
          ) : (
            <p>
              <b>{shortGpu(dgpu.name)} is awake</b>
              {dgpu.watts != null && <>, drawing {num(dgpu.watts, 1)} W</>}.
            </p>
          )}
        </section>
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
          <h3>{p.onBattery ? `Since you unplugged, ${hm(p.sinceMinutes)} ago` : "Last time on battery"}</h3>
          {p.sinceWh == null ? (
            <p className="empty">Unplug the charger and Thermal will add up how much energy each app uses.</p>
          ) : (
            <>
              <p className="big">
                {num(p.sinceWh, 1)} <small>Wh used{p.health?.fullWh ? `, ${Math.round((100 * p.sinceWh) / p.health.fullWh)}% of a full charge` : ""}</small>
              </p>
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
