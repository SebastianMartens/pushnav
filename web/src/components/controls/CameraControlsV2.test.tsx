import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { beforeEach, describe, it, expect, vi } from "vitest";
import { CameraControlsV2 } from "./CameraControlsV2";
import { api } from "@/lib/api";
import type { CameraStream, ControlDescriptorV2 } from "@/lib/cameraControlTypes";

vi.mock("@/lib/api", () => ({
  api: { setControl: vi.fn(() => Promise.resolve()) },
}));

const setControl = vi.mocked(api.setControl);

beforeEach(() => {
  setControl.mockReset();
  setControl.mockResolvedValue(undefined);
});

// v2: IDS-like, deliberately not in display order.
const v2Controls: ControlDescriptorV2[] = [
  { id: "binning", label: "Binning", type: "enum", cur: 1, restarts_stream: true,
    options: [{ value: 1, label: "1x1" }, { value: 2, label: "2x2" }] },
  { id: "gain", label: "Gain", type: "int", unit: "raw", min: 0, max: 100, step: 1, cur: 20 },
  { id: "fps", label: "Frame rate", type: "float", unit: "fps", min: 0.25, max: 10, step: 0.01, cur: 4 },
  { id: "exposure", label: "Exposure", type: "int", unit: "us", min: 92, max: 4_000_000, step: 78,
    cur: 200_000, scale: "log" },
];

const streaming: CameraStream = { width: 1936, height: 1216, binning: 1, state: "streaming", frame_interval_us: 250_000 };
const reconfiguring: CameraStream = { ...streaming, state: "reconfiguring" };

describe("CameraControlsV2", () => {
  it("orders exposure, fps, gain, binning", () => {
    render(<CameraControlsV2 controls={v2Controls} stream={streaming} />);
    const text = document.body.textContent ?? "";
    expect(text.indexOf("Exposure")).toBeLessThan(text.indexOf("Frame rate"));
    expect(text.indexOf("Frame rate")).toBeLessThan(text.indexOf("Gain"));
    expect(text.indexOf("Gain")).toBeLessThan(text.indexOf("Binning"));
  });

  it("labels exposure in real units and fps with its frame interval", () => {
    render(<CameraControlsV2 controls={v2Controls} stream={streaming} />);
    expect(screen.getByText("200 ms")).toBeInTheDocument();
    expect(screen.getByText("4.00 fps (250 ms)")).toBeInTheDocument();
    expect(screen.getByText("20")).toBeInTheDocument();
  });

  it("runs the exposure slider on log10 and commits a value snapped to the step", () => {
    render(<CameraControlsV2 controls={v2Controls} stream={streaming} />);
    const slider = screen.getAllByRole("slider")[0]; // exposure comes first
    // The slider's own range is in decades, not microseconds.
    expect(Number(slider.getAttribute("aria-valuemin"))).toBeCloseTo(Math.log10(92), 3);
    expect(Number(slider.getAttribute("aria-valuemax"))).toBeCloseTo(Math.log10(4_000_000), 3);
    expect(Number(slider.getAttribute("aria-valuenow"))).toBeCloseTo(Math.log10(200_000), 3);

    fireEvent.keyDown(slider, { key: "ArrowRight" });
    expect(setControl).toHaveBeenCalledTimes(1);
    const [id, value] = setControl.mock.calls[0];
    expect(id).toBe("exposure");
    expect(value).toBeGreaterThan(200_000);
    expect(value).toBeLessThan(201_000);
    expect(Number.isInteger(value)).toBe(true);
    expect(((value as number) - 92) % 78).toBe(0);
  });

  it("commits a float for fps", () => {
    render(<CameraControlsV2 controls={v2Controls} stream={streaming} />);
    fireEvent.keyDown(screen.getAllByRole("slider")[1], { key: "ArrowRight" }); // fps is second
    expect(setControl).toHaveBeenCalledWith("fps", 4.01);
  });

  it("shows an enum with 2 to 4 options as buttons and commits the option's value", () => {
    render(<CameraControlsV2 controls={v2Controls} stream={streaming} />);
    const group = screen.getByRole("group", { name: "Binning" });
    const one = within(group).getByRole("button", { name: "1x1" });
    const two = within(group).getByRole("button", { name: "2x2" });
    expect(one).toHaveAttribute("aria-pressed", "true");
    expect(two).toHaveAttribute("aria-pressed", "false");
    fireEvent.click(two);
    expect(setControl).toHaveBeenCalledWith("binning", 2);
    expect(two).toHaveAttribute("aria-pressed", "true");
  });

  it("shows an enum with more than 4 options as a select", () => {
    const fpsEnum: ControlDescriptorV2 = {
      id: "fps", label: "Frame rate", type: "enum", cur: 30,
      options: [5, 10, 15, 20, 30].map((v) => ({ value: v, label: `${v} fps` })),
    };
    render(<CameraControlsV2 controls={[fpsEnum]} stream={streaming} />);
    const select = screen.getByRole("combobox", { name: "Frame rate" });
    expect(select).toHaveValue("30");
    fireEvent.change(select, { target: { value: "15" } });
    expect(setControl).toHaveBeenCalledWith("fps", 15);
  });

  it("follows the next CONTROL_INFO: a new range and value", () => {
    const { rerender } = render(<CameraControlsV2 controls={v2Controls} stream={streaming} />);
    const moved = v2Controls.map((c) => (c.id === "exposure" ? { ...c, max: 1_000_000, cur: 1_000_000 } : c));
    rerender(<CameraControlsV2 controls={moved} stream={streaming} />);
    expect(screen.getByText("1.00 s")).toBeInTheDocument();
    const slider = screen.getAllByRole("slider")[0]; // exposure comes first
    expect(Number(slider.getAttribute("aria-valuemax"))).toBeCloseTo(6, 3);
  });

  it("shows other controls after the known ones, with a unit", () => {
    const extra: ControlDescriptorV2 = {
      id: "contrast", label: "Contrast", type: "float", unit: "x", min: 0, max: 4, step: 0.5, cur: 1.5,
    };
    render(<CameraControlsV2 controls={[extra, ...v2Controls]} stream={streaming} />);
    expect(screen.getByText("1.5 x")).toBeInTheDocument();
    const text = document.body.textContent ?? "";
    expect(text.indexOf("Binning")).toBeLessThan(text.indexOf("Contrast"));
  });
});

