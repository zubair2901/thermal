import { useEffect, useState } from "react";
import { isNative, listen, send } from "./bridge";
import { bytes } from "./format";
import type { HostInfo, Snapshot } from "./types";

interface SessionFile {
  name: string;
  path: string;
  size: number;
  modified: number;
}

function duration(s: number) {
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const sec = Math.floor(s % 60);
  return h ? `${h}:${String(m).padStart(2, "0")}:${String(sec).padStart(2, "0")}` : `${m}:${String(sec).padStart(2, "0")}`;
}

export default function Sessions({ snap, host }: { snap: Snapshot; host: HostInfo }) {
  const [files, setFiles] = useState<SessionFile[]>([]);
  const rec = snap.recording;

  useEffect(() => {
    const off = listen<{ files: SessionFile[] }>("sessions", (m) => setFiles(m.files));
    send({ type: "sessions" });
    return off;
  }, []);

  return (
    <div className="sessions">
      <div className="toolbar">
        <button className={rec?.active ? "primary stop" : "primary"} onClick={() => send({ type: "record", value: !rec?.active })} disabled={!isNative}>
          {rec?.active ? "Stop recording" : "Start recording"}
        </button>
        {rec?.active && <span className="rec">Recording for {duration(rec.seconds)}</span>}
        <button className="link" onClick={() => send({ type: "open", path: host.sessionsFolder })}>
          Open sessions folder
        </button>
      </div>
      <p className="lead">
        Recording saves one row per second (frame rate, temperatures, clocks, power, fans, memory) to a CSV file you can open in Excel. Start it before a game or
        a long render, stop it after, and compare the numbers below.
      </p>

      {rec && rec.summary.length > 0 ? (
        <section className="panel">
          <h3>{rec.active ? "This session so far" : "Last session"}</h3>
          <table className="grid compact summary">
            <thead>
              <tr>
                <th>Reading</th>
                <th className="n">Lowest</th>
                <th className="n">Average</th>
                <th className="n">Highest</th>
              </tr>
            </thead>
            <tbody>
              {rec.summary.map((m) => (
                <tr key={m.key}>
                  <td>{m.label}</td>
                  <td className="n">
                    {m.min ?? "–"} <small>{m.unit}</small>
                  </td>
                  <td className="n">
                    {m.avg ?? "–"} <small>{m.unit}</small>
                  </td>
                  <td className="n">
                    {m.max ?? "–"} <small>{m.unit}</small>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </section>
      ) : (
        !rec?.active && <p className="empty">No session recorded since Thermal started.</p>
      )}

      <section className="panel">
        <h3>Saved sessions</h3>
        {files.length === 0 ? (
          <p className="empty">Recordings you make appear here.</p>
        ) : (
          <table className="grid compact">
            <tbody>
              {files.map((f) => (
                <tr key={f.path}>
                  <td>{new Date(f.modified).toLocaleString()}</td>
                  <td className="muted">{f.name}</td>
                  <td className="n muted">{bytes(f.size)}</td>
                  <td className="n">
                    <button className="link" onClick={() => send({ type: "open", path: f.path })}>
                      Show in folder
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>
    </div>
  );
}
