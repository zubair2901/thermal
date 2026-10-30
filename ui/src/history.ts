import { useEffect, useRef, useState } from "react";
import { subscribe } from "./bridge";
import { shortGpu } from "./format";
import type { Snapshot } from "./types";

export const WINDOW = 300; // seconds kept in memory and shown in the ribbon

export interface Series {
  key: string;
  label: string;
  unit: string;
  norm: (v: number | null) => number; // 0..1 heat position
  values: (number | null)[];
}

function extract(s: Snapshot): { key: string; label: string; unit: string; kind: "temp" | "pct" | "rpm"; v: number | null }[] {
  const out: { key: string; label: string; unit: string; kind: "temp" | "pct" | "rpm"; v: number | null }[] = [];
  out.push({ key: "cpu.temp", label: "CPU temperature", unit: "°C", kind: "temp", v: s.cpu.temp });
  out.push({ key: "cpu.load", label: "CPU load", unit: "%", kind: "pct", v: s.cpu.load });
  s.gpus.forEach((g) => {
    out.push({ key: `${g.id}.temp`, label: `${shortGpu(g.name, s.cpu.name)} temperature`, unit: "°C", kind: "temp", v: g.temp });
    out.push({ key: `${g.id}.load`, label: `${shortGpu(g.name, s.cpu.name)} load`, unit: "%", kind: "pct", v: g.load });
  });
  s.fans.forEach((f, i) => out.push({ key: `fan${i}`, label: f.name, unit: "rpm", kind: "rpm", v: f.rpm }));
  out.push({ key: "mem", label: "Memory in use", unit: "%", kind: "pct", v: (100 * s.memory.used) / s.memory.total });
  return out;
}

const norms = {
  temp: (v: number | null) => (v == null ? 0 : (v - 35) / 60),
  pct: (v: number | null) => (v == null ? 0 : v / 100),
  rpm: (v: number | null) => (v == null ? 0 : v / 6000),
};

export function useTelemetry() {
  const [snap, setSnap] = useState<Snapshot | null>(null);
  const hist = useRef(new Map<string, Series>());
  const [, tick] = useState(0);

  useEffect(
    () =>
      subscribe((s) => {
        const seen = new Set<string>();
        for (const e of extract(s)) {
          seen.add(e.key);
          let ser = hist.current.get(e.key);
          if (!ser) {
            ser = { key: e.key, label: e.label, unit: e.unit, norm: norms[e.kind], values: [] };
            hist.current.set(e.key, ser);
          }
          ser.values.push(e.v);
          if (ser.values.length > WINDOW) ser.values.shift();
        }
        // Keep series that briefly vanish (a GPU powering down) aligned in time.
        for (const [k, ser] of hist.current) {
          if (!seen.has(k)) {
            ser.values.push(null);
            if (ser.values.length > WINDOW) ser.values.shift();
          }
        }
        setSnap(s);
        tick((n) => n + 1);
      }),
    [],
  );

  return { snap, history: hist.current };
}
