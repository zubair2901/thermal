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
  notes: string[];
}
