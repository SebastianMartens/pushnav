import { render } from "@testing-library/react";
import { describe, it, expect } from "vitest";
import { StarOverlay } from "./StarOverlay";
import { SyncCandidatesOverlay } from "./SyncCandidatesOverlay";
import { NavOverlay } from "./NavOverlay";
import type { EnginePayload, NavData } from "@/lib/types";

// Markers are sized in image pixels for a 1280 px wide frame; they scale by image_w / 1280
// (spec 5.5, task 4.10) so they keep their on-screen size at 968, 1280 and 1936 px.

function makeState(image_w: number, image_h: number, extra: Partial<EnginePayload> = {}): EnginePayload {
  return {
    state: "TRACKING", failures: 0,
    pointing: { valid: true, ra_deg: 0, dec_deg: 0, roll_deg: 0, matches: 12, prob: 0.001, solve_age_s: 0.1 },
    nav: null,
    origin_x: image_w / 2, origin_y: image_h / 2, image_w, image_h,
    finder_rotation: 0, fov_h_deg: 25.6,
    fov_source: "user", solver_db: "wide", fov_out_of_range: false,
    has_calibration: false, image_size: null,
    controls: [], sync: { in_progress: false, candidates: [], selected_idx: null, error: null },
    stellarium: { active: false, address: null }, lx200: { active: false, address: null },
    webserver: { url: null }, audio_enabled: true,
    camera: { connected: true, all_centroids: null, matched_centroids: null },
    location: { latitude: null, longitude: null, source: null },
    dev_mode: false, min_matches: 8, max_prob: 0.2, sample_active: null,
    server_errors: [],
    ...extra,
  };
}

const SIZES = [
  { w: 968, h: 608, k: 968 / 1280 },
  { w: 1280, h: 720, k: 1 },
  { w: 1936, h: 1216, k: 1936 / 1280 },
];

function svg(node: React.ReactNode) {
  return render(<svg>{node}</svg>).container.querySelector("svg")!;
}

describe.each(SIZES)("overlay marker scaling at $w px", ({ w, h, k }) => {
  it("StarOverlay circles", () => {
    const state = makeState(w, h, {
      camera: {
        connected: true,
        all_centroids: [[100, 100], [200, 300]],
        matched_centroids: [[200, 300]],
      },
    });
    const circles = svg(<StarOverlay state={state} />).querySelectorAll("circle");
    expect(circles).toHaveLength(2);
    // unmatched: r 6, stroke 1; matched: r 8, stroke 2 (at 1280)
    expect(Number(circles[0].getAttribute("r"))).toBeCloseTo(6 * k, 5);
    expect(Number(circles[0].getAttribute("stroke-width"))).toBeCloseTo(1 * k, 5);
    expect(Number(circles[1].getAttribute("r"))).toBeCloseTo(8 * k, 5);
    expect(Number(circles[1].getAttribute("stroke-width"))).toBeCloseTo(2 * k, 5);
  });

  it("SyncCandidatesOverlay rings and labels", () => {
    const state = makeState(w, h, {
      state: "SYNC_CONFIRM",
      sync: {
        in_progress: false, error: null, selected_idx: 0,
        candidates: [
          { idx: 0, pixel_x: 400, pixel_y: 300, ra_deg: 1, dec_deg: 2, magnitude: 3, name: "A" },
          { idx: 1, pixel_x: 500, pixel_y: 350, ra_deg: 1, dec_deg: 2, magnitude: 3, name: "B" },
        ],
      },
    });
    const root = svg(<SyncCandidatesOverlay state={state} />);
    const circles = root.querySelectorAll("circle");
    // per candidate: hit target, ring (and the dot for the selected one)
    const radii = Array.from(circles).map((c) => Number(c.getAttribute("r")));
    expect(radii[1]).toBeCloseTo(26 * k, 5); // selected ring
    expect(radii[0]).toBeCloseTo((26 + 8) * k, 5); // its hit target
    expect(radii[2]).toBeCloseTo(4 * k, 5); // selected dot
    expect(radii[4]).toBeCloseTo(18 * k, 5); // unselected ring
    expect(Number(circles[1].getAttribute("stroke-width"))).toBeCloseTo(3 * k, 5);
    expect(Number(root.querySelector("text")!.getAttribute("font-size"))).toBeCloseTo(20 * k, 5);
  });

  it("NavOverlay converge marker and ring stay the same size on screen", () => {
    const nav: NavData = {
      active: true, target_name: "M42", target_ra_deg: 0, target_dec_deg: 0,
      separation_deg: 2, direction_text: "", in_fov: true,
      pixel_x: w / 2 + 100, pixel_y: h / 2 - 50, camera_angle_deg: 0,
      edge_x: null, edge_y: null, edge_angle_deg: null,
    };
    const root = svg(<NavOverlay state={makeState(w, h, { nav })} />);
    // the target circle sits in a <g transform="scale(k)">
    const targetRing = Array.from(root.querySelectorAll("circle")).find((c) => c.getAttribute("r") === "18")!;
    expect(targetRing.parentElement!.getAttribute("transform")).toBe(`scale(${k})`);
    // the origin reticle is scaled about the origin pixel
    const reticle = root.querySelector('g[stroke="rgba(200, 50, 50, 0.78)"]')!;
    expect(reticle.getAttribute("transform")).toBe(
      `translate(${w / 2} ${h / 2}) scale(${k}) translate(${-w / 2} ${-h / 2})`,
    );
  });

  it("NavOverlay push arrow: tip sits 68 px (scaled) inside the edge point", () => {
    const nav: NavData = {
      active: true, target_name: null, target_ra_deg: 0, target_dec_deg: 0,
      separation_deg: 20, direction_text: "", in_fov: false,
      pixel_x: null, pixel_y: null, camera_angle_deg: 0,
      edge_x: w / 2, edge_y: 68 * k, edge_angle_deg: 0, // straight up, on the inset edge
    };
    const root = svg(<NavOverlay state={makeState(w, h, { nav })} />);
    const comet = root.querySelector("g[transform^='translate(']")!;
    const m = /translate\(([-\d.]+), ([-\d.]+)\) rotate\(0\) scale\(([\d.]+)\)/.exec(
      root.querySelector("g[transform*='rotate(0)']")!.getAttribute("transform")!,
    );
    expect(comet).toBeTruthy();
    expect(m).not.toBeNull();
    expect(Number(m![1])).toBeCloseTo(w / 2, 5);
    expect(Number(m![2])).toBeCloseTo(68 * k + 68 * k, 4); // edge_y + 68 * k, pointing up
    expect(Number(m![3])).toBeCloseTo(k, 5);
  });
});

describe("overlay scaling does not change the 1280 px look", () => {
  it("renders k = 1 markers exactly as before", () => {
    const state = makeState(1280, 720, {
      camera: { connected: true, all_centroids: [[10, 20]], matched_centroids: [] },
    });
    const circle = svg(<StarOverlay state={state} />).querySelector("circle")!;
    expect(circle.getAttribute("r")).toBe("6");
    expect(circle.getAttribute("stroke-width")).toBe("1");
  });
});
