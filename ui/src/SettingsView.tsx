import type { HostInfo, Settings } from "./types";

const ITEMS: { id: string; label: string }[] = [
  { id: "fps", label: "Frame rate" },
  { id: "cpu", label: "CPU" },
  { id: "gpu", label: "Dedicated GPU" },
  { id: "igpu", label: "Integrated GPU" },
  { id: "vram", label: "GPU memory" },
  { id: "fans", label: "Fans" },
  { id: "ram", label: "Memory" },
  { id: "battery", label: "Battery, when unplugged" },
];

const HOTKEYS = ["Ctrl+Shift+O", "Ctrl+Alt+O", "Alt+Shift+O", "Ctrl+Shift+F10", "None"];
const CORNERS: Settings["overlayCorner"][] = ["top-left", "top-right", "bottom-left", "bottom-right"];

function Toggle({ checked, onChange, label, hint }: { checked: boolean; onChange: (v: boolean) => void; label: string; hint?: string }) {
  return (
    <label className="row">
      <input type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)} />
      <span>
        {label}
        {hint && <small>{hint}</small>}
      </span>
    </label>
  );
}

function NumberField({ value, onChange, label, unit, min, max }: { value: number; onChange: (v: number) => void; label: string; unit: string; min: number; max: number }) {
  return (
    <label className="row num">
      <span>{label}</span>
      <input
        type="number"
        min={min}
        max={max}
        value={value}
        onChange={(e) => {
          const v = e.target.valueAsNumber;
          if (!isNaN(v)) onChange(Math.min(max, Math.max(min, v)));
        }}
      />
      <small>{unit}</small>
    </label>
  );
}

export default function SettingsView({ host, update }: { host: HostInfo; update: (p: Partial<Settings>) => void }) {
  const s = host.settings;
  const toggleItem = (id: string, on: boolean) =>
    update({ overlayItems: on ? ITEMS.map((i) => i.id).filter((x) => x === id || s.overlayItems.includes(x)) : s.overlayItems.filter((x) => x !== id) });

  return (
    <div className="settings">
      <section>
        <h3>Game overlay</h3>
        <p className="lead">A small readout drawn on top of games and other windows. Clicks pass through it. It shows over windowed and borderless games; games in exclusive fullscreen hide it.</p>
        <Toggle checked={s.overlayEnabled} onChange={(v) => update({ overlayEnabled: v })} label="Show the overlay" />
        <label className="row">
          <span>Shortcut to show or hide it</span>
          <select value={s.overlayHotkey} onChange={(e) => update({ overlayHotkey: e.target.value })}>
            {HOTKEYS.map((h) => (
              <option key={h}>{h}</option>
            ))}
          </select>
        </label>
        <div className="row">
          <span>Corner</span>
          <div className="seg" role="radiogroup" aria-label="Overlay corner">
            {CORNERS.map((c) => (
              <button key={c} role="radio" aria-checked={s.overlayCorner === c} onClick={() => update({ overlayCorner: c })}>
                {c[0].toUpperCase() + c.slice(1).replace("-", " ")}
              </button>
            ))}
          </div>
        </div>
        <label className="row">
          <span>Size</span>
          <input type="range" min={0.75} max={1.75} step={0.05} value={s.overlayScale} onChange={(e) => update({ overlayScale: e.target.valueAsNumber })} />
          <small>{Math.round(s.overlayScale * 100)}%</small>
        </label>
        <fieldset>
          <legend>Lines to show</legend>
          <div className="items">
            {ITEMS.map((i) => (
              <label key={i.id} className="check">
                <input type="checkbox" checked={s.overlayItems.includes(i.id)} onChange={(e) => toggleItem(i.id, e.target.checked)} /> {i.label}
              </label>
            ))}
          </div>
        </fieldset>
        {!host.fpsAvailable && <p className="warn">Frame rate needs Thermal to run as administrator.</p>}
      </section>

      <section>
        <h3>Desktop widget</h3>
        <p className="lead">A compact panel with temperatures, loads, fans and memory. Drag it anywhere; right-click it for options, double-click to open Thermal.</p>
        <Toggle checked={s.widgetEnabled} onChange={(v) => update({ widgetEnabled: v })} label="Show the widget" />
        <Toggle checked={s.widgetOnTop} onChange={(v) => update({ widgetOnTop: v })} label="Keep it above other windows" />
      </section>

      <section>
        <h3>Alerts</h3>
        <p className="lead">A Windows notification when a temperature stays at or above its limit. It won't repeat until the part has cooled 5 °C below the limit.</p>
        <Toggle checked={s.alertsEnabled} onChange={(v) => update({ alertsEnabled: v })} label="Notify me when the laptop runs hot" />
        <NumberField label="CPU limit" unit="°C" min={60} max={105} value={s.cpuTempAlert} onChange={(v) => update({ cpuTempAlert: v })} />
        <NumberField label="GPU limit" unit="°C" min={60} max={100} value={s.gpuTempAlert} onChange={(v) => update({ gpuTempAlert: v })} />
        <NumberField label="For at least" unit="seconds" min={5} max={600} value={s.alertSeconds} onChange={(v) => update({ alertSeconds: v })} />
      </section>

      <section>
        <h3>App</h3>
        <Toggle checked={s.closeToTray} onChange={(v) => update({ closeToTray: v })} label="Keep running in the tray when the window is closed" hint="Needed for the overlay, widget and alerts to keep working." />
        <Toggle checked={s.startWithWindows} onChange={(v) => update({ startWithWindows: v })} label="Start with Windows" hint="Starts in the tray when you sign in, without an administrator prompt." />
        <Toggle checked={s.trayTemperature} onChange={(v) => update({ trayTemperature: v })} label="Show CPU temperature in the tray icon" />
        <Toggle
          checked={s.pollDgpu}
          onChange={(v) => update({ pollDgpu: v })}
          label="Read the dedicated GPU's temperature and clocks while an app uses it"
          hint="Thermal connects only after a game or app has woken the GPU, and lets go 20 seconds after it's done, so the GPU can still sleep."
        />
      </section>

      <p className="version">Thermal {host.version}</p>
    </div>
  );
}
