import { useEffect, useRef } from "react";
import { heat } from "./format";
import { WINDOW, type Series } from "./history";

function useCanvas(draw: (ctx: CanvasRenderingContext2D, w: number, h: number) => void) {
  const ref = useRef<HTMLCanvasElement>(null);
  useEffect(() => {
    const c = ref.current;
    if (!c) return;
    const dpr = window.devicePixelRatio || 1;
    const w = c.clientWidth;
    const h = c.clientHeight;
    if (c.width !== w * dpr || c.height !== h * dpr) {
      c.width = w * dpr;
      c.height = h * dpr;
    }
    const ctx = c.getContext("2d")!;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, w, h);
    draw(ctx, w, h);
  });
  return ref;
}

/** Line trace whose colour at each point is that point's own heat. */
export function Trace({ series, points = 90, height = 44 }: { series?: Series; points?: number; height?: number }) {
  const vals = series?.values.slice(-points) ?? [];
  const ref = useCanvas(
    (ctx, w, h) => {
      if (!series || vals.length < 2) return;
      const step = w / (points - 1);
      const x0 = w - (vals.length - 1) * step;
      const y = (v: number) => h - 2 - Math.max(0, Math.min(1, series.norm(v))) * (h - 4);
      ctx.lineWidth = 1.5;
      ctx.lineJoin = "round";
      for (let i = 1; i < vals.length; i++) {
        const a = vals[i - 1];
        const b = vals[i];
        if (a == null || b == null) continue;
        const xa = x0 + (i - 1) * step;
        const xb = x0 + i * step;
        ctx.fillStyle = heat(series.norm(b), 0.14);
        ctx.beginPath();
        ctx.moveTo(xa, h);
        ctx.lineTo(xa, y(a));
        ctx.lineTo(xb, y(b));
        ctx.lineTo(xb, h);
        ctx.fill();
        ctx.strokeStyle = heat(series.norm(b));
        ctx.beginPath();
        ctx.moveTo(xa, y(a));
        ctx.lineTo(xb, y(b));
        ctx.stroke();
      }
    });
  return <canvas ref={ref} className="trace" style={{ height }} aria-hidden />;
}

/** Five-minute thermal ribbon: one row per series, one cell per second. */
export function Ribbon({ rows }: { rows: Series[] }) {
  const ref = useCanvas(
    (ctx, w, h) => {
      const rh = h / rows.length;
      const cw = w / WINDOW;
      rows.forEach((s, r) => {
        const off = WINDOW - s.values.length;
        s.values.forEach((v, i) => {
          if (v == null) return;
          ctx.fillStyle = heat(s.norm(v));
          ctx.fillRect((off + i) * cw, r * rh + 1, Math.ceil(cw), rh - 2);
        });
      });
      // minute ticks
      ctx.fillStyle = "rgba(22,27,38,0.9)";
      for (let m = 1; m < WINDOW / 60; m++) ctx.fillRect(w - m * 60 * cw, 0, 1, h);
    });
  return <canvas ref={ref} className="ribbon-canvas" style={{ height: rows.length * 22 }} aria-hidden />;
}
