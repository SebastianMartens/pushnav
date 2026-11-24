// Mirror of webserver/_build_payload — keep in sync by hand.

export type EngineState =
  | "SETUP"
  | "SYNC"
  | "SYNC_CONFIRM"
  | "CALIBRATE"
  | "WARMING_UP"
  | "TRACKING"
  | "RECONNECTING"
  | "ERROR";

export interface PointingData {
  valid: boolean;
  ra_deg: number;
  dec_deg: number;
  roll_deg: number;
  matches: number;
  prob: number;
  solve_age_s: number | null;
}

export interface NavData {
  active: boolean;
  target_name: string | null;
  target_ra_deg: number;
  target_dec_deg: number;
  separation_deg: number | null;
  direction_text: string;
  in_fov: boolean;
  pixel_x: number | null;
  pixel_y: number | null;
  camera_angle_deg: number | null;
  edge_x: number | null;
  edge_y: number | null;
  edge_angle_deg: number | null;
}

export interface ControlDescriptor {
  id?: string;     // server uses "id"
  name?: string;   // some payloads use "name"
  label: string;
  min: number;
  max: number;
  step: number;
  cur: number;     // current value — DPG protocol uses "cur"
  value?: number;  // legacy alias kept for back-compat
  unit?: string;
  type?: string;
}

export interface SyncCandidate {
  idx: number;
  name: string;
  ra_deg: number;
  dec_deg: number;
  magnitude: number;
  pixel_x: number;
  pixel_y: number;
}

export interface SyncBlock {
  in_progress: boolean;
  candidates: SyncCandidate[];
  selected_idx: number | null;
  error: string | null;
}

export interface ObserverLocation {
  name?: string;
  country?: string;
  latitude?: number;
  longitude?: number;
}

export interface ActivityLine {
  active: boolean;
  address: string | null;
  status?: unknown;
  object?: { name?: string; "localized-name"?: string } | null;
  location?: ObserverLocation | null;
  /** LX200 only: reported coordinate epoch ("jnow" default, "j2000" for Stellarium Mobile PLUS). */
  epoch?: "jnow" | "j2000";
}

export interface CameraBlock {
  connected: boolean;
  all_centroids: number[][] | null;     // [[y, x], ...]
  matched_centroids: number[][] | null; // [[y, x], ...]
  // fork: camera-controls (spec 6.1, 6.3). null while no camera is connected.
  camera_model?: string | null;
  backend?: string | null;
  camera_key?: string | null;
  /** Physical sensor width from HELLO (C# server only); needed to turn a focal length into a FOV. */
  sensor_width_mm?: number | null;
  /** Focal length behind the current FOV (user or learned), when the sensor size is known. */
  focal_length_mm?: number | null;
  // fork: camera-controls (spec 6.1). Protocol version of the session (1, 2; null: no camera) and,
  // in a v2 session, the frames now arriving; null in a v1 session. Absent from older engines.
  protocol_version?: number | null;
  stream?: import("./cameraControlTypes").CameraStream | null;
}

/** How the FOV was set; "searching" while the FOV search runs (spec 5.4.4), "default" = legacy 8.86°. */
export type FovSource = "user" | "learned" | "searching" | "default";

/** A Stellarium/LX200 server that failed to start (e.g. its port is in use). */
export interface ServerError {
  server: "stellarium" | "lx200";
  port: number;
  message: string;
}

export interface EnginePayload {
  state: EngineState;
  failures: number;
  pointing: PointingData;
  nav: NavData | null;
  origin_x: number;
  origin_y: number;
  image_w: number;
  image_h: number;
  finder_rotation: number;
  fov_h_deg: number;
  fov_source: FovSource;
  /** Loaded solver database ("narrow" / "wide"); null before the solver is loaded. */
  solver_db: string | null;
  /** True when no solver database covers the FOV (Settings shows a hint). */
  fov_out_of_range: boolean;
  has_calibration: boolean;
  image_size: [number, number] | null;
  controls: ControlDescriptor[];
  sync: SyncBlock;
  stellarium: ActivityLine;
  lx200: ActivityLine;
  webserver: { url: string | null };
  audio_enabled: boolean;
  camera: CameraBlock;
  location: {
    latitude: number | null;
    longitude: number | null;
    source: "stellarium" | "manual" | null;
  };
  dev_mode: boolean;
  min_matches: number;
  max_prob: number;
  sample_active: string | null;
  /** Absent from engines that predate this field. */
  server_errors?: ServerError[];
}
