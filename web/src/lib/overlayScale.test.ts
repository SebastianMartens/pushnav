import { describe, it, expect } from "vitest";
import { overlayScale, scaleAbout } from "./overlayScale";

describe("overlayScale", () => {
  it("is 1 at the 1280 px width the markers were sized for", () => {
    expect(overlayScale(1280)).toBe(1);
  });

  it("grows for the IDS 1936 px frame and shrinks for the 2x2 binned 968 px frame", () => {
    expect(overlayScale(1936)).toBeCloseTo(1.5125, 4);
    expect(overlayScale(968)).toBeCloseTo(0.75625, 5);
  });

  it.each([0, -5, NaN, Infinity])("falls back to 1 for a bad width (%s)", (w) => {
    expect(overlayScale(w)).toBe(1);
  });
});

describe("scaleAbout", () => {
  it("scales around a point", () => {
    expect(scaleAbout(100, 50, 2)).toBe("translate(100 50) scale(2) translate(-100 -50)");
  });
});
