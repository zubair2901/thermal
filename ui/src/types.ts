export type Nullable<T> = T | null;

export interface Gpu {
  id: string;
  name: string;
  kind: "discrete" | "integrated";
  load: Nullable<number>;
  temp: Nullable<number>;
  hotspot: Nullable<number>;
  clockCore: Nullable<number>;
  clockMem: Nullable<number>;
  power: Nullable<number>;
  memUsed: Nullable<number>; // bytes
  memTotal: Nullable<number>; // bytes
  memShared: Nullable<number>; // bytes
  fanRpm: Nullable<number>;
  fanPct: Nullable<number>;
  engines: { name: string; load: number }[];
  active: boolean;
}

export interface Fan {
  name: string;
  rpm: Nullable<number>;
  pct: Nullable<number>;
  source: string;
}

export interface Proc {
  pid: number;
  name: string;
  title: string;
  cpu: number; // % of whole machine
  ram: number; // private working set, bytes
  commit: number; // private bytes
  gpu: number; // % summed over its busiest engine
  gpuName: string;
  gpuEngine: string;
  vram: number; // dedicated bytes
  io: number; // bytes/s read+write
  threads: number;
}

export interface Sensor {
  hw: string;
  type: string;
  name: string;
  value: Nullable<number>;
  min: Nullable<number>;
  max: Nullable<number>;
}

export interface Snapshot {
  t: number;
  machine: {
    name: string;
    model: string;
    os: string;
    uptimeSec: number;
    elevated: boolean;
  };
  cpu: {
    name: string;
    load: number;
    cores: number[];
    temp: Nullable<number>;
    tempSource: string;
    clockAvg: Nullable<number>;
    clockMax: Nullable<number>;
    power: Nullable<number>;
  };
  gpus: Gpu[];
  fans: Fan[];
  memory: {
    used: number;
    total: number;
    committed: number;
    commitLimit: number;
    cached: number;
    compressed: Nullable<number>;
  };
  battery: Nullable<{
    charge: number;
    rateW: Nullable<number>;
    onAc: boolean;
    minutesLeft: Nullable<number>;
    wear: Nullable<number>;
  }>;
  disks: { name: string; read: number; write: number; activity: Nullable<number>; temp: Nullable<number> }[];
  net: { name: string; up: number; down: number }[];
  processes: Proc[];
  sensors: Sensor[];
  notes: Note[];
  fps: Nullable<{ pid: number; name: string; fps: number; frameTimeMs: number; low1: Nullable<number> }>;
  recording: Nullable<Recording>;
  power: Nullable<PowerInfo>;
}

export interface AppPower {
  name: string;
  title: string;
  share: number;
  watts: Nullable<number>;
  cpuW: Nullable<number>;
  gpuW: Nullable<number>;
  wh: Nullable<number>;
}

export interface PowerInfo {
  onBattery: boolean;
  drainW: Nullable<number>;
  cpuW: Nullable<number>;
  gpus: { id: string; name: string; kind: string; watts: Nullable<number>; awake: boolean; wakeApps: string[]; wokenByThermal: boolean }[];
  restW: Nullable<number>;
  apps: AppPower[];
  sinceMinutes: number;
  sinceWh: Nullable<number>;
  sinceApps: AppPower[];
  health: Nullable<{ designWh: Nullable<number>; fullWh: Nullable<number>; wearPct: Nullable<number>; cycles: Nullable<number> }>;
}

export interface Note {
  id: string;
  text: string;
  action: Nullable<string>;
}

export interface MetricSummary {
  key: string;
  label: string;
  unit: string;
  min: Nullable<number>;
  avg: Nullable<number>;
  max: Nullable<number>;
}

export interface Recording {
  active: boolean;
  startedAt: number;
  seconds: number;
  path: Nullable<string>;
  summary: MetricSummary[];
}

export interface Settings {
  pollDgpu: boolean;
  overlayEnabled: boolean;
  overlayHotkey: string;
  overlayCorner: "top-left" | "top-right" | "bottom-left" | "bottom-right";
  overlayScale: number;
  overlayItems: string[];
  widgetEnabled: boolean;
  widgetOnTop: boolean;
  alertsEnabled: boolean;
  cpuTempAlert: number;
  gpuTempAlert: number;
  alertSeconds: number;
  closeToTray: boolean;
  startWithWindows: boolean;
  trayTemperature: boolean;
}

export interface HostInfo {
  settings: Settings;
  fpsAvailable: boolean;
  sessionsFolder: string;
  version: string;
}
