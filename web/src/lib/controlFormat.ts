// fork: camera-controls (specs/camera-server/SPEC_CAMERA_CONTROLS.md section 6.2).
// Pure helpers behind CameraControls: value labels per unit, the log slider mapping,
// snapping to the camera's step, and the display order.
import type { ControlDescriptor } from "@/lib/types";

/** A time in microseconds: "850 µs", "12.5 ms", "250 ms", "1.50 s". */
export function formatMicroseconds(us: number): string {
  // Thresholds sit at the rounding points, so 999.7 ms reads "1.00 s", not "1000 ms".
  if (us < 999.5) return `${Math.round(us)} µs`;
  const ms = us / 1000;
  if (ms < 99.95) return `${ms.toFixed(1)} ms`;
  if (ms < 999.5) return `${ms.toFixed(0)} ms`;
  return `${(ms / 1000).toFixed(2)} s`;
}

// The v1 units keep their thresholds exactly as they were before the fork.
function formatMilliseconds(ms: number): string {
  if (ms >= 1000) return `${(ms / 1000).toFixed(2)} s`;
  if (ms >= 100) return `${ms.toFixed(0)} ms`;
  return `${ms.toFixed(1)} ms`;
}

/** Number of decimals a step needs: 0.01 -> 2, 78 -> 0 (at most 6). */
function decimalsOf(step: number | undefined): number {
  if (!step || !Number.isFinite(step) || Number.isInteger(step)) return 0;
  const text = String(step);
  if (text.includes("e-")) return Math.min(6, Number(text.split("e-")[1]));
  return Math.min(6, (text.split(".")[1] ?? "").length);
}

/** Round to the decimals of the step, which drops float noise such as 0.30000000000000004. */
function roundToDecimals(value: number, decimals: number): number {
  const k = 10 ** decimals;
  return Math.round(value * k) / k;
}

/**
 * The label of a control value, by its unit.
 *   "100us"  (Linux/macOS UVC, v1): milliseconds = value * 0.1
 *   "log2s"  (Windows DirectShow, v1): milliseconds = 2^value * 1000
 *   "us"     (v2): microseconds
 *   "fps"    (v2): "4.00 fps (250 ms)"; the frame interval follows from the rate
 * No unit and "raw" (gain): the plain value. Any other unit: the value and the unit.
 * Exposure controls report platform-specific raw units (issue #26); the slider keeps
 * operating on raw min/max/step, only the displayed label is converted.
 */
export function formatControlValue(value: number, unit?: string, step?: number): string {
  if (unit === "100us") return formatMilliseconds(value * 0.1);
  if (unit === "log2s") return formatMilliseconds(Math.pow(2, value) * 1000);
  if (unit === "us") return formatMicroseconds(value);
  if (unit === "fps") {
    const rate = `${value.toFixed(2)} fps`;
    return value > 0 ? `${rate} (${formatMicroseconds(1e6 / value)})` : rate;
  }
  const text = String(step ? roundToDecimals(value, decimalsOf(step)) : value);
  return unit && unit !== "raw" ? `${text} ${unit}` : text;
}

const ORDER = ["exposure", "fps", "gain", "binning"];

export function controlId(control: ControlDescriptor): string {
  return control.id ?? control.name ?? "";
}

/** exposure, fps, gain, binning, then the others by label (the sort is stable). */
export function sortControls(controls: ControlDescriptor[]): ControlDescriptor[] {
  const rank = (c: ControlDescriptor) => {
    const i = ORDER.indexOf(controlId(c));
    return i < 0 ? ORDER.length : i;
  };
  return [...controls].sort((a, b) => {
    const byRank = rank(a) - rank(b);
    return byRank !== 0 ? byRank : (a.label ?? "").localeCompare(b.label ?? "");
  });
}

/** True when the slider should run on log10(value): `scale: "log"` and a positive range. */
export function isLogScale(control: ControlDescriptor): boolean {
  return control.scale === "log" && (control.min ?? 0) > 0 && (control.max ?? 0) > 0;
}

/** Slider positions of a log control: 1000 steps per decade-ish, fine enough for any range. */
export const LOG_SLIDER_STEP = 0.001;

/** Value -> slider position: the value itself, or log10(value) on a log control. */
export function toSliderPosition(control: ControlDescriptor, value: number): number {
  return isLogScale(control) ? Math.log10(Math.max(value, Number.MIN_VALUE)) : value;
}

/** Slider position -> value, not yet snapped. */
export function fromSliderPosition(control: ControlDescriptor, position: number): number {
  return isLogScale(control) ? Math.pow(10, position) : position;
}

/** Slider bounds in slider units. */
export function sliderBounds(control: ControlDescriptor): { min: number; max: number; step: number } {
  const min = control.min ?? 0;
  const max = control.max ?? min;
  if (isLogScale(control)) {
    return { min: Math.log10(min), max: Math.log10(max), step: LOG_SLIDER_STEP };
  }
  return { min, max, step: control.step || 1 };
}

/**
 * The value the camera would take: rounded to its step (counted from `min`), kept inside
 * [min, max], an int for an `int` control. A log slider commits through this.
 */
export function snapToStep(control: ControlDescriptor, value: number): number {
  const min = control.min ?? 0;
  const max = control.max ?? min;
  const step = control.step;
  let v = value;
  if (step && step > 0) v = min + Math.round((v - min) / step) * step;
  v = Math.min(max, Math.max(min, v));
  if (control.type === "float") return roundToDecimals(v, Math.max(decimalsOf(step), 2));
  return Math.round(v);
}
