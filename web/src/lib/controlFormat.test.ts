import { describe, it, expect } from "vitest";
import {
  formatControlValue,
  formatMicroseconds,
  fromSliderPosition,
  isLogScale,
  sliderBounds,
  snapToStep,
  sortControls,
  toSliderPosition,
} from "./controlFormat";
import type { ControlDescriptor } from "./types";

const exposure: ControlDescriptor = {
  id: "exposure", label: "Exposure", type: "int", unit: "us",
  min: 92, max: 4_000_000, step: 78, cur: 200_000, scale: "log",
};
const fps: ControlDescriptor = {
  id: "fps", label: "Frame rate", type: "float", unit: "fps", min: 0.25, max: 10, step: 0.01, cur: 4,
};

describe("formatMicroseconds", () => {
  it.each([
    [92, "92 µs"],
    [850, "850 µs"],
    [999, "999 µs"],
    [999.6, "1.0 ms"],
    [99_960, "100 ms"],
    [999_974, "1.00 s"],
    [1000, "1.0 ms"],
    [12_500, "12.5 ms"],
    [250_000, "250 ms"],
    [1_500_000, "1.50 s"],
    [4_000_000, "4.00 s"],
  ])("%d us -> %s", (us, label) => {
    expect(formatMicroseconds(us)).toBe(label);
  });
});

describe("formatControlValue", () => {
  it("shows a v2 exposure in microseconds, milliseconds or seconds", () => {
    expect(formatControlValue(850, "us")).toBe("850 µs");
    expect(formatControlValue(12_500, "us")).toBe("12.5 ms");
    expect(formatControlValue(1_500_000, "us")).toBe("1.50 s");
  });

  it("shows fps with two decimals and the frame interval", () => {
    expect(formatControlValue(4, "fps")).toBe("4.00 fps (250 ms)");
    expect(formatControlValue(0.25, "fps")).toBe("0.25 fps (4.00 s)");
    expect(formatControlValue(25, "fps")).toBe("25.00 fps (40.0 ms)");
  });

  // The units of the v1 servers keep their labels.
  it.each([
    [15000, "100us", "1.50 s"],
    [2500, "100us", "250 ms"],
    [100, "100us", "10.0 ms"],
    [0, "log2s", "1.00 s"],
    [-3, "log2s", "125 ms"],
    [-6, "log2s", "15.6 ms"],
  ])("v1 %d in %s -> %s", (value, unit, label) => {
    expect(formatControlValue(value, unit)).toBe(label);
  });

  it("shows gain and unit-less values as the plain number", () => {
    expect(formatControlValue(40, "raw")).toBe("40");
    expect(formatControlValue(40)).toBe("40");
    expect(formatControlValue(0.3000000004, undefined, 0.1)).toBe("0.3");
  });

  it("shows any other unit after the value", () => {
    expect(formatControlValue(6, "dB", 0.1)).toBe("6 dB");
    expect(formatControlValue(2.5, "x", 0.5)).toBe("2.5 x");
  });
});

describe("sortControls", () => {
  const ctl = (id: string, label: string): ControlDescriptor => ({ id, label, cur: 0 });

  it("puts exposure, fps, gain, binning first and the others by label", () => {
    const sorted = sortControls([
      ctl("gamma", "Gamma"), ctl("binning", "Binning"), ctl("contrast", "Contrast"),
      ctl("gain", "Gain"), ctl("fps", "Frame rate"), ctl("exposure", "Exposure"),
    ]);
    expect(sorted.map((c) => c.id)).toEqual(["exposure", "fps", "gain", "binning", "contrast", "gamma"]);
  });

  it("keeps the v1 order of exposure and gain, and does not modify its input", () => {
    const v1 = [ctl("exposure", "Exposure"), ctl("gain", "Gain")];
    expect(sortControls(v1).map((c) => c.id)).toEqual(["exposure", "gain"]);
    const mixed = [ctl("gain", "Gain"), ctl("exposure", "Exposure")];
    sortControls(mixed);
    expect(mixed.map((c) => c.id)).toEqual(["gain", "exposure"]);
  });
});

describe("log slider mapping", () => {
  it("runs on log10(value) for a log control with a positive range", () => {
    expect(isLogScale(exposure)).toBe(true);
    const { min, max } = sliderBounds(exposure);
    expect(min).toBeCloseTo(Math.log10(92));
    expect(max).toBeCloseTo(Math.log10(4_000_000));
    expect(toSliderPosition(exposure, 1000)).toBeCloseTo(3);
    expect(fromSliderPosition(exposure, 3)).toBeCloseTo(1000);
  });

  it("round-trips a value", () => {
    const back = fromSliderPosition(exposure, toSliderPosition(exposure, 200_000));
    expect(back).toBeCloseTo(200_000, 3);
  });

  it("falls back to a linear slider without a positive range or without scale: log", () => {
    expect(isLogScale({ ...exposure, min: 0 })).toBe(false);
    expect(isLogScale({ ...exposure, scale: "linear" })).toBe(false);
    expect(isLogScale({ ...exposure, scale: undefined })).toBe(false);
    expect(sliderBounds({ ...exposure, scale: undefined })).toEqual({ min: 92, max: 4_000_000, step: 78 });
    expect(toSliderPosition({ ...exposure, scale: undefined }, 5000)).toBe(5000);
  });
});

describe("snapToStep", () => {
  it("rounds to the step counted from min", () => {
    expect(snapToStep(exposure, 200_000)).toBe(92 + 78 * Math.round((200_000 - 92) / 78));
    expect((snapToStep(exposure, 123_456) - 92) % 78).toBe(0);
  });

  it("keeps the value inside [min, max]", () => {
    expect(snapToStep(exposure, 3)).toBe(92);
    expect(snapToStep(exposure, 9_000_000)).toBeLessThanOrEqual(4_000_000);
    expect(snapToStep(fps, 99)).toBeLessThanOrEqual(10);
  });

  it("gives an int control an int and a float control a clean float", () => {
    expect(Number.isInteger(snapToStep(exposure, 1234.5))).toBe(true);
    expect(snapToStep(fps, 3.456789)).toBe(3.46);
    expect(snapToStep(fps, 0.1)).toBe(0.25);
  });
});
