import type { Snapshot } from "./types";

type Listener = (s: Snapshot) => void;

interface WebView {
  addEventListener(type: "message", cb: (e: { data: unknown }) => void): void;
  postMessage(msg: unknown): void;
}

const webview: WebView | undefined = (window as unknown as { chrome?: { webview?: WebView } }).chrome?.webview;

export const isNative = !!webview;

export function send(msg: { type: string; [k: string]: unknown }) {
  webview?.postMessage(msg);
}

const listeners = new Map<string, Set<(msg: unknown) => void>>();
webview?.addEventListener("message", (e) => {
  const d = e.data as { type?: string };
  if (d?.type) listeners.get(d.type)?.forEach((cb) => cb(d));
});

/** Subscribe to a message type from the host. Returns an unsubscribe function. */
export function listen<T>(type: string, cb: (msg: T) => void): () => void {
  const set = listeners.get(type) ?? new Set();
  set.add(cb as (msg: unknown) => void);
  listeners.set(type, set);
  return () => set.delete(cb as (msg: unknown) => void);
}

export function subscribe(cb: Listener): () => void {
  if (webview) {
    const off = listen<{ snapshot: Snapshot }>("snapshot", (d) => cb(d.snapshot));
    send({ type: "ready" });
    return off;
  }
  // Running in a plain browser (vite dev): feed synthetic data so the UI can be worked on.
  const id = window.setInterval(() => cb(mock()), 1000);
  cb(mock());
  return () => window.clearInterval(id);
}

let phase = 0;
const GB = 1024 ** 3;
const names = ["chrome.exe", "Code.exe", "explorer.exe", "Discord.exe", "steam.exe", "msedgewebview2.exe", "node.exe", "Spotify.exe", "dwm.exe", "svchost.exe", "MsMpEng.exe", "cyberpunk2077.exe"];

function wave(p: number, base: number, amp: number) {
  return Math.round(Math.min(base + amp, base + amp * (0.5 + 0.5 * Math.sin(phase / p)) + Math.random() * amp * 0.15));
}

