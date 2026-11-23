import { render, screen } from "@testing-library/react";
import { describe, it, expect } from "vitest";
import { Wizard } from "./Wizard";
import type { EnginePayload } from "@/lib/types";

const base: EnginePayload = {
  state: "SETUP", failures: 0,
  pointing: { valid: false, ra_deg: 0, dec_deg: 0, roll_deg: 0, matches: 0, prob: 1, solve_age_s: null },
  nav: null,
  origin_x: 640, origin_y: 360, image_w: 1280, image_h: 720,
  finder_rotation: 0, fov_h_deg: 8.86,
  fov_source: "default", solver_db: "narrow", fov_out_of_range: false,
  has_calibration: false, image_size: null,
  controls: [], sync: { in_progress: false, candidates: [], selected_idx: null, error: null },
  stellarium: { active: false, address: null }, lx200: { active: false, address: null },
  webserver: { url: null }, audio_enabled: true,
  camera: { connected: false, all_centroids: null, matched_centroids: null },
  location: { latitude: null, longitude: null, source: null },
  dev_mode: false, min_matches: 8, max_prob: 0.2, sample_active: null,
  server_errors: [],
};

// fork: camera-controls (spec 5.5): the IDS reference setup, 1936x1216 at 25.6 deg (25 mm lens)
const wide: EnginePayload = {
  ...base,
  origin_x: 968, origin_y: 608, image_w: 1936, image_h: 1216, fov_h_deg: 25.6,
  fov_source: "user", solver_db: "wide",
};

/** Pixel offset from the image centre of a point `deg` degrees away (tangent-plane projection). */
function offsetPx(imageW: number, fovDeg: number, deg: number): number {
  return Math.tan((deg * Math.PI) / 180) * (imageW / (2 * Math.tan((fovDeg / 2) * (Math.PI / 180))));
}

function trackingWithTarget(fix: EnginePayload, upDeg: number, rightDeg: number): EnginePayload {
  return {
    ...fix,
    state: "TRACKING",
    pointing: { ...fix.pointing, valid: true, solve_age_s: 0.2 },
    nav: {
      active: true, target_name: "M13", target_ra_deg: 0, target_dec_deg: 0,
      separation_deg: Math.hypot(upDeg, rightDeg), direction_text: "", in_fov: true,
      pixel_x: fix.origin_x + offsetPx(fix.image_w, fix.fov_h_deg, rightDeg),
      pixel_y: fix.origin_y - offsetPx(fix.image_w, fix.fov_h_deg, upDeg),
      camera_angle_deg: 0, edge_x: null, edge_y: null, edge_angle_deg: null,
    },
  };
}

describe("Wizard", () => {
  it("renders the setup step in SETUP state", () => {
    render(<Wizard state={{ ...base, state: "SETUP" }} />);
    expect(
      screen.getByText(/Make sure you can see stars/i),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: /^next$/i }),
    ).toBeInTheDocument();
  });

  it("renders the sync step in SYNC state", () => {
    render(<Wizard state={{ ...base, state: "SYNC" }} />);
    expect(
      screen.getByText(/Pick any bright star/i),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: /^next$/i }),
    ).toBeInTheDocument();
  });

  it("offers a camera retry in ERROR state instead of a restart", () => {
    render(<Wizard state={{ ...base, state: "ERROR" }} />);
    expect(screen.getByText(/Camera lost/i)).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: /retry camera/i }),
    ).toBeInTheDocument();
    expect(screen.queryByText(/restart/i)).not.toBeInTheDocument();
  });

  it("tracking step reads the same mount deltas from a 1936x1216 / 25.6 deg frame as from the legacy one", () => {
    // The same target (1.5 deg up, 1.0 deg right of the pointing) lands on very different pixels
    // in the two fixtures; the step must turn both back into the same offset.
    const deltas = (fixture: EnginePayload) => {
      const { container, unmount } = render(<Wizard state={trackingWithTarget(fixture, 1.5, 1.0)} />);
      const m = /Right ([\d.]+)°\s*·\s*Up ([\d.]+)°/.exec(container.textContent ?? "");
      unmount();
      return m && [m[1], m[2]];
    };
    const legacy = deltas(base);
    expect(legacy).not.toBeNull();
    expect(deltas(wide)).toEqual(legacy);
  });

  it("renders LOCK badge when tracking is valid", () => {
    const s = { ...base, state: "TRACKING" as const,
      pointing: { ...base.pointing, valid: true, solve_age_s: 0.2 } };
    render(<Wizard state={s} />);
    expect(screen.getByText(/LOCK/)).toBeInTheDocument();
  });
});
