import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, it, expect, vi } from "vitest";
import { CameraControls } from "./CameraControls";
import { api } from "@/lib/api";
import type { ControlDescriptor } from "@/lib/types";

// fork: camera-controls. A v1 camera still runs the upstream CameraControls, untouched;
// these pin what it shows, so a change there (or in the hook that picks it) is noticed.
vi.mock("@/lib/api", () => ({
  api: { setControl: vi.fn(() => Promise.resolve()) },
}));

const setControl = vi.mocked(api.setControl);

beforeEach(() => {
  setControl.mockReset();
  setControl.mockResolvedValue(undefined);
});

// What the Linux / macOS / Windows servers send.
const v1Controls: ControlDescriptor[] = [
  { id: "exposure", label: "Exposure", type: "int", unit: "100us", min: 1, max: 15000, step: 1, cur: 2500 },
  { id: "gain", label: "Gain", type: "int", unit: "raw", min: 0, max: 100, step: 1, cur: 40 },
];
const v1Windows: ControlDescriptor[] = [
  { id: "exposure", label: "Exposure", type: "int", unit: "log2s", min: -13, max: 0, step: 1, cur: -3 },
  { id: "gain", label: "Gain", type: "int", unit: "raw", min: 0, max: 100, step: 1, cur: 40 },
];

describe("CameraControls (upstream, v1 camera)", () => {
  it("shows the two controls in their v1 units", () => {
    render(<CameraControls controls={v1Controls} />);
    expect(screen.getByText("Exposure")).toBeInTheDocument();
    expect(screen.getByText("250 ms")).toBeInTheDocument();
    expect(screen.getByText("Gain")).toBeInTheDocument();
    expect(screen.getByText("40")).toBeInTheDocument();
    expect(screen.getAllByRole("slider")).toHaveLength(2);
    expect(screen.queryByRole("status")).not.toBeInTheDocument();
  });

  it("shows the log2s unit of the Windows C server", () => {
    render(<CameraControls controls={v1Windows} />);
    expect(screen.getByText("125 ms")).toBeInTheDocument();
  });

  it("keeps the camera's order", () => {
    render(<CameraControls controls={v1Controls} />);
    const text = document.body.textContent ?? "";
    expect(text.indexOf("Exposure")).toBeLessThan(text.indexOf("Gain"));
  });

  it("commits a linear slider on key release with the raw value", () => {
    render(<CameraControls controls={v1Controls} />);
    const [exposureSlider] = screen.getAllByRole("slider");
    fireEvent.keyDown(exposureSlider, { key: "ArrowRight" });
    expect(setControl).toHaveBeenCalledTimes(1);
    expect(setControl).toHaveBeenCalledWith("exposure", 2501);
  });
});