function mock(): Snapshot {
  phase++;
  const cores = Array.from({ length: 16 }, (_, i) => Math.min(100, wave(5 + i, 5, 60)));
  const load = cores.reduce((a, b) => a + b, 0) / cores.length;
  return {
    t: Date.now(),
    machine: { name: "VICTUS", model: "Victus by HP Gaming Laptop 15-fb2xxx", os: "Windows 11 Home 26300", uptimeSec: 18342 + phase, elevated: true },
    cpu: { name: "AMD Ryzen 7 8845HS w/ Radeon 780M Graphics", load, cores, temp: wave(9, 52, 38), tempSource: "Tctl/Tdie", clockAvg: wave(7, 2900, 1800), clockMax: 5100, power: wave(8, 12, 40) },
    gpus: [
      { id: "nv", name: "NVIDIA GeForce RTX 4060 Laptop GPU", kind: "discrete", load: wave(11, 0, 95), temp: wave(13, 45, 35), hotspot: wave(13, 52, 38), clockCore: wave(11, 210, 2100), clockMem: 8000, power: wave(11, 4, 100), memUsed: wave(20, 0.5, 5) * GB, memTotal: 8 * GB, memShared: 0.2 * GB, fanRpm: null, fanPct: null, engines: [{ name: "3D", load: wave(11, 0, 95) }, { name: "Copy", load: 2 }, { name: "Video Decode", load: 0 }], active: true },
      { id: "amd", name: "AMD Radeon(TM) Graphics", kind: "integrated", load: wave(6, 3, 30), temp: wave(9, 50, 30), hotspot: null, clockCore: wave(6, 400, 2200), clockMem: null, power: wave(6, 1, 8), memUsed: 0.4 * GB, memTotal: 0.5 * GB, memShared: 1.4 * GB, fanRpm: null, fanPct: null, engines: [{ name: "3D", load: wave(6, 3, 30) }, { name: "Video Decode", load: 4 }], active: true },
    ],
    fans: [
      { name: "CPU fan", rpm: Math.round(wave(9, 2000, 3500) / 100) * 100, pct: null, source: "HP BIOS" },
      { name: "GPU fan", rpm: Math.round(wave(12, 1800, 3800) / 100) * 100, pct: null, source: "HP BIOS" },
    ],
    memory: { used: wave(30, 14, 6) * GB, total: 31.3 * GB, committed: 24 * GB, commitLimit: 36 * GB, cached: 6.2 * GB, compressed: 0.8 * GB },
    battery: { charge: 87, rateW: 0, onAc: true, minutesLeft: null, wear: 6.4 },
    disks: [{ name: "WD PC SN5000S 1TB", read: wave(4, 0, 80e6), write: wave(5, 0, 30e6), activity: wave(4, 0, 40), temp: wave(30, 38, 8) }],
    net: [{ name: "Wi-Fi", up: wave(3, 0, 2e6), down: wave(3, 0, 12e6) }],
    processes: names.map((n, i) => ({
      pid: 1000 + i * 37,
      name: n,
      title: "",
      cpu: wave(3 + i, 0, 24 / (i + 1)),
      ram: (2.4 / (i + 1)) * GB,
      commit: (2.6 / (i + 1)) * GB,
      gpu: i === 11 ? wave(11, 0, 95) : i === 0 ? wave(5, 0, 6) : 0,
      gpuName: i === 11 ? "NVIDIA GeForce RTX 4060 Laptop GPU" : i === 0 ? "AMD Radeon(TM) Graphics" : "",
      gpuEngine: i === 11 || i === 0 ? "3D" : "",
      vram: i === 11 ? 4.1 * GB : 0,
      io: wave(4 + i, 0, 1e6 / (i + 1)),
      threads: 20 + i * 7,
    })),
    sensors: [
      { hw: "AMD Ryzen 7 8845HS", type: "Temperature", name: "Core (Tctl/Tdie)", value: 64, min: 41, max: 92 },
      { hw: "AMD Ryzen 7 8845HS", type: "Power", name: "Package", value: 31.4, min: 4, max: 54 },
      { hw: "NVIDIA GeForce RTX 4060 Laptop GPU", type: "Clock", name: "GPU Core", value: 2010, min: 210, max: 2370 },
    ],
    notes: [{ id: "pawnio", text: "CPU die temperature and package power need the PawnIO driver, a small signed driver that sensor tools use to read the processor. Without it the temperature shown is an estimate.", action: "installPawnIO" }],
    fps: phase % 40 < 25 ? { pid: 4412, name: "cyberpunk2077", fps: wave(4, 88, 40), frameTimeMs: 8.4, low1: 71 } : null,
    recording: null,
    power: {
      onBattery: true,
      drainW: 24.6,
      cpuW: 9.8,
      gpus: [
        { id: "nv", name: "NVIDIA GeForce RTX 4060 Laptop GPU", kind: "discrete", watts: 6.1, awake: true, wakeApps: ["steamwebhelper", "Discord"], wokenByThermal: false },
        { id: "amd", name: "AMD Radeon(TM) Graphics", kind: "integrated", watts: 3.2, awake: true, wakeApps: [], wokenByThermal: false },
      ],
      restW: 5.5,
      apps: [
        { name: "chrome", title: "Google Chrome", share: 0.31, watts: 5.9, cpuW: 4.1, gpuW: 1.8, wh: null },
        { name: "Code", title: "Visual Studio Code", share: 0.17, watts: 3.2, cpuW: 3.2, gpuW: 0, wh: null },
        { name: "steamwebhelper", title: "Steam Client WebHelper", share: 0.14, watts: 2.7, cpuW: 0.4, gpuW: 2.3, wh: null },
        { name: "Discord", title: "Discord", share: 0.09, watts: 1.7, cpuW: 0.6, gpuW: 1.1, wh: null },
        { name: "MsMpEng", title: "Antimalware Service Executable", share: 0.05, watts: 1.0, cpuW: 1.0, gpuW: 0, wh: null },
      ],
      sinceMinutes: 47,
      sinceWh: 18.4,
      sinceApps: [
        { name: "chrome", title: "Google Chrome", share: 0.28, watts: null, cpuW: null, gpuW: null, wh: 5.2 },
        { name: "Code", title: "Visual Studio Code", share: 0.15, watts: null, cpuW: null, gpuW: null, wh: 2.8 },
        { name: "steamwebhelper", title: "Steam Client WebHelper", share: 0.12, watts: null, cpuW: null, gpuW: null, wh: 2.2 },
      ],
      health: { designWh: 70, fullWh: 65.5, wearPct: 6.4, cycles: 112 },
    },
  };
}
