import { useEffect, useMemo, useState } from "react";
import { isNative, listen, send } from "./bridge";
import { bytes, heat, num, rate, shortGpu as gpuShort } from "./format";
import type { Proc, Snapshot } from "./types";

type Key = "name" | "cpu" | "ram" | "gpu" | "vram" | "io" | "threads";

interface Row {
  id: string;
  name: string;
  exe: string;
  pids: number[];
  cpu: number;
  ram: number;
  gpu: number;
  gpuWhere: string;
  vram: number;
  io: number;
  threads: number;
}

function group(procs: Proc[], byApp: boolean, cpuName: string): Row[] {
  const shortGpu = (n: string) => gpuShort(n, cpuName);
  if (!byApp)
    return procs.map((p) => ({
      id: String(p.pid),
      name: p.title || p.name,
      exe: `${p.name} (${p.pid})`,
      pids: [p.pid],
      cpu: p.cpu,
      ram: p.ram,
      gpu: p.gpu,
      gpuWhere: p.gpu ? `${shortGpu(p.gpuName)} ${p.gpuEngine}` : "",
      vram: p.vram,
      io: p.io,
      threads: p.threads,
    }));
  const m = new Map<string, Row>();
  for (const p of procs) {
    const k = p.name.toLowerCase();
    const r = m.get(k);
    if (!r) {
      m.set(k, {
        id: k,
        name: p.title || p.name,
        exe: p.name,
        pids: [p.pid],
        cpu: p.cpu,
        ram: p.ram,
        gpu: p.gpu,
        gpuWhere: p.gpu ? `${shortGpu(p.gpuName)} ${p.gpuEngine}` : "",
        vram: p.vram,
        io: p.io,
        threads: p.threads,
      });
    } else {
      r.pids.push(p.pid);
      r.cpu += p.cpu;
      r.ram += p.ram;
      if (p.gpu > r.gpu) {
        r.gpu = p.gpu;
        r.gpuWhere = `${shortGpu(p.gpuName)} ${p.gpuEngine}`;
      }
      r.vram += p.vram;
      r.io += p.io;
      r.threads += p.threads;
      if (!r.name && p.title) r.name = p.title;
    }
  }
  return [...m.values()];
}

const COLS: { key: Key; label: string; numeric: boolean }[] = [
  { key: "name", label: "Name", numeric: false },
  { key: "cpu", label: "CPU", numeric: true },
  { key: "ram", label: "Memory", numeric: true },
  { key: "gpu", label: "GPU", numeric: true },
  { key: "vram", label: "GPU memory", numeric: true },
  { key: "io", label: "Disk I/O", numeric: true },
  { key: "threads", label: "Threads", numeric: true },
];

