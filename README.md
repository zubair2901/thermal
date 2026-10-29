# Thermal

A performance monitor for Windows laptops, built for the HP Victus 15 (Ryzen 7 8845HS, RTX 4060 Laptop, Radeon 780M). It shows what Task Manager leaves out: temperatures, fan speeds, both GPUs side by side, and which app is using which GPU.

![icon](ui/public/icon.png)

## What it shows

- **CPU**: die temperature, total and per-thread load, clock, package power, CPU fan speed
- **Both GPUs**: temperature, hot spot, load per engine (3D, copy, video decode/encode), core clock, power, VRAM or shared memory, GPU fan speed
- **Fans**: CPU and GPU fan RPM, read from the HP BIOS (the generic sensor libraries can't see these fans)
- **Memory**: in use, committed, file cache, and the apps holding the most
- **Processes**: CPU, memory (private working set), GPU load and which GPU/engine, GPU memory, disk I/O, threads; group by app, sort, filter, end task
- **Storage, network, battery**: SSD temperature and throughput, Wi-Fi up/down, charge and charge/discharge rate
- **Last five minutes**: a heat ribbon with one row per sensor
- **All sensors**: every reading LibreHardwareMonitor exposes, with min and max

## Running it

`dist/Thermal.exe` is a single self-contained file (no .NET install needed). It asks for administrator rights when it starts, because the fan readings (HP BIOS WMI) and CPU sensors need them. WebView2 is already part of Windows 11.

### CPU temperature and power

On Ryzen, LibreHardwareMonitor reads the die temperature and package power through the PawnIO driver. Without it, Thermal falls back to the ACPI thermal zone and shows a note. To get exact readings, run this once in an administrator terminal, then restart Thermal:

```
winget install namazso.PawnIO
```

### The "Read RTX sensors" switch

Reading the RTX 4060's sensors through NVAPI keeps it awake. On battery, turn the switch off so the GPU can power down; load and per-app GPU usage still come from Windows' own counters.

## How it's built

| Part | Where | What |
| --- | --- | --- |
| Host | `app/` | .NET 8 WinForms window with WebView2. Serves the UI from embedded resources and posts a JSON snapshot every second. |
| Sensors | `app/Telemetry/Collector.cs` | LibreHardwareMonitorLib for CPU, GPU, storage, network and battery sensors. |
| Fans | `app/Telemetry/HpBios.cs` | HP `hpqBIntM` WMI BIOS calls (command codes from the OmenMon project). |
| Per-app GPU | `app/Telemetry/Counters.cs`, `Dxgi.cs` | "GPU Engine" and "GPU Process Memory" performance counters, mapped to adapter names through DXGI LUIDs. |
| UI | `ui/` | React + TypeScript + Vite. Canvas traces and the heat ribbon are drawn by hand; Archivo variable font (condensed widths for numerals). |

### Build

```
cd ui && npm install && npm run build
cd ../app && dotnet publish -c Release -o ../dist
```

`npm run dev` in `ui/` runs the interface in a browser with synthetic data. To point the desktop window at the dev server, set `THERMAL_DEV_URL=http://localhost:5179`. `THERMAL_DEVTOOLS=1` enables the WebView2 inspector.

`Thermal.exe --dump out.json` takes three readings and writes them to a file, which helps when a sensor is missing. Errors are logged to `%LOCALAPPDATA%\Thermal\error.log`.
