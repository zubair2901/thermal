import { useEffect, useState } from "react";
import { isNative, listen, send } from "./bridge";
import { num, shortGpu } from "./format";
import type { PowerInfo, WakeApp } from "./types";

type Gpu = PowerInfo["gpus"][number];
type Done = "gpuPref" | "restart" | "close";
type Result = { done?: Done; error?: string };

function WakeRow({ app, igpuName, result }: { app: WakeApp; igpuName: string; result?: Result }) {
  const [confirm, setConfirm] = useState<"restart" | "close" | null>(null);
  const label = app.title || app.name;
  const set = app.pref === 1 || result?.done === "gpuPref";
  // Store apps are set by app ID; if Windows didn't give us one, fall back to its Settings page.
  const canSet = !!app.path && !app.nvidia && (!app.packaged || !!app.appId);

  let status = "";
  if (result?.error) status = result.error;
  else if (result?.done === "restart") status = `Restarted. It now opens on the ${igpuName}.`;
  else if (result?.done === "close") status = "Closed.";
  else if (app.nvidia) status = "Part of the NVIDIA App. Turning off its in-game overlay (NVIDIA App > Settings > Features) lets the GPU sleep.";
  else if (set) status = `Set to power saving. It moves to the ${igpuName} the next time it starts.`;
  else if (!canSet) status = "Windows didn't report this app's ID. Add it under Microsoft Store apps in Windows graphics settings.";

  return (
    <li className="wake">
      <div className="wake-name">
        {label}
        {app.title && app.title.toLowerCase() !== app.name.toLowerCase() && <small> {app.name}</small>}
        {status && <p className={result?.error ? "wake-status err" : "wake-status"}>{status}</p>}
      </div>
      <div className="wake-actions">
        {confirm ? (
          <>
            <span className="muted">
              {confirm === "restart" ? "Restart" : "Close"} {label}? Unsaved work may be lost.
            </span>
            <button
              className="danger"
              onClick={() => {
                send(confirm === "restart" ? { type: "restartApp", name: app.name, path: app.path, appId: app.appId } : { type: "closeApp", name: app.name });
                setConfirm(null);
              }}
            >
              {confirm === "restart" ? "Restart" : "Close"}
            </button>
            <button onClick={() => setConfirm(null)}>Cancel</button>
          </>
        ) : (
          <>
            {canSet && !set && (
              <button
                className="primary"
                disabled={!isNative}
                onClick={() => send({ type: "gpuPref", name: app.name, path: app.path, appId: app.appId, value: 1 })}
              >
                Use power-saving GPU
              </button>
            )}
            {!canSet && !app.nvidia && <button onClick={() => send({ type: "graphicsSettings" })}>Open graphics settings</button>}
            {canSet && set && result?.done !== "restart" && (
              <button className="primary" disabled={!isNative} onClick={() => setConfirm("restart")}>
                Restart it now
              </button>
            )}
            <button disabled={!isNative} onClick={() => setConfirm("close")}>
              Close
            </button>
          </>
        )}
      </div>
    </li>
  );
}

/** Which apps keep the dedicated GPU awake, with a fix for each one. */
export default function GpuWake({
  gpu,
  igpuName,
  onBattery,
  onReadSensors,
}: {
  gpu: Gpu;
  igpuName: string;
  onBattery: boolean;
  onReadSensors: (v: boolean) => void;
}) {
  const [results, setResults] = useState<Record<string, Result>>({});
  useEffect(
    () =>
      listen<{ name: string; action: Done; error: string | null }>("appAction", (r) =>
        setResults((s) => ({ ...s, [r.name]: r.error ? { error: r.error } : { done: r.action } })),
      ),
    [],
  );
  const name = shortGpu(gpu.name);
  const draw = gpu.watts != null ? `, drawing ${num(gpu.watts, 1)} W` : "";

  if (!gpu.awake)
    return (
      <section className="callout">
        <p>
          <b>{name} is asleep.</b> Nothing is using it, so it draws no power.
        </p>
      </section>
    );

  if (gpu.wakeApps.length === 0)
    return (
      <section className="callout">
        {gpu.wokenByThermal ? (
          <p>
            <b>Thermal is keeping {name} awake</b> to read its temperature and clocks. No app needs it right now.{" "}
            <button className="link" onClick={() => onReadSensors(false)}>
              Stop reading its sensors
            </button>{" "}
            to let it sleep.
          </p>
        ) : (
          <p>
            <b>
              {name} is awake{draw}.
            </b>{" "}
            {gpu.onlyWindows && "Windows itself is using it, not an app, so there is nothing to change here."}
          </p>
        )}
      </section>
    );

  return (
    <section className={onBattery ? "callout hot" : "callout"}>
      <p>
        <b>
          {name} is awake{draw}.
        </b>{" "}
        {gpu.wakeApps.length === 1 ? "This app is" : `These ${gpu.wakeApps.length} apps are`} keeping it on. Move each one to the power-saving {igpuName}, then
        restart it. Once none are left, {name} powers off.
      </p>
      <ul className="wakes">
        {gpu.wakeApps.map((a) => (
          <WakeRow key={a.name} app={a} igpuName={igpuName} result={results[a.name]} />
        ))}
      </ul>
      <p className="muted">
        Games and 3D apps run slower on the {igpuName}. To undo a change, set the app back to High performance in{" "}
        <button className="link" onClick={() => send({ type: "graphicsSettings" })}>
          Windows graphics settings
        </button>
        .
      </p>
    </section>
  );
}
