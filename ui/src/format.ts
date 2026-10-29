// Heat scale: the only colour system in the app. 0 = cool, 1 = hot.
const STOPS: [number, [number, number, number]][] = [
  [0.0, [64, 120, 168]],
  [0.35, [79, 163, 217]],
  [0.62, [240, 176, 63]],
  [0.82, [236, 112, 54]],
  [1.0, [229, 72, 77]],
];

export function heat(x: number, alpha = 1): string {
  const v = Math.max(0, Math.min(1, x));
  for (let i = 1; i < STOPS.length; i++) {
    const [p1, c1] = STOPS[i];
    if (v <= p1) {
      const [p0, c0] = STOPS[i - 1];
      const k = (v - p0) / (p1 - p0);
      const c = c0.map((a, j) => Math.round(a + (c1[j] - a) * k));
      return `rgba(${c[0]},${c[1]},${c[2]},${alpha})`;
    }
  }
  return `rgba(229,72,77,${alpha})`;
}

// Map a reading to 0..1 on the scale that matters for that kind of sensor.
export const scale = {
  temp: (c: number | null) => (c == null ? 0 : (c - 35) / (95 - 35)),
  pct: (p: number | null) => (p == null ? 0 : p / 100),
  rpm: (r: number | null, max = 6000) => (r == null ? 0 : r / max),
};

export function bytes(n: number | null | undefined, digits = 1): string {
  if (n == null || !isFinite(n)) return "–";
  const u = ["B", "KB", "MB", "GB", "TB"];
  let i = 0;
  let v = n;
  while (Math.abs(v) >= 1024 && i < u.length - 1) {
    v /= 1024;
    i++;
  }
  return `${v.toFixed(i < 2 ? 0 : digits)} ${u[i]}`;
}

export function rate(n: number | null | undefined): string {
  if (n == null) return "–";
  if (n < 1024) return "0 KB/s";
  return `${bytes(n)}/s`;
}

export function num(n: number | null | undefined, digits = 0): string {
  return n == null || !isFinite(n) ? "–" : n.toFixed(digits);
}

export function uptime(s: number): string {
  const d = Math.floor(s / 86400);
  const h = Math.floor((s % 86400) / 3600);
  const m = Math.floor((s % 3600) / 60);
  return d ? `${d}d ${h}h ${m}m` : `${h}h ${m}m`;
}

export function shortGpu(name: string): string {
  return name
    .replace(/NVIDIA GeForce /i, "")
    .replace(/ Laptop GPU/i, "")
    .replace(/AMD Radeon\(TM\) Graphics/i, "Radeon 780M")
    .replace(/\(TM\)|\(R\)/g, "");
}

export function shortCpu(name: string): string {
  return name.replace(/ w\/.*$/i, "").replace(/\(TM\)|\(R\)/g, "").replace(/^AMD /, "");
}
