import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, it, expect, vi } from "vitest";
import { CameraFov } from "./CameraFov";
import { Settings } from "./Settings";
import { api } from "@/lib/api";
import type { EnginePayload } from "@/lib/types";

vi.mock("@/lib/api", () => ({
  api: {
    setCameraFov: vi.fn(() => Promise.resolve()),
    setSettings: vi.fn(() => Promise.resolve()),
    setAdvanced: vi.fn(() => Promise.resolve()),
  },
}));

const setCameraFov = vi.mocked(api.setCameraFov);

const ids: EnginePayload["camera"] = {
  connected: true, all_centroids: null, matched_centroids: null,
  camera_model: "UI306xCP-M", backend: "windows-dotnet-ids", camera_key: "ids:1409:3060",
  sensor_width_mm: 11.35, focal_length_mm: 25,
};

function makeState(extra: Partial<EnginePayload> = {}, camera: Partial<EnginePayload["camera"]> = {}): EnginePayload {
  return {
    state: "SETUP", failures: 0,
    pointing: { valid: false, ra_deg: 0, dec_deg: 0, roll_deg: 0, matches: 0, prob: 1, solve_age_s: null },
    nav: null,
    origin_x: 968, origin_y: 608, image_w: 1936, image_h: 1216,
    finder_rotation: 0, fov_h_deg: 25.6,
    fov_source: "user", solver_db: "wide", fov_out_of_range: false,
    has_calibration: false, image_size: null,
    controls: [], sync: { in_progress: false, candidates: [], selected_idx: null, error: null },
    stellarium: { active: false, address: null }, lx200: { active: false, address: null },
    webserver: { url: null }, audio_enabled: true,
    camera: { ...ids, ...camera },
    location: { latitude: null, longitude: null, source: null },
    dev_mode: false, min_matches: 8, max_prob: 0.2, sample_active: null,
    server_errors: [],
    ...extra,
  };
}

beforeEach(() => {
  setCameraFov.mockReset();
  setCameraFov.mockResolvedValue(undefined);
});

