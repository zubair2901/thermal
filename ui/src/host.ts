import { useCallback, useEffect, useState } from "react";
import { isNative, listen, send } from "./bridge";
import type { HostInfo, Settings } from "./types";

const PREVIEW: HostInfo = {
  settings: {
    pollDgpu: true,
    overlayEnabled: false,
    overlayHotkey: "Ctrl+Shift+O",
    overlayCorner: "top-left",
    overlayScale: 1,
    overlayItems: ["fps", "cpu", "gpu", "vram", "fans", "ram"],
    widgetEnabled: false,
    widgetOnTop: true,
    alertsEnabled: true,
    cpuTempAlert: 95,
    gpuTempAlert: 87,
    alertSeconds: 30,
    closeToTray: true,
    startWithWindows: false,
    trayTemperature: true,
  },
  fpsAvailable: true,
  sessionsFolder: "C:\\Users\\you\\Documents\\Thermal sessions",
  version: "preview",
};

/** Settings live in the host (they drive native windows); the UI reads and edits them through messages. */
export function useHost(): [HostInfo | null, (patch: Partial<Settings>) => void] {
  const [host, setHost] = useState<HostInfo | null>(isNative ? null : PREVIEW);

  useEffect(() => listen<HostInfo>("settings", (h) => setHost(h)), []);

  const update = useCallback((patch: Partial<Settings>) => {
    setHost((h) => {
      if (!h) return h;
      const settings = { ...h.settings, ...patch };
      send({ type: "settings", settings });
      return { ...h, settings };
    });
  }, []);

  return [host, update];
}
