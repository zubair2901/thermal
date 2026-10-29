import { useMemo, useState } from "react";
import { heat, rate, scale } from "./format";
import type { Snapshot } from "./types";

const UNIT: Record<string, string> = {
  Temperature: "°C",
  Load: "%",
  Clock: "MHz",
  Power: "W",
  Voltage: "V",
  Current: "A",
  Fan: "rpm",
  Control: "%",
  Level: "%",
  Data: "GB",
  SmallData: "MB",
  Energy: "mWh",
  TimeSpan: "s",
  Frequency: "Hz",
};

function fmt(type: string, v: number | null) {
  if (v == null) return "–";
  if (type === "Throughput") return rate(v);
  const digits = type === "Voltage" || type === "Current" ? 3 : type === "Power" || type === "Data" ? 1 : 0;
  return `${v.toFixed(digits)} ${UNIT[type] ?? ""}`.trim();
}

function tint(type: string, v: number | null) {
  if (v == null) return undefined;
  if (type === "Temperature") return heat(scale.temp(v));
  if (type === "Load") return heat(scale.pct(v));
  if (type === "Fan") return heat(scale.rpm(v));
  return undefined;
}

export default function Sensors({ snap }: { snap: Snapshot }) {
  const [q, setQ] = useState("");
  const groups = useMemo(() => {
    const needle = q.trim().toLowerCase();
    const m = new Map<string, typeof snap.sensors>();
    for (const s of snap.sensors) {
      if (needle && !`${s.hw} ${s.type} ${s.name}`.toLowerCase().includes(needle)) continue;
      const list = m.get(s.hw) ?? [];
      list.push(s);
      m.set(s.hw, list);
    }
    return [...m.entries()];
  }, [snap.sensors, q]);

  return (
    <section className="sensors" aria-label="All sensors">
      <div className="toolbar">
        <input type="search" placeholder="Filter, for example “temperature” or “fan”" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Filter sensors" />
        <span className="count">{snap.sensors.length} sensors from LibreHardwareMonitor</span>
      </div>
      {groups.length === 0 && <p className="empty">No sensors match “{q}”.</p>}
      <div className="sensor-groups">
        {groups.map(([hw, list]) => (
          <div className="sensor-group" key={hw}>
            <h3>{hw}</h3>
            <table className="grid compact">
              <thead>
                <tr>
                  <th>Sensor</th>
                  <th>Kind</th>
                  <th className="n">Now</th>
                  <th className="n">Lowest</th>
                  <th className="n">Highest</th>
                </tr>
              </thead>
              <tbody>
                {list.map((s) => (
                  <tr key={`${s.type}/${s.name}`}>
                    <td>{s.name}</td>
                    <td className="muted">{s.type === "SmallData" ? "Data" : s.type}</td>
                    <td className="n" style={{ color: tint(s.type, s.value) }}>{fmt(s.type, s.value)}</td>
                    <td className="n muted">{fmt(s.type, s.min)}</td>
                    <td className="n muted">{fmt(s.type, s.max)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ))}
      </div>
    </section>
  );
}