export default function Processes({ snap }: { snap: Snapshot }) {
  const [q, setQ] = useState("");
  const [byApp, setByApp] = useState(true);
  const [sort, setSort] = useState<{ key: Key; desc: boolean }>({ key: "cpu", desc: true });
  const [selected, setSelected] = useState<string | null>(null);
  const [confirm, setConfirm] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [frozen, setFrozen] = useState<Proc[] | null>(null);

  useEffect(
    () =>
      listen<{ pid: number; error: string | null }>("killResult", (r) => {
        setMessage(r.error ? `Could not end process ${r.pid}: ${r.error}` : `Ended process ${r.pid}.`);
      }),
    [],
  );

  const procs = frozen ?? snap.processes;
  const rows = useMemo(() => {
    const needle = q.trim().toLowerCase();
    const list = group(procs, byApp, snap.cpu.name).filter((r) => !needle || r.name.toLowerCase().includes(needle) || r.exe.toLowerCase().includes(needle));
    const dir = sort.desc ? -1 : 1;
    return list.sort((a, b) => {
      const x = a[sort.key];
      const y = b[sort.key];
      return typeof x === "string" ? dir * x.localeCompare(y as string) : dir * ((x as number) - (y as number));
    });
  }, [procs, byApp, q, sort, snap.cpu.name]);

  const totals = useMemo(
    () => ({
      cpu: snap.cpu.load,
      ram: (100 * snap.memory.used) / snap.memory.total,
      gpu: Math.max(0, ...snap.gpus.map((g) => g.load ?? 0)),
    }),
    [snap],
  );

  const sel = rows.find((r) => r.id === selected) ?? null;

  function endSelected() {
    if (!sel) return;
    sel.pids.forEach((pid) => send({ type: "kill", pid }));
    setConfirm(false);
    setSelected(null);
  }

  const memTotal = snap.memory.total;

  return (
    <section className="procs" aria-label="Processes">
      <div className="toolbar">
        <input type="search" placeholder="Filter by name" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Filter processes" />
        <label className="check">
          <input type="checkbox" checked={byApp} onChange={(e) => setByApp(e.target.checked)} /> Group by app
        </label>
        <label className="check">
          <input type="checkbox" checked={frozen != null} onChange={(e) => setFrozen(e.target.checked ? snap.processes : null)} /> Pause updates
        </label>
        <span className="count">{rows.length} {byApp ? "apps" : "processes"}</span>
        {sel && isNative && (
          <span className="danger-zone">
            {confirm ? (
              <>
                End {sel.name}
                {sel.pids.length > 1 ? ` (${sel.pids.length} processes)` : ""}? Unsaved work will be lost.
                <button className="danger" onClick={endSelected}>
                  End task
                </button>
                <button onClick={() => setConfirm(false)}>Keep running</button>
              </>
            ) : (
              <button className="danger" onClick={() => setConfirm(true)}>
                End task
              </button>
            )}
          </span>
        )}
      </div>
      {message && (
        <p className="toast" role="status" onClick={() => setMessage(null)}>
          {message}
        </p>
      )}
      <div className="table-wrap">
        <table className="grid">
          <thead>
            <tr>
              {COLS.map((c) => (
                <th
                  key={c.key}
                  className={c.numeric ? "n" : undefined}
                  aria-sort={sort.key === c.key ? (sort.desc ? "descending" : "ascending") : undefined}
                >
                  <button onClick={() => setSort((s) => ({ key: c.key, desc: s.key === c.key ? !s.desc : c.numeric }))}>
                    {c.label}
                    {c.key === "cpu" && <small>{num(totals.cpu)}%</small>}
                    {c.key === "ram" && <small>{num(totals.ram)}%</small>}
                    {c.key === "gpu" && <small>{num(totals.gpu)}%</small>}
                  </button>
                </th>
              ))}
              <th>GPU in use</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((r) => (
              <tr
                key={r.id}
                className={r.id === selected ? "sel" : undefined}
                onClick={() => {
                  setSelected(r.id === selected ? null : r.id);
                  setConfirm(false);
                }}
              >
                <td>
                  {r.name}
                  {byApp && r.pids.length > 1 ? (
                    <small> {r.name === r.exe ? `${r.pids.length} processes` : `${r.exe}, ${r.pids.length} processes`}</small>
                  ) : (
                    r.name !== r.exe && <small> {r.exe}</small>
                  )}
                </td>
                <td className="n" style={{ background: heat(r.cpu / 50, Math.min(0.5, r.cpu / 50)) }}>{num(r.cpu, 1)}%</td>
                <td className="n" style={{ background: heat((8 * r.ram) / memTotal, Math.min(0.5, (6 * r.ram) / memTotal)) }}>{bytes(r.ram)}</td>
                <td className="n" style={{ background: heat(r.gpu / 100, Math.min(0.5, r.gpu / 100)) }}>{r.gpu ? `${num(r.gpu, 1)}%` : "–"}</td>
                <td className="n">{r.vram ? bytes(r.vram) : "–"}</td>
                <td className="n">{r.io > 1024 ? rate(r.io) : "–"}</td>
                <td className="n">{r.threads}</td>
                <td className="muted">{r.gpuWhere}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}