describe("CameraFov", () => {
  it("shows the camera, the FOV with its source and the active database", () => {
    render(<CameraFov state={makeState()} />);
    expect(screen.getByTestId("camera-name")).toHaveTextContent("UI306xCP-M (windows-dotnet-ids)");
    expect(screen.getByTestId("fov-text")).toHaveTextContent("25.60° (set by you)");
    expect(screen.getByTestId("solver-db")).toHaveTextContent("wide");
  });

  it.each([
    ["user", "25.60° (set by you)"],
    ["learned", "25.60° (learned from the sky)"],
    ["default", "25.60° (default)"],
    ["searching", "Finding field of view…"],
  ] as const)("labels the %s source", (source, text) => {
    render(<CameraFov state={makeState({ fov_source: source })} />);
    expect(screen.getByTestId("fov-text")).toHaveTextContent(text);
  });

  it("says so while the FOV search runs", () => {
    render(<CameraFov state={makeState({ fov_source: "searching", fov_h_deg: 8.86, solver_db: "narrow" },
      { focal_length_mm: null })} />);
    expect(screen.getByTestId("fov-text")).toHaveTextContent("Finding field of view…");
    expect(screen.queryByRole("button", { name: /reset to learned/i })).not.toBeInTheDocument();
  });

  it("shows the focal length behind the FOV", () => {
    render(<CameraFov state={makeState()} />);
    expect(screen.getByLabelText(/focal length/i)).toHaveValue(25);
  });

  it("sends a new focal length when the field is left", () => {
    render(<CameraFov state={makeState({ fov_source: "searching" }, { focal_length_mm: null })} />);
    const input = screen.getByLabelText(/focal length/i);
    fireEvent.change(input, { target: { value: "35" } });
    fireEvent.blur(input);
    expect(setCameraFov).toHaveBeenCalledWith({ focal_length_mm: 35 });
  });

  it("sends nothing for an empty or unchanged field", () => {
    render(<CameraFov state={makeState()} />);
    const input = screen.getByLabelText(/focal length/i);
    fireEvent.blur(input); // unchanged: 25
    fireEvent.change(input, { target: { value: "" } });
    fireEvent.blur(input);
    expect(setCameraFov).not.toHaveBeenCalled();
  });

  it("lets the user turn a learned value into a user value by entering the pre-filled focal length", () => {
    render(<CameraFov state={makeState({ fov_source: "learned" })} />);
    const input = screen.getByLabelText(/focal length/i);
    expect(input).toHaveValue(25);
    fireEvent.blur(input); // unchanged, but the source is "learned"
    expect(setCameraFov).toHaveBeenCalledWith({ focal_length_mm: 25 });
  });

  it("keeps an edit in progress when the search ends and the focal length fills in", () => {
    const { rerender } = render(
      <CameraFov state={makeState({ fov_source: "searching" }, { focal_length_mm: null })} />,
    );
    const input = screen.getByLabelText(/focal length/i);
    fireEvent.change(input, { target: { value: "35" } });
    rerender(<CameraFov state={makeState({ fov_source: "learned", fov_h_deg: 8.82 }, { focal_length_mm: 73.5 })} />);
    expect(screen.getByLabelText(/focal length/i)).toHaveValue(35);
    fireEvent.blur(screen.getByLabelText(/focal length/i));
    expect(setCameraFov).toHaveBeenCalledWith({ focal_length_mm: 35 });
    // after the commit the engine's value is shown again
    expect(screen.getByLabelText(/focal length/i)).toHaveValue(73.5);
  });

  it("is disabled with a hint when the camera does not report its sensor size", () => {
    render(<CameraFov state={makeState({ fov_source: "default", fov_h_deg: 8.86, solver_db: "narrow" },
      { sensor_width_mm: null, focal_length_mm: null, camera_model: "OV9281", backend: "linux-v4l2",
        camera_key: "linux-v4l2:OV9281" })} />);
    expect(screen.getByLabelText(/focal length/i)).toBeDisabled();
    expect(screen.getByText(/does not report its sensor size/i)).toBeInTheDocument();
  });

  it("is disabled without a camera", () => {
    render(<CameraFov state={makeState({ fov_source: "default", fov_h_deg: 8.86, solver_db: "narrow" },
      { connected: false, camera_model: null, backend: null, camera_key: null,
        sensor_width_mm: null, focal_length_mm: null })} />);
    expect(screen.getByTestId("camera-name")).toHaveTextContent("not connected");
    expect(screen.getByLabelText(/focal length/i)).toBeDisabled();
  });

  it("offers Reset to learned only for a user value", () => {
    const { rerender } = render(<CameraFov state={makeState({ fov_source: "user" })} />);
    fireEvent.click(screen.getByRole("button", { name: /reset to learned/i }));
    expect(setCameraFov).toHaveBeenCalledWith({ reset: true });
    rerender(<CameraFov state={makeState({ fov_source: "learned" })} />);
    expect(screen.queryByRole("button", { name: /reset to learned/i })).not.toBeInTheDocument();
  });

  it("shows the out-of-range hint", () => {
    const { rerender } = render(<CameraFov state={makeState()} />);
    expect(screen.queryByText(/no star database covers/i)).not.toBeInTheDocument();
    rerender(<CameraFov state={makeState({ fov_out_of_range: true, fov_h_deg: 14 })} />);
    expect(screen.getByText(/No star database covers this field of view/i)).toBeInTheDocument();
  });

  it("shows the engine's reason when the request is refused", async () => {
    setCameraFov.mockRejectedValue(new Error("POST /api/camera/fov → 400: the sensor size of this camera is unknown"));
    render(<CameraFov state={makeState({}, { focal_length_mm: null })} />);
    const input = screen.getByLabelText(/focal length/i);
    fireEvent.change(input, { target: { value: "35" } });
    fireEvent.blur(input);
    await waitFor(() =>
      expect(screen.getByRole("alert")).toHaveTextContent("the sensor size of this camera is unknown"),
    );
    expect(screen.getByRole("alert")).not.toHaveTextContent("POST");
  });
});

describe("Settings", () => {
  it("includes the camera and field-of-view section", () => {
    render(<Settings state={makeState()} showStars={false} setShowStars={() => {}} />);
    expect(screen.getByTestId("camera-fov")).toBeInTheDocument();
    expect(screen.getByText("Advanced solver")).toBeInTheDocument();
  });
});