describe("CameraControlsV2 while the camera reconfigures", () => {
  it("disables every control and says so", () => {
    render(<CameraControlsV2 controls={v2Controls} stream={reconfiguring} />);
    expect(screen.getByRole("status")).toHaveTextContent("Reconfiguring camera…");
    for (const slider of screen.getAllByRole("slider")) {
      expect(slider).toHaveAttribute("data-disabled");
    }
    for (const button of screen.getAllByRole("button")) {
      expect(button).toBeDisabled();
    }
  });

  it("does not commit from a disabled button", () => {
    render(<CameraControlsV2 controls={v2Controls} stream={reconfiguring} />);
    fireEvent.click(screen.getByRole("button", { name: "2x2" }));
    expect(setControl).not.toHaveBeenCalled();
  });

  it("enables the controls again when the stream is back", () => {
    const { rerender } = render(<CameraControlsV2 controls={v2Controls} stream={reconfiguring} />);
    rerender(<CameraControlsV2 controls={v2Controls} stream={streaming} />);
    expect(screen.queryByRole("status")).not.toBeInTheDocument();
    for (const button of screen.getAllByRole("button")) {
      expect(button).toBeEnabled();
    }
  });
});

describe("CameraControlsV2 when the camera did not take a value", () => {
  it("puts a slider back to the camera's value", async () => {
    setControl.mockRejectedValue(new Error("400"));
    vi.spyOn(console, "error").mockImplementation(() => {});
    render(<CameraControlsV2 controls={v2Controls} stream={streaming} />);
    const gain = screen.getAllByRole("slider")[2];
    fireEvent.keyDown(gain, { key: "ArrowRight" });
    expect(setControl).toHaveBeenCalledWith("gain", 21);
    await waitFor(() => expect(gain).toHaveAttribute("aria-valuenow", "20"));
    expect(screen.getByText("20")).toBeInTheDocument();
  });

  it("puts a button group back to the camera's value", async () => {
    setControl.mockRejectedValue(new Error("400"));
    vi.spyOn(console, "error").mockImplementation(() => {});
    render(<CameraControlsV2 controls={v2Controls} stream={streaming} />);
    fireEvent.click(screen.getByRole("button", { name: "2x2" }));
    await waitFor(() => expect(screen.getByRole("button", { name: "1x1" })).toHaveAttribute("aria-pressed", "true"));
    expect(screen.getByRole("button", { name: "2x2" })).toHaveAttribute("aria-pressed", "false");
  });

  it("resets to the latest value of the camera, not the one from the moment of the commit", async () => {
    let reject: (e: Error) => void = () => {};
    setControl.mockReturnValue(new Promise<void>((_, r) => { reject = r; }));
    vi.spyOn(console, "error").mockImplementation(() => {});
    const { rerender } = render(<CameraControlsV2 controls={v2Controls} stream={streaming} />);
    const gain = screen.getAllByRole("slider")[2];
    fireEvent.keyDown(gain, { key: "ArrowRight" }); // commits 21 against a camera that has 20
    // meanwhile the camera reports another value (a CONTROL_INFO), then the request fails
    rerender(<CameraControlsV2 controls={v2Controls.map((c) => (c.id === "gain" ? { ...c, cur: 35 } : c))} stream={streaming} />);
    reject(new Error("400"));
    await waitFor(() => expect(gain).toHaveAttribute("aria-valuenow", "35"));
  });
});

describe("CameraControlsV2 without a stream", () => {
  it("is not reconfiguring: the controls are enabled and no status shows", () => {
    render(<CameraControlsV2 controls={v2Controls} stream={null} />);
    expect(screen.queryByRole("status")).not.toBeInTheDocument();
    for (const button of screen.getAllByRole("button")) expect(button).toBeEnabled();
  });

  it("renders with the stream prop left out", () => {
    render(<CameraControlsV2 controls={v2Controls} />);
    expect(screen.getAllByRole("slider")).toHaveLength(3);
  });
});
